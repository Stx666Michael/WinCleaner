using WinCleaner.Core;

namespace WinCleaner.Tests;

public class RulesTests : IDisposable
{
    private readonly Sandbox _sb = new();
    public void Dispose() => _sb.Dispose();

    private string? Lock(string path) => _sb.Rules.GetLockReason(path);

    [Fact]
    public void Protected_folder_contents_are_locked()
    {
        Assert.NotNull(Lock(Path.Combine(_sb.Windows, "System32", "kernel32.dll")));
        Assert.NotNull(Lock(_sb.Windows));
    }

    [Fact]
    public void Casing_does_not_bypass_protection()
    {
        Assert.NotNull(Lock(Path.Combine(_sb.Root, "WINDOWS", "system32", "x.dll")));
        Assert.NotNull(Lock(Path.Combine(_sb.Root, "windows", "x.dll")));
    }

    [Fact]
    public void Extended_length_prefix_does_not_bypass_protection()
    {
        Assert.NotNull(Lock(@"\\?\" + Path.Combine(_sb.Windows, "x.dll")));
    }

    [Fact]
    public void Dot_dot_segments_are_resolved_before_checking()
    {
        var sneaky = Path.Combine(_sb.Me, "..", "..", "Windows", "x.dll");
        Assert.NotNull(Lock(sneaky));
    }

    [Fact]
    public void Forward_slashes_do_not_bypass_protection()
    {
        Assert.NotNull(Lock(Path.Combine(_sb.Windows, "x.dll").Replace('\\', '/')));
    }

    [Fact]
    public void Other_users_and_user_root_are_locked()
    {
        Assert.NotNull(Lock(Path.Combine(_sb.Other, "secret.docx")));
        Assert.NotNull(Lock(Path.GetDirectoryName(_sb.Me)!));   // the Users folder contains profiles
        Assert.NotNull(Lock(_sb.Me));                           // the profile itself
    }

    [Fact]
    public void Own_files_are_allowed()
    {
        Assert.Null(Lock(Path.Combine(_sb.Me, "Videos", "big.mkv")));
        Assert.Null(Lock(Path.Combine(_sb.Downloads, "setup.exe")));
    }

    [Theory]
    [InlineData("pagefile.sys")]
    [InlineData("hiberfil.sys")]
    [InlineData("swapfile.sys")]
    [InlineData("NTUSER.DAT")]
    [InlineData("ntuser.dat.LOG1")]
    public void System_files_are_locked_anywhere(string name)
    {
        Assert.NotNull(Lock(Path.Combine(_sb.Root, name)));
        Assert.NotNull(Lock(Path.Combine(_sb.Me, name)));
    }

    [Fact]
    public void Unsafe_path_shapes_are_locked()
    {
        Assert.NotNull(Lock(@"\\server\share\file.bin"));
        Assert.NotNull(Lock(@"\\.\PhysicalDrive0"));
        Assert.NotNull(Lock(@"C:file.txt"));                         // drive-relative
        Assert.NotNull(Lock(Path.Combine(_sb.Me, "a.txt") + ":hidden"));  // alternate data stream
        Assert.NotNull(Lock(""));
        Assert.NotNull(Lock(@"C:\"));                                // drive root
    }

    [Fact]
    public void Approved_location_inside_a_protected_folder_is_allowed_but_not_the_folder_itself()
    {
        Assert.Null(Lock(Path.Combine(_sb.WinTemp, "junk.tmp")));
        Assert.NotNull(Lock(_sb.WinTemp));
        Assert.NotNull(Lock(Path.Combine(_sb.Windows, "Other", "junk.tmp")));
    }

    [Fact]
    public void Default_rules_protect_the_real_windows_folder()
    {
        var rules = Rules.CreateDefault();
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.NotNull(rules.GetLockReason(Path.Combine(win, "System32", "ntdll.dll")));
        Assert.NotNull(rules.GetLockReason(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)));
        Assert.NotNull(rules.GetLockReason(@"C:\pagefile.sys"));
        Assert.Null(rules.GetLockReason(Path.Combine(Path.GetTempPath(), "x.tmp")));
    }
}
