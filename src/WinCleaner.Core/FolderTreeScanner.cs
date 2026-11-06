using System.Collections.Concurrent;
using System.Diagnostics;

namespace WinCleaner.Core;

public readonly record struct TreeProgress(long Files, long Bytes, int DeniedFolders, string CurrentFolder);

public sealed class FolderTreeResult
{
    public FolderNode Root { get; init; } = null!;
    public long Files { get; init; }
    public long Bytes { get; init; }
    public int DeniedFolders { get; init; }
    public TimeSpan Elapsed { get; init; }
    public bool Cancelled { get; init; }
}

/// <summary>
/// Maps how much space every folder under a root uses. Read-only. Never follows junctions or symlinks
/// and ignores cloud-only placeholders (they use no local space).
/// </summary>
public sealed class FolderTreeScanner
{
    public async Task<FolderTreeResult> ScanAsync(string root, IProgress<TreeProgress>? progress, CancellationToken ct)
    {
        var path = PathUtil.Normalize(root);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException($"Folder not found: {path}");

        var run = new Run(path, ct);
        var work = Task.Run(run.Execute, CancellationToken.None);
        while (!work.IsCompleted)
        {
            progress?.Report(run.Snapshot());
            await Task.WhenAny(work, Task.Delay(100));
        }
        var cancelled = await work;
        progress?.Report(run.Snapshot());
        return run.ToResult(cancelled);
    }

    private sealed class Run
    {
        private readonly FolderNode _root;
        private readonly string _rootPath;
        private readonly CancellationToken _ct;
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private long _files, _bytes;
        private int _denied;
        private string _current = "";

        public Run(string rootPath, CancellationToken ct)
        {
            _rootPath = rootPath;
            _root = new FolderNode(rootPath, null);
            _ct = ct;
        }

        public TreeProgress Snapshot() =>
            new(Interlocked.Read(ref _files), Interlocked.Read(ref _bytes), Volatile.Read(ref _denied), _current);

        public FolderTreeResult ToResult(bool cancelled) => new()
        {
            Root = _root,
            Files = _files,
            Bytes = _bytes,
            DeniedFolders = _denied,
            Elapsed = _sw.Elapsed,
            Cancelled = cancelled,
        };

        /// <returns>true when cancelled.</returns>
        public bool Execute()
        {
            try
            {
                Walk();
                Finish(_root);
                return false;
            }
            catch (OperationCanceledException) { return true; }
            catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is OperationCanceledException)) { return true; }
        }

        private void Walk()
        {
            using var queue = new BlockingCollection<(FolderNode Node, string Path)>();
            int pending = 0;
            void Enqueue(FolderNode node, string path)
            {
                Interlocked.Increment(ref pending);
                queue.Add((node, path));
            }
            Enqueue(_root, _rootPath);

            int n = Math.Clamp(Environment.ProcessorCount * 2, 4, 16);
            var tasks = Enumerable.Range(0, n).Select(_ => Task.Factory.StartNew(() =>
            {
                foreach (var (node, path) in queue.GetConsumingEnumerable(_ct))
                {
                    try { ProcessDir(node, path, Enqueue); }
                    catch (OperationCanceledException) { throw; }
                    catch { node.Denied = true; }
                    finally { if (Interlocked.Decrement(ref pending) == 0) queue.CompleteAdding(); }
                }
            }, _ct, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            Task.WaitAll(tasks);
        }

        private void ProcessDir(FolderNode node, string path, Action<FolderNode, string> enqueue)
        {
            _ct.ThrowIfCancellationRequested();
            _current = path;

            List<FsEntry> entries;
            try { entries = FsUtil.Enumerate(path); }
            catch (UnauthorizedAccessException) { node.Denied = true; Interlocked.Increment(ref _denied); return; }
            catch (IOException) { node.Denied = true; return; }

            var files = new List<FileEntry>();
            long size = 0;
            foreach (var e in entries)
            {
                if (e.IsReparsePoint || e.IsCloudOnly) continue;
                if (e.IsDirectory)
                {
                    // Only this worker touches node.Children; the child is queued for another worker afterwards.
                    var child = new FolderNode(e.Name, node);
                    node.Children.Add(child);
                    enqueue(child, PathUtil.Combine(path, e.Name));
                }
                else
                {
                    size += e.Length;
                    files.Add(new FileEntry(e.Name, e.Length, e.LastWriteUtc));
                }
            }

            files.Sort((a, b) => b.Size.CompareTo(a.Size));
            node.OwnFilesSize = size;
            node.OwnFileCount = files.Count;
            if (files.Count > FolderNode.MaxFilesKept)
            {
                var rest = files.GetRange(FolderNode.MaxFilesKept, files.Count - FolderNode.MaxFilesKept);
                node.OtherFileCount = rest.Count;
                node.OtherFilesSize = rest.Sum(f => f.Size);
                files.RemoveRange(FolderNode.MaxFilesKept, rest.Count);
            }
            node.Files = files.ToArray();

            Interlocked.Add(ref _files, node.OwnFileCount);
            Interlocked.Add(ref _bytes, size);
        }

        /// <summary>Totals each folder from the bottom up (iteratively, so very deep trees cannot overflow the stack).</summary>
        private static void Finish(FolderNode root)
        {
            var order = new List<FolderNode>();
            var stack = new Stack<FolderNode>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                order.Add(n);
                foreach (var c in n.Children) stack.Push(c);
            }
            for (int i = order.Count - 1; i >= 0; i--)   // children always come after their parent in this order
            {
                var n = order[i];
                long size = n.OwnFilesSize, files = n.OwnFileCount;
                foreach (var c in n.Children) { size += c.Size; files += c.FileCount; }
                n.Size = size;
                n.FileCount = files;
                n.Children.Sort((a, b) => b.Size.CompareTo(a.Size));
            }
        }
    }
}
