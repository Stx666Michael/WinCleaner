using WinCleaner.Core;

namespace WinCleaner.Tests;

public class CleanerTests : IDisposable
{
    private readonly Sandbox _sb = new();
    public void Dispose() => _sb.Dispose();

    [Fact]
    public void Permanent_delete_removes_a_file_and_reports_freed_bytes()
    {
        var p = _sb.File_(Path.Combine(_sb.Me, "Videos", "big.mkv"), 5000);
        var result = _sb.NewCleaner().Clean(new[] { Sandbox.Make(p, 5000) }, CleanMode.Permanent);

        Assert.False(File.Exists(p));
        Assert.Equal(1, result.ItemsCleaned);
        Assert.Equal(5000, result.BytesFreed);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public void File_changed_after_scan_is_skipped()
    {
        var p = _sb.File_(Path.Combine(_sb.Me, "doc.bin"), 5000);
        var finding = Sandbox.Make(p, 5000);
        File.AppendAllText(p, "edited after the scan");   // size and time change

        var result = _sb.NewCleaner().Clean(new[] { finding }, CleanMode.Permanent);
        Assert.True(File.Exists(p));
        Assert.Equal(0, result.ItemsCleaned);
        Assert.Contains("Changed", Assert.Single(result.Failures).Reason);
    }

    [Fact]
    public void Protected_file_is_refused_even_if_the_finding_claims_it_is_reviewable()
    {
        var p = _sb.File_(Path.Combine(_sb.Windows, "System32", "core.dll"), 100);
        var forged = Sandbox.Make(p, 100, tier: RiskTier.Safe);

        var result = _sb.NewCleaner().Clean(new[] { forged }, CleanMode.Permanent);
        Assert.True(File.Exists(p));
        Assert.Single(result.Failures);
    }

    [Fact]
    public void Locked_tier_is_refused()
    {
        var p = _sb.File_(Path.Combine(_sb.Me, "x.bin"), 100);
        var result = _sb.NewCleaner().Clean(new[] { Sandbox.Make(p, 100, tier: RiskTier.Locked) }, CleanMode.Permanent);
        Assert.True(File.Exists(p));
        Assert.Single(result.Failures);
    }

    [Fact]
    public void Another_users_file_is_refused()
    {
        var p = _sb.File_(Path.Combine(_sb.Other, "private.docx"), 100);
        var result = _sb.NewCleaner().Clean(new[] { Sandbox.Make(p, 100) }, CleanMode.Permanent);
        Assert.True(File.Exists(p));
        Assert.Single(result.Failures);
    }

    [Fact]
    public void Approved_folder_itself_cannot_be_deleted_without_contents_only()
    {
        _sb.File_(Path.Combine(_sb.WinTemp, "a.tmp"), 100);
        var f = Sandbox.Make(_sb.WinTemp, 100, Category.Temp, RiskTier.Safe, isDir: true);

        var result = _sb.NewCleaner().Clean(new[] { f }, CleanMode.Permanent);
        Assert.True(Directory.Exists(_sb.WinTemp));
        Assert.True(File.Exists(Path.Combine(_sb.WinTemp, "a.tmp")));
        Assert.Single(result.Failures);
    }

    [Fact]
    public void An_approved_cache_folder_itself_cannot_be_deleted_only_its_contents()
    {
        _sb.File_(Path.Combine(_sb.Cache, "a.bin"), 100);
        // Cache is an approved whole-folder cache in the sandbox rules; deleting the folder as a plain folder must fail.
        var f = Sandbox.Make(_sb.Cache, 100, Category.LargeFile, RiskTier.Review, isDir: true, contentsOnly: false);

        var result = _sb.NewCleaner().Clean(new[] { f }, CleanMode.Permanent);
        Assert.True(Directory.Exists(_sb.Cache));
        Assert.True(File.Exists(Path.Combine(_sb.Cache, "a.bin")));
        Assert.Contains("only its contents", Assert.Single(result.Failures).Reason);
        Assert.NotNull(_sb.Rules.GetDeleteBlockReason(_sb.Cache));
        Assert.Null(_sb.Rules.GetDeleteBlockReason(Path.Combine(_sb.Cache, "a.bin")));
    }

    [Fact]
    public void A_whole_user_folder_can_be_deleted_with_a_fresh_stamp_but_never_the_profile_itself()
    {
        var dir = Path.Combine(_sb.Me, "Projects", "old");
        _sb.File_(Path.Combine(dir, "sub", "x.bin"), 500);
        _sb.File_(Path.Combine(dir, "y.bin"), 500);
        var f = Sandbox.Make(dir, 1000, Category.LargeFile, RiskTier.Review, isDir: true);

        var result = _sb.NewCleaner().Clean(new[] { f }, CleanMode.Permanent);
        Assert.False(Directory.Exists(dir));
        Assert.Equal(1000, result.BytesFreed);

        var profile = Sandbox.Make(_sb.Me, 1, Category.LargeFile, RiskTier.Review, isDir: true);
        var blocked = _sb.NewCleaner().Clean(new[] { profile }, CleanMode.Permanent);
        Assert.True(Directory.Exists(_sb.Me));
        Assert.Single(blocked.Failures);
    }

    [Fact]
    public void Contents_only_empties_a_cache_but_keeps_the_folder()
    {
        _sb.File_(Path.Combine(_sb.Cache, "a.bin"), 1000);
        _sb.File_(Path.Combine(_sb.Cache, "sub", "b.bin"), 1000);
        var f = Sandbox.Make(_sb.Cache, 2000, Category.AppCache, RiskTier.Safe, isDir: true, contentsOnly: true);

        var result = _sb.NewCleaner().Clean(new[] { f }, CleanMode.Permanent);
        Assert.True(Directory.Exists(_sb.Cache));
        Assert.Empty(Directory.GetFileSystemEntries(_sb.Cache));
        Assert.Equal(2000, result.BytesFreed);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public void Contents_only_is_refused_for_a_folder_that_is_not_an_approved_cache()
    {
        var dir = Path.Combine(_sb.Me, "Documents");
        _sb.File_(Path.Combine(dir, "thesis.docx"), 100);
        var f = Sandbox.Make(dir, 100, Category.AppCache, RiskTier.Safe, isDir: true, contentsOnly: true);

        var result = _sb.NewCleaner().Clean(new[] { f }, CleanMode.Permanent);
        Assert.True(File.Exists(Path.Combine(dir, "thesis.docx")));
        Assert.Single(result.Failures);
    }

    [Fact]
    public void Deleting_a_folder_never_follows_a_junction_inside_it()
    {
        var precious = _sb.File_(Path.Combine(_sb.Outside, "precious.bin"), 1000);
        _sb.File_(Path.Combine(_sb.Cache, "junk.bin"), 1000);
        var link = _sb.Junction(Path.Combine(_sb.Cache, "link"), _sb.Outside);
        var f = Sandbox.Make(_sb.Cache, 1000, Category.AppCache, RiskTier.Safe, isDir: true, contentsOnly: true);

        _sb.NewCleaner().Clean(new[] { f }, CleanMode.Permanent);

        Assert.True(File.Exists(precious), "file behind the junction must survive");
        Assert.True(Directory.Exists(_sb.Outside));
        Assert.False(Directory.Exists(link), "the junction itself is removed");
        Assert.False(File.Exists(Path.Combine(_sb.Cache, "junk.bin")));
    }

    [Fact]
    public void Path_that_goes_through_a_junction_is_refused()
    {
        var victim = _sb.File_(Path.Combine(_sb.Windows, "keep.dll"), 100);
        _sb.Junction(Path.Combine(_sb.Me, "sneaky"), _sb.Windows);
        var viaLink = Path.Combine(_sb.Me, "sneaky", "keep.dll");
        var f = new Finding(viaLink, false, 100, File.GetLastWriteTimeUtc(victim), File.GetLastWriteTimeUtc(victim),
            Category.LargeFile, RiskTier.Review, "forged");

        var result = _sb.NewCleaner().Clean(new[] { f }, CleanMode.Permanent);
        Assert.True(File.Exists(victim));
        Assert.Single(result.Failures);
    }

    [Fact]
    public void Missing_file_and_unreadable_path_do_not_crash_the_run()
    {
        var gone = Path.Combine(_sb.Me, "gone.bin");
        var ok = _sb.File_(Path.Combine(_sb.Me, "ok.bin"), 100);
        var items = new[]
        {
            new Finding(gone, false, 1, DateTime.UtcNow, DateTime.UtcNow, Category.LargeFile, RiskTier.Review, "t"),
            new Finding(@"\\server\share\x", false, 1, DateTime.UtcNow, DateTime.UtcNow, Category.LargeFile, RiskTier.Review, "t"),
            Sandbox.Make(ok, 100),
        };
        var result = _sb.NewCleaner().Clean(items, CleanMode.Permanent);
        Assert.Equal(1, result.ItemsCleaned);
        Assert.Equal(2, result.Failures.Count);
        Assert.False(File.Exists(ok));
    }

    [Fact]
    public void A_locked_file_is_reported_and_other_items_still_get_cleaned()
    {
        var inUse = _sb.File_(Path.Combine(_sb.Me, "inuse.bin"), 100);
        var other = _sb.File_(Path.Combine(_sb.Me, "other.bin"), 100);
        var items = new[] { Sandbox.Make(inUse, 100), Sandbox.Make(other, 100) };

        using (new FileStream(inUse, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = _sb.NewCleaner().Clean(items, CleanMode.Permanent);
            Assert.Equal(1, result.ItemsCleaned);
            Assert.Single(result.Failures);
        }
        Assert.True(File.Exists(inUse));
        Assert.False(File.Exists(other));
    }

    [Fact]
    public void Read_only_files_are_deleted()
    {
        var p = _sb.File_(Path.Combine(_sb.Me, "ro.bin"), 100);
        File.SetAttributes(p, FileAttributes.ReadOnly);
        var result = _sb.NewCleaner().Clean(new[] { Sandbox.Make(p, 100) }, CleanMode.Permanent);
        Assert.False(File.Exists(p));
        Assert.Empty(result.Failures);
    }

    [Fact]
    public void Every_outcome_is_written_to_the_log()
    {
        var p = _sb.File_(Path.Combine(_sb.Me, "logged.bin"), 100);
        var blocked = _sb.File_(Path.Combine(_sb.Windows, "blocked.dll"), 100);
        var result = _sb.NewCleaner().Clean(new[] { Sandbox.Make(p, 100), Sandbox.Make(blocked, 100) }, CleanMode.Permanent);

        var log = File.ReadAllText(result.LogPath!);
        Assert.Contains("DELETE", log);
        Assert.Contains("logged.bin", log);
        Assert.Contains("SKIP", log);
        Assert.Contains("blocked.dll", log);
    }

    [Fact]
    public async Task Scan_then_clean_round_trip_removes_only_the_selected_item()
    {
        var a = _sb.File_(Path.Combine(_sb.Me, "a.bin"), 5000);
        var b = _sb.File_(Path.Combine(_sb.Me, "b.bin"), 5000);
        var r = await new Scanner(_sb.Rules).ScanAsync(new ScanOptions(new[] { _sb.Root }, 1000), null, CancellationToken.None);

        var picked = r.Findings.Where(f => f.Path == a).ToList();
        var result = _sb.NewCleaner().Clean(picked, CleanMode.Permanent);

        Assert.False(File.Exists(a));
        Assert.True(File.Exists(b));
        Assert.Equal(5000, result.BytesFreed);
    }
}
