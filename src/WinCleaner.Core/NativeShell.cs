using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WinCleaner.Core;

/// <summary>Thin P/Invoke layer. The app is x64-only, so the struct layouts below are the x64 ones.</summary>
internal static class NativeShell
{
    // ---- Recycle Bin -------------------------------------------------------------------------
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct SHQUERYRBINFO
    {
        public uint cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    private const uint SHERB_NOCONFIRMATION = 0x1, SHERB_NOPROGRESSUI = 0x2, SHERB_NOSOUND = 0x4;

    public static bool TryQueryRecycleBin(string root, out long size, out long items)
    {
        var info = new SHQUERYRBINFO { cbSize = (uint)Marshal.SizeOf<SHQUERYRBINFO>() };
        int hr = SHQueryRecycleBin(root, ref info);
        size = hr == 0 ? info.i64Size : 0;
        items = hr == 0 ? info.i64NumItems : 0;
        return hr == 0;
    }

    /// <returns>0 on success, otherwise an HRESULT.</returns>
    public static int EmptyRecycleBin(string root) =>
        SHEmptyRecycleBin(IntPtr.Zero, root, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);

    // ---- Move to Recycle Bin -----------------------------------------------------------------
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    private const uint FO_DELETE = 0x3;
    private const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40,
                         FOF_NOERRORUI = 0x400, FOF_WANTNUKEWARNING = 0x4000;

    /// <summary>
    /// Sends paths to the Recycle Bin. FOF_WANTNUKEWARNING makes Windows ask before it permanently
    /// deletes something that is too big for the bin, instead of silently destroying it.
    /// </summary>
    /// <returns>0 on success, otherwise a legacy SHFileOperation error code.</returns>
    public static int Recycle(IReadOnlyList<string> paths, out bool aborted)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = string.Join("\0", paths) + "\0",   // marshaller adds the second terminator
            fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOERRORUI | FOF_SILENT | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING),
        };
        int rc = SHFileOperation(ref op);
        aborted = op.fAnyOperationsAborted;
        return rc;
    }

    // ---- Paths -------------------------------------------------------------------------------
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathNameW(string lpszShortPath, StringBuilder lpszLongPath, uint cchBuffer);

    public static string ExpandShortPath(string path)
    {
        var sb = new StringBuilder(1024);
        uint n = GetLongPathNameW(path, sb, (uint)sb.Capacity);
        if (n == 0) return path;
        if (n > sb.Capacity) { sb = new StringBuilder((int)n + 1); n = GetLongPathNameW(path, sb, (uint)sb.Capacity); if (n == 0) return path; }
        return sb.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle hFile, StringBuilder lpszFilePath, uint cchFilePath, uint dwFlags);

    private const uint FILE_SHARE_ALL = 0x7, OPEN_EXISTING = 3, FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    /// <summary>The real location of a path after resolving every junction/symlink in it, or null if it cannot be opened.</summary>
    public static string? GetFinalPath(string path)
    {
        using var h = CreateFileW(path, 0, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (h.IsInvalid) return null;
        var sb = new StringBuilder(1024);
        uint n = GetFinalPathNameByHandleW(h, sb, (uint)sb.Capacity, 0);
        if (n == 0) return null;
        if (n > sb.Capacity) { sb = new StringBuilder((int)n + 1); n = GetFinalPathNameByHandleW(h, sb, (uint)sb.Capacity, 0); if (n == 0) return null; }
        return sb.ToString();
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    private static readonly Guid FolderIdDownloads = new("374DE290-123F-4565-9164-39C4925E467B");

    public static string? GetDownloadsFolder()
    {
        try
        {
            if (SHGetKnownFolderPath(FolderIdDownloads, 0, IntPtr.Zero, out var p) != 0) return null;
            try { return Marshal.PtrToStringUni(p); } finally { Marshal.FreeCoTaskMem(p); }
        }
        catch { return null; }
    }
}
