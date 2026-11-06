using System.Diagnostics;
using WinCleaner.Core;

namespace WinCleaner.Tests;

/// <summary>
/// A throwaway directory tree with its own Rules, so tests never touch real user files.
///   Windows\            protected (Windows\Temp is an approved per-child temp location)
///   Users\me\           the "current user"; Users\other\ belongs to someone else
///   Users\me\Cache      approved whole-folder cache
/// </summary>
public sealed class Sandbox : IDisposable
{
    public string Root { get; }
    public string Windows => Path.Combine(Root, "Windows");
    public string WinTemp => Path.Combine(Windows, "Temp");
    public string Me => Path.Combine(Root, "Users", "me");
    public string Other => Path.Combine(Root, "Users", "other");
    public string Cache => Path.Combine(Me, "Cache");
    public string Downloads => Path.Combine(Me, "Downloads");
    public string Outside => Path.Combine(Root, "Outside");   // reachable only through links
    public Rules Rules { get; }

    public Sandbox()
    {
        // Resolve 8.3 / link differences up front so path comparisons are exact.
        var tmp = PathUtil.Normalize(Path.GetTempPath());
        Root = Path.Combine(tmp, "wc-test-" + Guid.NewGuid().ToString("N")[..8]);
        foreach (var d in new[] { WinTemp, Me, Other, Cache, Downloads, Outside })
            Directory.CreateDirectory(d);

        Rules = new Rules(new RulesConfig(
            ProtectedRoots: new[] { Windows },
            KnownLocations: new[]
            {
                new KnownLocation(WinTemp, Category.Temp, "Windows temp file", PerChild: true, MinAge: TimeSpan.FromHours(24), NeedsAdmin: true),
                new KnownLocation(Cache, Category.AppCache, "Test cache", PerChild: false),
            },
            UserProfile: Me,
            DownloadsDir: Downloads));
    }

    public string File_(string path, int bytes = 10, int ageDays = 0)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        if (ageDays > 0) File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-ageDays));
        return path;
    }

    public string Junction(string link, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        { CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException("mklink failed: " + p.StandardError.ReadToEnd());
        return link;
    }

    public static Finding Make(string path, long size, Category cat = Category.LargeFile, RiskTier tier = RiskTier.Review,
        bool isDir = false, bool contentsOnly = false)
    {
        var stamp = isDir ? Directory.GetLastWriteTimeUtc(path) : File.GetLastWriteTimeUtc(path);
        return new Finding(path, isDir, size, stamp, stamp, cat, tier, "test", contentsOnly);
    }

    public Cleaner NewCleaner() => new(Rules, Path.Combine(Root, "clean-log.txt"));

    public void Dispose()
    {
        try
        {
            // Remove junctions first so the recursive delete can never wander outside the sandbox.
            foreach (var d in Directory.EnumerateDirectories(Root, "*", SearchOption.AllDirectories).ToList())
                if (Directory.Exists(d) && (new DirectoryInfo(d).Attributes & FileAttributes.ReparsePoint) != 0)
                    Directory.Delete(d, false);
            Directory.Delete(Root, true);
        }
        catch { }
    }
}
