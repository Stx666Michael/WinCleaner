namespace WinCleaner.Core;

public static class PathUtil
{
    /// <summary>
    /// Turns a path into one canonical form: no \\?\ prefix, no "..", 8.3 names expanded, no trailing slash.
    /// Throws ArgumentException for anything that is not a plain local drive path (device paths, UNC,
    /// alternate data streams, drive-relative paths), so callers can treat "cannot normalize" as "unsafe".
    /// </summary>
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Empty path");
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Network path");
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
        else if (path.StartsWith(@"\\", StringComparison.Ordinal)) throw new ArgumentException("Network or device path");

        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || (path[2] != '\\' && path[2] != '/'))
            throw new ArgumentException("Only absolute local drive paths are supported");
        if (path.IndexOf(':', 2) >= 0) throw new ArgumentException("Alternate data streams are not supported");

        var full = Path.GetFullPath(path);
        if (full.Contains('~')) full = NativeShell.ExpandShortPath(full);
        return TrimEnd(full);
    }

    public static string TrimEnd(string path) =>
        path.Length > 3 ? path.TrimEnd('\\', '/') : path;

    /// <summary>True if <paramref name="path"/> is strictly inside <paramref name="root"/>. Both must be normalized.</summary>
    public static bool IsUnder(string path, string root)
    {
        if (path.Length <= root.Length) return false;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
        return root.EndsWith('\\') || path[root.Length] == '\\';
    }

    public static bool IsSameOrUnder(string path, string root) =>
        string.Equals(path, root, StringComparison.OrdinalIgnoreCase) || IsUnder(path, root);

    /// <summary>The real path after following all junctions/symlinks, normalized; null when it cannot be resolved.</summary>
    public static string? ResolveFinal(string path)
    {
        var final = NativeShell.GetFinalPath(path);
        if (final is null) return null;
        try { return Normalize(final); } catch (ArgumentException) { return null; }
    }

    public static string Combine(string dir, string name) =>
        dir.EndsWith('\\') ? dir + name : dir + "\\" + name;
}
