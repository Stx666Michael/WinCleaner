using WinCleaner.Core;

namespace WinCleaner.Tests;

public class ScannerTests : IDisposable
{
    private readonly Sandbox _sb = new();
    public void Dispose() => _sb.Dispose();

    private async Task<ScanResult> Scan(long threshold = 1000, params string[] roots)
    {
        var scanner = new Scanner(_sb.Rules);
        return await scanner.ScanAsync(new ScanOptions(roots.Length > 0 ? roots : new[] { _sb.Root }, threshold), null, CancellationToken.None);
    }

    [Fact]
    public async Task Finds_large_files_and_ignores_small_ones()
    {
        var big = _sb.File_(Path.Combine(_sb.Me, "Videos", "big.mkv"), 5000);
        _sb.File_(Path.Combine(_sb.Me, "Videos", "small.txt"), 100);

        var r = await Scan();
        var f = Assert.Single(r.Findings, x => x.Path == big);
        Assert.Equal(Category.LargeFile, f.Category);
        Assert.Equal(RiskTier.Review, f.Tier);
        Assert.DoesNotContain(r.Findings, x => x.Path.EndsWith("small.txt"));
    }

    [Fact]
    public async Task Large_files_in_protected_or_foreign_places_are_listed_as_locked()
    {
        var sys = _sb.File_(Path.Combine(_sb.Windows, "System32", "huge.dll"), 5000);
        var theirs = _sb.File_(Path.Combine(_sb.Other, "movie.mp4"), 5000);

        var r = await Scan();
        Assert.Equal(RiskTier.Locked, r.Findings.Single(x => x.Path == sys).Tier);
        Assert.Equal(RiskTier.Locked, r.Findings.Single(x => x.Path == theirs).Tier);
    }

    [Fact]
    public async Task Does_not_follow_junctions()
    {
        var hidden = _sb.File_(Path.Combine(_sb.Outside, "hidden-big.bin"), 5000);
        _sb.Junction(Path.Combine(_sb.Me, "link"), _sb.Outside);

        // Scan only the user folder: the file is reachable solely through the junction.
        var r = await Scan(1000, _sb.Me);
        Assert.DoesNotContain(r.Findings, x => x.Path.Contains("hidden-big"));
        Assert.DoesNotContain(r.Findings, x => x.Path.Contains("link"));
    }

    [Fact]
    public async Task Old_node_modules_is_one_item_and_recent_is_ignored()
    {
        var old = Path.Combine(_sb.Me, "oldproj", "node_modules");
        _sb.File_(Path.Combine(old, "a", "index.js"), 3000, ageDays: 200);
        _sb.File_(Path.Combine(old, "b", "index.js"), 3000, ageDays: 200);
        Directory.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-200));

        var fresh = Path.Combine(_sb.Me, "newproj", "node_modules");
        _sb.File_(Path.Combine(fresh, "a", "index.js"), 3000);

        var r = await Scan();
        var nm = Assert.Single(r.Findings, x => x.Category == Category.NodeModules);
        Assert.Equal(old, nm.Path);
        Assert.True(nm.IsDirectory);
        Assert.Equal(6000, nm.Size);
        // Files inside node_modules must not also appear as separate large files.
        Assert.DoesNotContain(r.Findings, x => x.Category == Category.LargeFile && x.Path.Contains("node_modules"));
    }

    [Fact]
    public async Task Approved_cache_is_one_contents_only_item()
    {
        _sb.File_(Path.Combine(_sb.Cache, "a.bin"), 4000);
        _sb.File_(Path.Combine(_sb.Cache, "sub", "b.bin"), 4000);

        var r = await Scan();
        var f = Assert.Single(r.Findings, x => x.Category == Category.AppCache);
        Assert.Equal(_sb.Cache, f.Path);
        Assert.True(f.ContentsOnly);
        Assert.Equal(RiskTier.Safe, f.Tier);
        Assert.Equal(8000, f.Size);
        // Not double counted by the large-file walk.
        Assert.DoesNotContain(r.Findings, x => x.Category == Category.LargeFile && x.Path.StartsWith(_sb.Cache));
    }

    [Fact]
    public async Task Temp_items_are_listed_per_child_and_only_when_old_enough()
    {
        var old = _sb.File_(Path.Combine(_sb.WinTemp, "old.tmp"), 50, ageDays: 3);
        _sb.File_(Path.Combine(_sb.WinTemp, "new.tmp"), 50);

        var r = await Scan();
        var f = Assert.Single(r.Findings, x => x.Category == Category.Temp);
        Assert.Equal(old, f.Path);
        Assert.Equal(RiskTier.Safe, f.Tier);
    }

    [Fact]
    public async Task Old_installers_in_downloads_are_found_but_recent_ones_are_not()
    {
        var old = _sb.File_(Path.Combine(_sb.Downloads, "setup.exe"), 200, ageDays: 200);
        _sb.File_(Path.Combine(_sb.Downloads, "new-setup.exe"), 200);
        _sb.File_(Path.Combine(_sb.Me, "Documents", "tool.exe"), 200, ageDays: 200);   // not in Downloads

        var r = await Scan();
        var f = Assert.Single(r.Findings, x => x.Category == Category.OldDownloads);
        Assert.Equal(old, f.Path);
    }

    [Fact]
    public async Task Old_big_logs_and_crash_dumps_are_found()
    {
        var log = _sb.File_(Path.Combine(_sb.Me, "app", "service.log"), 2 * 1024 * 1024, ageDays: 60);
        var dump = _sb.File_(Path.Combine(_sb.Me, "app", "crash.dmp"), 2 * 1024 * 1024);
        _sb.File_(Path.Combine(_sb.Me, "app", "recent.log"), 2 * 1024 * 1024);
        _sb.File_(Path.Combine(_sb.Me, "app", "tiny.log"), 100, ageDays: 60);

        var r = await Scan(threshold: long.MaxValue);
        Assert.Equal(Category.OldLogs, r.Findings.Single(x => x.Path == log).Category);
        Assert.Equal(Category.CrashData, r.Findings.Single(x => x.Path == dump).Category);
        Assert.Equal(2, r.Findings.Count);
    }

    [Fact]
    public async Task Cancellation_stops_the_scan()
    {
        for (int i = 0; i < 50; i++) _sb.File_(Path.Combine(_sb.Me, $"d{i}", "f.bin"), 10);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var r = await new Scanner(_sb.Rules).ScanAsync(new ScanOptions(new[] { _sb.Root }), null, cts.Token);
        Assert.True(r.Cancelled);
    }
}
