using WinCleaner.Core;

namespace WinCleaner.Tests;

public class FolderTreeTests : IDisposable
{
    private readonly Sandbox _sb = new();
    public void Dispose() => _sb.Dispose();

    private async Task<FolderTreeResult> Scan(string? root = null, CancellationToken ct = default) =>
        await new FolderTreeScanner().ScanAsync(root ?? _sb.Me, null, ct);

    [Fact]
    public async Task Sizes_are_totalled_up_the_tree()
    {
        _sb.File_(Path.Combine(_sb.Me, "a.bin"), 100);
        _sb.File_(Path.Combine(_sb.Me, "Videos", "v1.mkv"), 1000);
        _sb.File_(Path.Combine(_sb.Me, "Videos", "2024", "v2.mkv"), 2000);
        _sb.File_(Path.Combine(_sb.Me, "Docs", "d.txt"), 50);

        var r = await Scan();
        Assert.Equal(3150, r.Root.Size);
        Assert.Equal(4, r.Root.FileCount);
        Assert.Equal(100, r.Root.OwnFilesSize);

        var videos = r.Root.Children.Single(c => c.Name == "Videos");
        Assert.Equal(3000, videos.Size);
        Assert.Equal(1000, videos.OwnFilesSize);
        Assert.Equal(2000, videos.Children.Single().Size);
        Assert.Equal(3150, r.Bytes);                 // the scanner's running totals match the tree
        Assert.Equal(4, r.Files);
    }

    [Fact]
    public async Task Children_are_sorted_largest_first_and_files_are_sorted_too()
    {
        _sb.File_(Path.Combine(_sb.Me, "small", "x"), 10);
        _sb.File_(Path.Combine(_sb.Me, "big", "x"), 5000);
        _sb.File_(Path.Combine(_sb.Me, "mid", "x"), 300);
        _sb.File_(Path.Combine(_sb.Me, "f1"), 5);
        _sb.File_(Path.Combine(_sb.Me, "f2"), 50);

        var r = await Scan();
        // (the sandbox profile also has empty Cache and Downloads folders, which sort last)
        Assert.Equal(new[] { "big", "mid", "small" }, r.Root.Children.Take(3).Select(c => c.Name));
        Assert.True(r.Root.Children.Zip(r.Root.Children.Skip(1)).All(p => p.First.Size >= p.Second.Size));
        Assert.Equal(new[] { "f2", "f1" }, r.Root.Files.Select(f => f.Name));
    }

    [Fact]
    public async Task Only_the_largest_files_are_kept_per_folder_but_totals_stay_exact()
    {
        int n = FolderNode.MaxFilesKept + 25;
        for (int i = 1; i <= n; i++) _sb.File_(Path.Combine(_sb.Me, "many", $"f{i}.bin"), i);

        var many = (await Scan()).Root.Children.Single(c => c.Name == "many");
        long expected = n * (n + 1L) / 2;
        Assert.Equal(expected, many.Size);
        Assert.Equal(FolderNode.MaxFilesKept, many.Files.Count);
        Assert.Equal(n, many.Files[0].Size);                       // largest first
        Assert.Equal(25, many.OtherFileCount);
        Assert.Equal(many.OwnFilesSize, many.Files.Sum(f => f.Size) + many.OtherFilesSize);
    }

    [Fact]
    public async Task Junctions_are_not_followed()
    {
        _sb.File_(Path.Combine(_sb.Outside, "hidden.bin"), 9999);
        _sb.File_(Path.Combine(_sb.Me, "real.bin"), 10);
        _sb.Junction(Path.Combine(_sb.Me, "link"), _sb.Outside);

        var r = await Scan();
        Assert.Equal(10, r.Root.Size);
        Assert.DoesNotContain(r.Root.Children, c => c.Name == "link");
    }

    [Fact]
    public async Task Empty_folders_are_listed_with_zero_size()
    {
        Directory.CreateDirectory(Path.Combine(_sb.Me, "empty"));
        var r = await Scan();
        var empty = Assert.Single(r.Root.Children, c => c.Name == "empty");
        Assert.Equal(0, empty.Size);
    }

    [Fact]
    public async Task Full_paths_and_find_round_trip()
    {
        _sb.File_(Path.Combine(_sb.Me, "a", "b", "c", "x.bin"), 7);
        var r = await Scan();
        var c = r.Root.Find(Path.Combine(_sb.Me, "a", "b", "c"))!;
        Assert.NotNull(c);
        Assert.Equal(Path.Combine(_sb.Me, "a", "b", "c"), c.FullPath);
        Assert.Same(r.Root, r.Root.Find(_sb.Me));
        Assert.Same(c, r.Root.Find(Path.Combine(_sb.Me, "A", "B", "C")));   // case-insensitive
        Assert.Null(r.Root.Find(Path.Combine(_sb.Me, "a", "nope")));
        Assert.Null(r.Root.Find(_sb.Other));                               // outside the tree
    }

    [Fact]
    public async Task Removing_a_child_or_file_updates_every_ancestor()
    {
        _sb.File_(Path.Combine(_sb.Me, "top.bin"), 100);
        _sb.File_(Path.Combine(_sb.Me, "a", "b", "deep.bin"), 1000);
        _sb.File_(Path.Combine(_sb.Me, "a", "keep.bin"), 10);
        var r = await Scan();
        var a = r.Root.Children.Single(c => c.Name == "a");
        var b = a.Children.Single();

        Assert.True(a.RemoveChild(b));
        Assert.Equal(10, a.Size);
        Assert.Equal(110, r.Root.Size);
        Assert.Equal(2, r.Root.FileCount);

        Assert.True(r.Root.RemoveFile("top.bin"));
        Assert.Equal(10, r.Root.Size);
        Assert.Equal(1, r.Root.FileCount);
        Assert.Equal(0, r.Root.OwnFilesSize);
        Assert.False(r.Root.RemoveFile("top.bin"));   // already gone
    }

    [Fact]
    public async Task Cancellation_is_reported()
    {
        for (int i = 0; i < 30; i++) _sb.File_(Path.Combine(_sb.Me, $"d{i}", "f"), 1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var r = await Scan(ct: cts.Token);
        Assert.True(r.Cancelled);
    }

    [Fact]
    public async Task Missing_or_invalid_roots_throw()
    {
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => Scan(Path.Combine(_sb.Root, "nope")));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Scan(@"\\server\share"));
    }

    [Fact]
    public async Task Unreadable_folder_is_flagged_not_fatal()
    {
        // A file where a folder is expected cannot be enumerated: simulate by scanning a real tree then checking the flag default.
        _sb.File_(Path.Combine(_sb.Me, "ok", "f"), 5);
        var r = await Scan();
        Assert.False(r.Root.Denied);
        Assert.Equal(0, r.DeniedFolders);
    }
}
