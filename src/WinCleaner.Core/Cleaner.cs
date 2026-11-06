using System.Text;

namespace WinCleaner.Core;

/// <summary>
/// The only code that deletes anything. Every item is re-validated right before it is touched, so a
/// stale or tampered list can never remove a protected path, follow a link out of the scanned area,
/// or delete a file that changed after the scan.
/// </summary>
public sealed class Cleaner
{
    private readonly Rules _rules;
    private readonly string _logPath;

    public Cleaner(Rules rules, string? logPath = null)
    {
        _rules = rules;
        _logPath = logPath ?? DefaultLogPath;
    }

    public static string DefaultLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinCleaner", "clean-log.txt");

    public CleanResult Clean(IReadOnlyList<Finding> items, CleanMode mode, IProgress<CleanProgress>? progress = null, CancellationToken ct = default)
    {
        var result = new CleanResult { Mode = mode, LogPath = _logPath };
        var log = new StringBuilder();
        log.AppendLine($"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} clean started, mode={mode}, items={items.Count}");

        for (int i = 0; i < items.Count; i++)
        {
            if (ct.IsCancellationRequested) { result.Cancelled = true; break; }
            var f = items[i];
            progress?.Report(new CleanProgress(i, items.Count, f.Label ?? f.Path));
            try
            {
                var outcome = CleanOne(f, mode, result, ct);
                if (outcome is not null)
                {
                    result.Failures.Add(new CleanFailure(f.Path, outcome));
                    log.AppendLine($"SKIP   {f.Path} :: {outcome}");
                }
                else
                {
                    result.ItemsCleaned++;
                    result.CleanedPaths.Add(f.Path);
                    log.AppendLine($"{(mode == CleanMode.Permanent || f.Category == Category.RecycleBin ? "DELETE" : "RECYCLE")} {f.Path} ({f.Size} bytes)");
                }
            }
            catch (OperationCanceledException) { result.Cancelled = true; break; }
            catch (Exception ex)
            {
                result.Failures.Add(new CleanFailure(f.Path, ex.Message));
                log.AppendLine($"FAIL   {f.Path} :: {ex.Message}");
            }
        }

        progress?.Report(new CleanProgress(items.Count, items.Count, ""));
        log.AppendLine($"=== done: cleaned={result.ItemsCleaned}, failed={result.Failures.Count}, freed={result.BytesFreed}, recycled={result.BytesRecycled}");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            File.AppendAllText(_logPath, log.ToString());
        }
        catch { /* logging must never break a clean */ }
        return result;
    }

    /// <returns>null on success, otherwise the reason the item was not (fully) cleaned.</returns>
    private string? CleanOne(Finding f, CleanMode mode, CleanResult result, CancellationToken ct)
    {
        if (f.Tier == RiskTier.Locked) return "Protected item";

        if (f.Category == Category.RecycleBin) return EmptyRecycleBin(f, result);

        string p;
        try { p = PathUtil.Normalize(f.Path); }
        catch (ArgumentException) { return "Unsupported or unsafe path"; }

        // 1. Policy: is this path allowed at all?
        if (f.ContentsOnly)
        {
            if (!_rules.IsKnownLocationRoot(p)) return "Not an approved cache folder";
        }
        else
        {
            var lockReason = _rules.GetLockReason(p);
            if (lockReason is not null) return lockReason;
        }

        // 2. The path must be what it claims: no junction/symlink anywhere in it.
        var final = PathUtil.ResolveFinal(p);
        if (final is null) return f.IsDirectory ? "Folder no longer exists" : "File no longer exists";
        if (!string.Equals(final, p, StringComparison.OrdinalIgnoreCase)) return "Path goes through a link";
        if (!f.ContentsOnly && _rules.GetLockReason(final) is { } finalLock) return finalLock;

        // 3. It must still be the thing we scanned.
        FileSystemInfo info = f.IsDirectory ? new DirectoryInfo(p) : new FileInfo(p);
        if (!info.Exists) return "Already gone";
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) return "Is a link or cloud file";
        if (!f.IsDirectory)
        {
            if ((info.Attributes & FileAttributes.System) != 0) return "Windows system file";
            var fi = (FileInfo)info;
            if (fi.Length != f.Size || !SameTime(fi.LastWriteTimeUtc, f.StampUtc)) return "Changed since the scan";
        }
        else if (!f.ContentsOnly && !SameTime(info.LastWriteTimeUtc, f.StampUtc))
        {
            return "Folder changed since the scan";
        }

        // 4. Recycle Bin needs a drive that has one.
        if (mode == CleanMode.RecycleBin)
        {
            try { if (new DriveInfo(p[..1]).DriveType != DriveType.Fixed) return "This drive has no Recycle Bin. Use permanent delete."; }
            catch { return "Cannot determine drive type"; }
        }

        // 5. Do it.
        long before = f.IsDirectory ? FsUtil.Measure(p, ct).Bytes : f.Size;
        string? error = null;

        if (mode == CleanMode.Permanent)
        {
            if (f.IsDirectory) DeleteTree(p, deleteSelf: !f.ContentsOnly, ref error, ct);
            else DeleteFile(p, ref error);
        }
        else
        {
            var targets = f.ContentsOnly ? ChildPaths(p, ref error) : new List<string> { p };
            for (int i = 0; i < targets.Count; i += 100)
            {
                ct.ThrowIfCancellationRequested();
                var batch = targets.GetRange(i, Math.Min(100, targets.Count - i));
                NativeShell.Recycle(batch, out var aborted);
                if (aborted) { error ??= "Cancelled by Windows"; break; }
            }
        }

        long after = 0;
        if (f.IsDirectory) { if (Directory.Exists(p)) after = FsUtil.Measure(p, ct).Bytes; }
        else if (File.Exists(p)) after = f.Size;

        long removed = Math.Max(0, before - after);
        if (mode == CleanMode.Permanent) result.BytesFreed += removed; else result.BytesRecycled += removed;

        bool leftovers = f.ContentsOnly ? after > 0 : (f.IsDirectory ? Directory.Exists(p) : File.Exists(p));
        if (!leftovers) return null;
        var why = error ?? "In use or access denied";
        return removed > 0 ? $"Partly cleaned ({Format.Size(after)} left): {why}" : why;
    }

    private string? EmptyRecycleBin(Finding f, CleanResult result)
    {
        string root;
        try { root = PathUtil.Normalize(f.Path); }
        catch (ArgumentException) { return "Unsupported path"; }
        if (root.Length != 3 || new DriveInfo(root).DriveType != DriveType.Fixed) return "Not a fixed drive";
        int hr = NativeShell.EmptyRecycleBin(root);
        if (hr != 0) return $"Windows could not empty the Recycle Bin (0x{hr:X8})";
        result.BytesFreed += f.Size;
        return null;
    }

    private static bool SameTime(DateTime a, DateTime b) => Math.Abs((a - b).TotalSeconds) < 2;

    private static List<string> ChildPaths(string dir, ref string? error)
    {
        var list = new List<string>();
        try
        {
            foreach (var e in new DirectoryInfo(dir).EnumerateFileSystemInfos())
            {
                if ((e.Attributes & FileAttributes.ReparsePoint) != 0) { RemoveLink(e); continue; }
                list.Add(e.FullName);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error ??= ex.Message; }
        return list;
    }

    private static void DeleteFile(string path, ref string? error)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error ??= ex.Message; }
    }

    /// <summary>Removes a junction/symlink itself without touching what it points to.</summary>
    private static void RemoveLink(FileSystemInfo link)
    {
        try
        {
            if (link is DirectoryInfo) Directory.Delete(link.FullName, recursive: false);
            else File.Delete(link.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void DeleteTree(string dir, bool deleteSelf, ref string? error, CancellationToken ct)
    {
        IEnumerable<FileSystemInfo> children;
        try { children = new DirectoryInfo(dir).EnumerateFileSystemInfos().ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error ??= ex.Message; return; }

        foreach (var c in children)
        {
            ct.ThrowIfCancellationRequested();
            if ((c.Attributes & FileAttributes.ReparsePoint) != 0) { RemoveLink(c); continue; }   // never recurse into links
            if (c is DirectoryInfo) DeleteTree(c.FullName, deleteSelf: true, ref error, ct);
            else DeleteFile(c.FullName, ref error);
        }

        if (!deleteSelf) return;
        try { Directory.Delete(dir, recursive: false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error ??= ex.Message; }
    }
}
