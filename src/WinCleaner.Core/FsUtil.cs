using System.IO.Enumeration;

namespace WinCleaner.Core;

public readonly record struct FsEntry(string Name, bool IsDirectory, long Length, DateTime LastWriteUtc, FileAttributes Attributes)
{
    public bool IsReparsePoint => (Attributes & FileAttributes.ReparsePoint) != 0;

    /// <summary>OneDrive-style placeholders: reading them would download data and they use no local space.</summary>
    public bool IsCloudOnly =>
        (Attributes & (FileAttributes.Offline | RecallOnDataAccess | RecallOnOpen)) != 0;

    // FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS / FILE_ATTRIBUTE_RECALL_ON_OPEN (not in the .NET enum)
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
}

public readonly record struct Measurement(long Bytes, DateTime NewestUtc, long Files);

public static class FsUtil
{
    /// <summary>Lists one folder using a single directory query (size, time and attributes come for free). Throws on access denied.</summary>
    public static List<FsEntry> Enumerate(string dir)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
        };
        var query = new FileSystemEnumerable<FsEntry>(dir,
            (ref FileSystemEntry e) => new FsEntry(
                e.FileName.ToString(), e.IsDirectory, e.IsDirectory ? 0 : e.Length,
                e.LastWriteTimeUtc.UtcDateTime, e.Attributes),
            options);
        return query.ToList();
    }

    /// <summary>
    /// Total size of a folder tree. Never follows junctions/symlinks and ignores cloud-only placeholders.
    /// Unreadable folders are skipped and counted through <paramref name="onDenied"/>.
    /// </summary>
    public static Measurement Measure(string root, CancellationToken ct = default, Action? onDenied = null)
    {
        long bytes = 0, files = 0;
        var newest = DateTime.MinValue;
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            List<FsEntry> entries;
            try { entries = Enumerate(dir); }
            catch (UnauthorizedAccessException) { onDenied?.Invoke(); continue; }
            catch (IOException) { continue; }

            foreach (var e in entries)
            {
                if (e.IsReparsePoint || e.IsCloudOnly) continue;
                if (e.IsDirectory) { stack.Push(PathUtil.Combine(dir, e.Name)); continue; }
                bytes += e.Length;
                files++;
                if (e.LastWriteUtc > newest) newest = e.LastWriteUtc;
            }
        }
        return new Measurement(bytes, newest, files);
    }
}
