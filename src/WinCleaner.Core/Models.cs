namespace WinCleaner.Core;

public enum RiskTier { Safe, Review, Locked }

public enum Category
{
    Temp,
    BrowserCache,
    AppCache,
    CrashData,
    WindowsUpdate,
    RecycleBin,
    OldLogs,
    OldDownloads,
    NodeModules,
    LargeFile,
}

public enum CleanMode { RecycleBin, Permanent }

public static class CategoryInfo
{
    public static string Name(Category c) => c switch
    {
        Category.Temp => "Temporary files",
        Category.BrowserCache => "Browser caches",
        Category.AppCache => "App & developer caches",
        Category.CrashData => "Crash reports & dumps",
        Category.WindowsUpdate => "Windows Update downloads",
        Category.RecycleBin => "Recycle Bin",
        Category.OldLogs => "Old log files",
        Category.OldDownloads => "Old downloads",
        Category.NodeModules => "Unused node_modules",
        Category.LargeFile => "Large files",
        _ => c.ToString(),
    };
}

/// <summary>One item the scan suggests cleaning: a file, a folder, or the Recycle Bin of a drive.</summary>
/// <param name="StampUtc">The item's own last-write time at scan time; the cleaner re-checks it before deleting.</param>
/// <param name="ContentsOnly">Delete what is inside the folder but keep the folder itself (used for caches).</param>
public sealed record Finding(
    string Path,
    bool IsDirectory,
    long Size,
    DateTime ModifiedUtc,
    DateTime StampUtc,
    Category Category,
    RiskTier Tier,
    string Reason,
    bool ContentsOnly = false,
    string? Label = null,
    bool NeedsAdmin = false);

public sealed record ScanOptions(
    IReadOnlyList<string> Roots,
    long LargeFileThreshold = 500L * 1024 * 1024,
    bool IncludeKnownLocations = true);

public readonly record struct ScanProgress(
    long FilesScanned,
    long BytesScanned,
    long FoundBytes,
    int FindingCount,
    int DeniedFolders,
    string CurrentFolder);

public sealed class ScanResult
{
    public List<Finding> Findings { get; init; } = new();
    public int DeniedFolders { get; init; }
    public long FilesScanned { get; init; }
    public long BytesScanned { get; init; }
    public TimeSpan Elapsed { get; init; }
    public bool Cancelled { get; init; }
}

public sealed record CleanFailure(string Path, string Reason);

public readonly record struct CleanProgress(int Done, int Total, string Current);

public sealed class CleanResult
{
    public CleanMode Mode { get; init; }
    public int ItemsCleaned { get; set; }
    /// <summary>Bytes actually removed from disk (permanent delete, or Recycle Bin emptied).</summary>
    public long BytesFreed { get; set; }
    /// <summary>Bytes moved into the Recycle Bin; they only free space once the bin is emptied.</summary>
    public long BytesRecycled { get; set; }
    public List<CleanFailure> Failures { get; } = new();
    /// <summary>Paths of items that were cleaned completely.</summary>
    public List<string> CleanedPaths { get; } = new();
    public bool Cancelled { get; set; }
    public string? LogPath { get; set; }
}
