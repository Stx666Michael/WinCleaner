using System.Collections.Concurrent;
using System.Diagnostics;

namespace WinCleaner.Core;

/// <summary>Read-only. Walks the chosen locations and reports what could be cleaned; deletes nothing.</summary>
public sealed class Scanner
{
    private readonly Rules _rules;

    public Scanner(Rules rules) => _rules = rules;

    public async Task<ScanResult> ScanAsync(ScanOptions options, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var run = new ScanRun(_rules, options, ct);
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

    private sealed class ScanRun
    {
        private static readonly HashSet<string> SkipDirNames = new(StringComparer.OrdinalIgnoreCase)
            { "$Recycle.Bin", "System Volume Information" };

        private static readonly HashSet<string> InstallerExts = new(StringComparer.OrdinalIgnoreCase)
            { ".exe", ".msi", ".msix", ".zip", ".7z", ".rar", ".iso", ".cab", ".img" };

        private readonly Rules _rules;
        private readonly ScanOptions _options;
        private readonly CancellationToken _ct;
        private readonly ConcurrentBag<Finding> _findings = new();
        private readonly HashSet<string> _claimed = new(StringComparer.OrdinalIgnoreCase);
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private long _files, _bytes, _foundBytes;
        private int _denied, _count;
        private string _current = "";

        public ScanRun(Rules rules, ScanOptions options, CancellationToken ct)
        {
            _rules = rules; _options = options; _ct = ct;
        }

        public ScanProgress Snapshot() => new(
            Interlocked.Read(ref _files), Interlocked.Read(ref _bytes), Interlocked.Read(ref _foundBytes),
            Volatile.Read(ref _count), Volatile.Read(ref _denied), _current);

        public ScanResult ToResult(bool cancelled) => new()
        {
            Findings = _findings.OrderByDescending(f => f.Size).ToList(),
            DeniedFolders = _denied,
            FilesScanned = _files,
            BytesScanned = _bytes,
            Elapsed = _sw.Elapsed,
            Cancelled = cancelled,
        };

        private void Add(Finding f)
        {
            _findings.Add(f);
            Interlocked.Add(ref _foundBytes, f.Tier == RiskTier.Locked ? 0 : f.Size);
            Interlocked.Increment(ref _count);
        }

        /// <returns>true when the scan was cancelled.</returns>
        public bool Execute()
        {
            try
            {
                var roots = NormalizeRoots(_options.Roots);
                if (_options.IncludeKnownLocations)
                {
                    foreach (var k in _rules.KnownLocations)
                        if (roots.Any(r => PathUtil.IsSameOrUnder(k.Path, r)))
                            ScanKnownLocation(k);
                    AddRecycleBins(roots);
                }
                Walk(roots);
                return false;
            }
            catch (OperationCanceledException) { return true; }
            catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is OperationCanceledException)) { return true; }
        }

        private static List<string> NormalizeRoots(IEnumerable<string> input)
        {
            var all = new List<string>();
            foreach (var r in input)
            {
                try
                {
                    var n = PathUtil.Normalize(r);
                    if (Directory.Exists(n)) all.Add(n);
                }
                catch (ArgumentException) { }
                catch (NotSupportedException) { }
            }
            // Drop duplicates and roots nested inside another root so nothing is scanned twice.
            return all.Distinct(StringComparer.OrdinalIgnoreCase)
                      .Where(a => !all.Any(b => PathUtil.IsUnder(a, b)))
                      .ToList();
        }

        // ---- Known disposable locations ----------------------------------------------------------------

        private void ScanKnownLocation(KnownLocation k)
        {
            _ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(k.Path)) return;
            var info = new DirectoryInfo(k.Path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) return;
            // Skip if any part of the path is a link: we could end up cleaning somewhere else.
            var final = PathUtil.ResolveFinal(k.Path);
            if (final is null || !string.Equals(final, k.Path, StringComparison.OrdinalIgnoreCase)) return;

            _claimed.Add(k.Path);
            var suffix = k.NeedsAdmin ? " (needs administrator)" : "";

            if (!k.PerChild)
            {
                var m = FsUtil.Measure(k.Path, _ct, OnDenied);
                AddMeasured(m);
                if (m.Bytes <= 0) return;
                Add(new Finding(k.Path, true, m.Bytes, ToLocal(m.NewestUtc, info.LastWriteTimeUtc), info.LastWriteTimeUtc,
                    k.Category, RiskTier.Safe, "Rebuilt automatically when needed" + suffix, ContentsOnly: true, Label: k.Label, NeedsAdmin: k.NeedsAdmin));
                return;
            }

            List<FsEntry> children;
            try { children = FsUtil.Enumerate(k.Path); }
            catch (UnauthorizedAccessException) { OnDenied(); return; }
            catch (IOException) { return; }

            Parallel.ForEach(children, new ParallelOptions { CancellationToken = _ct, MaxDegreeOfParallelism = 4 }, child =>
            {
                if (child.IsReparsePoint || child.IsCloudOnly) return;
                var path = PathUtil.Combine(k.Path, child.Name);
                long size; DateTime newest;
                if (child.IsDirectory)
                {
                    var m = FsUtil.Measure(path, _ct, OnDenied);
                    AddMeasured(m);
                    size = m.Bytes;
                    newest = m.Files > 0 ? m.NewestUtc : child.LastWriteUtc;
                }
                else
                {
                    if ((child.Attributes & FileAttributes.System) != 0) return;
                    size = child.Length; newest = child.LastWriteUtc;
                    Interlocked.Increment(ref _files); Interlocked.Add(ref _bytes, size);
                }
                if (size <= 0) return;
                var age = DateTime.UtcNow - newest;
                if (k.MinAge is { } min && age < min) return;

                var reason = k.Label + (k.MinAge is null ? "" : $", unused for {Format.Age(age)}") + suffix;
                Add(new Finding(path, child.IsDirectory, size, newest, child.LastWriteUtc,
                    k.Category, RiskTier.Safe, reason, NeedsAdmin: k.NeedsAdmin));
            });
        }

        private void AddRecycleBins(IEnumerable<string> roots)
        {
            foreach (var r in roots)
            {
                if (r.Length != 3) continue;   // only whole-drive scans include the Recycle Bin
                try { if (new DriveInfo(r).DriveType != DriveType.Fixed) continue; } catch { continue; }
                if (!NativeShell.TryQueryRecycleBin(r, out var size, out var items) || size <= 0) continue;
                var now = DateTime.UtcNow;
                Add(new Finding(r, true, size, now, now, Category.RecycleBin, RiskTier.Safe,
                    $"{items:N0} deleted item(s) waiting in the Recycle Bin. Emptying it is permanent.",
                    ContentsOnly: true, Label: $"Recycle Bin ({r[..2]})"));
            }
        }

        // ---- Whole-disk walk ---------------------------------------------------------------------------

        private void Walk(List<string> roots)
        {
            if (roots.Count == 0) return;
            using var queue = new BlockingCollection<string>();
            int pending = 0;

            void Enqueue(string dir)
            {
                Interlocked.Increment(ref pending);
                queue.Add(dir);
            }
            foreach (var r in roots) Enqueue(r);

            int n = Math.Clamp(Environment.ProcessorCount * 2, 4, 16);
            var tasks = Enumerable.Range(0, n).Select(_ => Task.Factory.StartNew(() =>
            {
                foreach (var dir in queue.GetConsumingEnumerable(_ct))
                {
                    try { ProcessDir(dir, Enqueue); }
                    catch (OperationCanceledException) { throw; }
                    catch { /* one bad folder must not stop the scan */ }
                    finally { if (Interlocked.Decrement(ref pending) == 0) queue.CompleteAdding(); }
                }
            }, _ct, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

            Task.WaitAll(tasks);
        }

        private void ProcessDir(string dir, Action<string> enqueue)
        {
            _ct.ThrowIfCancellationRequested();
            _current = dir;
            List<FsEntry> entries;
            try { entries = FsUtil.Enumerate(dir); }
            catch (UnauthorizedAccessException) { OnDenied(); return; }
            catch (IOException) { return; }

            long files = 0, bytes = 0;
            foreach (var e in entries)
            {
                if (e.IsReparsePoint && e.IsDirectory) continue;   // never follow junctions/symlinks
                if (e.IsCloudOnly) continue;
                var full = PathUtil.Combine(dir, e.Name);

                if (e.IsDirectory)
                {
                    if (SkipDirNames.Contains(e.Name) || _claimed.Contains(full)) continue;
                    if (e.Name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) { EvaluateNodeModules(full, dir, e); continue; }
                    enqueue(full);
                    continue;
                }

                files++; bytes += e.Length;
                if (e.IsReparsePoint) continue;
                EvaluateFile(full, e);
            }
            Interlocked.Add(ref _files, files);
            Interlocked.Add(ref _bytes, bytes);
        }

        private void EvaluateFile(string full, FsEntry e)
        {
            var size = e.Length;
            var age = DateTime.UtcNow - e.LastWriteUtc;
            var ext = Path.GetExtension(e.Name);
            var isLarge = size >= _options.LargeFileThreshold;

            Category cat; string reason;
            if (size >= Rules.MinRuleFileSize && (ext.Equals(".dmp", StringComparison.OrdinalIgnoreCase) || ext.Equals(".mdmp", StringComparison.OrdinalIgnoreCase)))
            { cat = Category.CrashData; reason = "Crash dump file"; }
            else if (size >= Rules.MinRuleFileSize && age >= Rules.LogMinAge
                     && (ext.Equals(".log", StringComparison.OrdinalIgnoreCase) || ext.Equals(".etl", StringComparison.OrdinalIgnoreCase)))
            { cat = Category.OldLogs; reason = $"Log file, not modified for {Format.Age(age)}"; }
            else if (age >= Rules.DownloadMinAge && InstallerExts.Contains(ext) && _rules.IsInDownloads(full))
            { cat = Category.OldDownloads; reason = $"Installer or archive in Downloads, untouched for {Format.Age(age)}"; }
            else if (isLarge)
            { cat = Category.LargeFile; reason = $"Large file, last changed {Format.Age(age)} ago"; }
            else return;

            var lockReason = _rules.GetLockReason(full)
                             ?? ((e.Attributes & FileAttributes.System) != 0 ? "Windows system file" : null);
            if (lockReason is not null)
            {
                // Locked items are only listed so you can see what uses space; skip the small ones.
                if (isLarge) Add(new Finding(full, false, size, e.LastWriteUtc, e.LastWriteUtc, Category.LargeFile, RiskTier.Locked, lockReason));
                return;
            }
            Add(new Finding(full, false, size, e.LastWriteUtc, e.LastWriteUtc, cat, RiskTier.Review, reason));
        }

        private void EvaluateNodeModules(string full, string parent, FsEntry e)
        {
            var m = FsUtil.Measure(full, _ct, OnDenied);
            AddMeasured(m);
            if (m.Bytes <= 0 || _rules.GetLockReason(full) is not null) return;

            var lastActive = m.NewestUtc;
            try
            {
                var pkg = new FileInfo(PathUtil.Combine(parent, "package.json"));
                if (pkg.Exists && pkg.LastWriteTimeUtc > lastActive) lastActive = pkg.LastWriteTimeUtc;
            }
            catch { }

            var age = DateTime.UtcNow - lastActive;
            if (age < Rules.NodeModulesMinAge) return;
            Add(new Finding(full, true, m.Bytes, lastActive, e.LastWriteUtc, Category.NodeModules, RiskTier.Review,
                $"Project dependencies, no changes for {Format.Age(age)}. Restore with npm install."));
        }

        private void AddMeasured(Measurement m)
        {
            Interlocked.Add(ref _files, m.Files);
            Interlocked.Add(ref _bytes, m.Bytes);
        }

        private void OnDenied() => Interlocked.Increment(ref _denied);

        private static DateTime ToLocal(DateTime newestUtc, DateTime fallback) =>
            newestUtc == DateTime.MinValue ? fallback : newestUtc;
    }
}
