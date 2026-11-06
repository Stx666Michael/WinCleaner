namespace WinCleaner.Core;

/// <summary>A folder that is known to hold disposable data.</summary>
/// <param name="PerChild">true: each entry inside is its own finding. false: the whole folder is one finding and only its contents are cleaned.</param>
public sealed record KnownLocation(
    string Path,
    Category Category,
    string Label,
    bool PerChild,
    TimeSpan? MinAge = null,
    bool NeedsAdmin = false);

public sealed record RulesConfig(
    IReadOnlyList<string> ProtectedRoots,
    IReadOnlyList<KnownLocation> KnownLocations,
    string UserProfile,
    string? DownloadsDir);

/// <summary>
/// Decides what may never be cleaned. This is the single source of truth used by both the scanner
/// (to mark items Locked) and the cleaner (to refuse to delete them).
/// </summary>
public sealed class Rules
{
    public static readonly TimeSpan TempMinAge = TimeSpan.FromHours(24);
    public static readonly TimeSpan LogMinAge = TimeSpan.FromDays(30);
    public static readonly TimeSpan DownloadMinAge = TimeSpan.FromDays(90);
    public static readonly TimeSpan NodeModulesMinAge = TimeSpan.FromDays(90);
    public const long MinRuleFileSize = 1024 * 1024;

    private static readonly HashSet<string> ProtectedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "pagefile.sys", "hiberfil.sys", "swapfile.sys", "DumpStack.log", "DumpStack.log.tmp", "bootmgr", "BOOTNXT",
    };

    private readonly string[] _protectedRoots;
    private readonly KnownLocation[] _known;
    private readonly string _userProfile;
    private readonly string _usersRoot;

    public IReadOnlyList<KnownLocation> KnownLocations => _known;
    public string UserProfile => _userProfile;
    public string? DownloadsDir { get; }

    public Rules(RulesConfig config)
    {
        _protectedRoots = config.ProtectedRoots.Select(TryNormalize).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _known = config.KnownLocations
            .Select(k => TryNormalize(k.Path) is { } p ? k with { Path = p } : null)
            .OfType<KnownLocation>().ToArray();
        _userProfile = PathUtil.Normalize(config.UserProfile);
        _usersRoot = PathUtil.Normalize(Path.GetDirectoryName(_userProfile) ?? _userProfile);
        DownloadsDir = config.DownloadsDir is { } d ? TryNormalize(d) : null;
    }

    private static string? TryNormalize(string p)
    {
        try { return PathUtil.Normalize(p); } catch (ArgumentException) { return null; }
    }

    /// <summary>Why this path must never be deleted, or null if it is allowed.</summary>
    public string? GetLockReason(string path)
    {
        string p;
        try { p = PathUtil.Normalize(path); }
        catch (ArgumentException) { return "Unsupported or unsafe path"; }
        catch (NotSupportedException) { return "Unsupported or unsafe path"; }

        if (p.Length <= 3) return "Drive root";

        // Approved disposable folders win over the protected folders that contain them (e.g. C:\Windows\Temp).
        if (_known.Any(k => PathUtil.IsUnder(p, k.Path))) return null;

        var name = Path.GetFileName(p);
        if (ProtectedFileNames.Contains(name)
            || name.StartsWith("ntuser.", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("usrclass.dat", StringComparison.OrdinalIgnoreCase))
            return "Windows system file";

        foreach (var root in _protectedRoots)
        {
            if (PathUtil.IsSameOrUnder(p, root)) return "Protected Windows or program folder";
            if (PathUtil.IsUnder(root, p)) return "Contains protected system folders";
        }

        if (PathUtil.IsUnder(p, _usersRoot) && !PathUtil.IsSameOrUnder(p, _userProfile) && !PathUtil.IsUnder(_userProfile, p))
            return "Belongs to another user profile";
        if (PathUtil.IsUnder(_userProfile, p) || string.Equals(p, _userProfile, StringComparison.OrdinalIgnoreCase))
            return "Contains your user profile";

        return null;
    }

    /// <summary>True if the (normalized) path is exactly one of the approved cache folders.</summary>
    public bool IsKnownLocationRoot(string normalizedPath) =>
        _known.Any(k => string.Equals(k.Path, normalizedPath, StringComparison.OrdinalIgnoreCase));

    public bool IsInDownloads(string normalizedPath) =>
        DownloadsDir is not null && PathUtil.IsUnder(normalizedPath, DownloadsDir);

    // ---------------------------------------------------------------------------------------------

    public static Rules CreateDefault()
    {
        var sysRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var sysDrive = Path.GetPathRoot(sysRoot) ?? @"C:\";
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var prot = new List<string>
        {
            sysRoot,
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            programData,
            Path.Combine(sysDrive, "Boot"),
            Path.Combine(sysDrive, "EFI"),
            Path.Combine(sysDrive, "Windows.old"),
            Path.Combine(sysDrive, "$WinREAgent"),
        };
        foreach (var d in DriveInfo.GetDrives())
        {
            if (d.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
            foreach (var n in new[] { "$Recycle.Bin", "System Volume Information", "Recovery", "Config.Msi", "Program Files", "Program Files (x86)" })
                prot.Add(Path.Combine(d.Name, n));
        }

        var known = new List<KnownLocation>();
        void Add(string path, Category c, string label, bool perChild, TimeSpan? minAge = null, bool admin = false) =>
            known.Add(new KnownLocation(path, c, label, perChild, minAge, admin));

        Add(Path.GetTempPath(), Category.Temp, "User temp file", true, TempMinAge);
        Add(Path.Combine(sysRoot, "Temp"), Category.Temp, "Windows temp file", true, TempMinAge, true);
        Add(Path.Combine(sysRoot, "SoftwareDistribution", "Download"), Category.WindowsUpdate, "Windows Update download", true, null, true);
        Add(Path.Combine(sysRoot, "Minidump"), Category.CrashData, "Windows crash dump", true, null, true);
        Add(Path.Combine(programData, "Microsoft", "Windows", "WER"), Category.CrashData, "Windows Error Reporting data", true, null, true);
        Add(Path.Combine(local, "CrashDumps"), Category.CrashData, "App crash dump", true);
        Add(Path.Combine(local, "Microsoft", "Windows", "WER"), Category.CrashData, "Error report", true);

        Add(Path.Combine(local, "Microsoft", "Windows", "INetCache"), Category.AppCache, "Internet cache", false);
        Add(Path.Combine(local, "D3DSCache"), Category.AppCache, "DirectX shader cache", false);
        Add(Path.Combine(local, "NVIDIA", "DXCache"), Category.AppCache, "NVIDIA DirectX shader cache", false);
        Add(Path.Combine(local, "NVIDIA", "GLCache"), Category.AppCache, "NVIDIA OpenGL shader cache", false);
        Add(Path.Combine(local, "AMD", "DxCache"), Category.AppCache, "AMD shader cache", false);
        Add(Path.Combine(local, "npm-cache"), Category.AppCache, "npm cache", false);
        Add(Path.Combine(local, "pip", "Cache"), Category.AppCache, "pip cache", false);
        Add(Path.Combine(local, "NuGet", "v3-cache"), Category.AppCache, "NuGet HTTP cache", false);

        // Browser caches only: never profiles, cookies, history or passwords.
        var chromium = new (string Name, string UserData)[]
        {
            ("Chrome", Path.Combine(local, "Google", "Chrome", "User Data")),
            ("Edge", Path.Combine(local, "Microsoft", "Edge", "User Data")),
            ("Brave", Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data")),
        };
        foreach (var (name, userData) in chromium)
        {
            if (!Directory.Exists(userData)) continue;
            foreach (var prof in SafeDirs(userData))
            {
                var pn = Path.GetFileName(prof);
                if (pn != "Default" && !pn.StartsWith("Profile ", StringComparison.Ordinal)) continue;
                foreach (var sub in new[] { "Cache", "Code Cache", "GPUCache" })
                    Add(Path.Combine(prof, sub), Category.BrowserCache, $"{name} {sub.ToLowerInvariant()} ({pn})", false);
            }
        }
        var ffProfiles = Path.Combine(local, "Mozilla", "Firefox", "Profiles");
        if (Directory.Exists(ffProfiles))
            foreach (var prof in SafeDirs(ffProfiles))
                Add(Path.Combine(prof, "cache2"), Category.BrowserCache, "Firefox cache", false);

        return new Rules(new RulesConfig(prot, known, profile, NativeShell.GetDownloadsFolder()));
    }

    private static IEnumerable<string> SafeDirs(string dir)
    {
        try { return Directory.GetDirectories(dir); } catch { return Array.Empty<string>(); }
    }
}
