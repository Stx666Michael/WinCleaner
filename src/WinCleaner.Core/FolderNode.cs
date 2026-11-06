namespace WinCleaner.Core;

public readonly record struct FileEntry(string Name, long Size, DateTime ModifiedUtc);

/// <summary>
/// One folder in a scanned disk tree, with the total size of everything below it.
/// Only the largest <see cref="MaxFilesKept"/> files of each folder are kept individually; the rest are
/// summed up, so scanning a whole drive stays light on memory.
/// </summary>
public sealed class FolderNode
{
    public const int MaxFilesKept = 50;

    public FolderNode(string name, FolderNode? parent)
    {
        Name = name;
        Parent = parent;
    }

    public string Name { get; }
    public FolderNode? Parent { get; }

    /// <summary>Sub-folders, largest first (sorted once the scan has finished).</summary>
    public List<FolderNode> Children { get; } = new();

    /// <summary>The largest files directly inside this folder, largest first.</summary>
    public IReadOnlyList<FileEntry> Files { get; internal set; } = Array.Empty<FileEntry>();

    /// <summary>Bytes of files directly inside this folder (kept and not kept).</summary>
    public long OwnFilesSize { get; internal set; }
    public int OwnFileCount { get; internal set; }

    /// <summary>Files directly inside this folder that are not listed in <see cref="Files"/>.</summary>
    public long OtherFilesSize { get; internal set; }
    public int OtherFileCount { get; internal set; }

    /// <summary>Total bytes in this folder and everything below it.</summary>
    public long Size { get; internal set; }
    public long FileCount { get; internal set; }

    /// <summary>The folder could not be read (access denied), so its contents are not counted.</summary>
    public bool Denied { get; internal set; }

    /// <summary>Removes a sub-folder (after it was deleted from disk) and updates the totals of this folder and its ancestors.</summary>
    public bool RemoveChild(FolderNode child)
    {
        if (!Children.Remove(child)) return false;
        SubtractFromTotals(child.Size, child.FileCount);
        return true;
    }

    /// <summary>Removes a listed file (after it was deleted from disk) and updates the totals.</summary>
    public bool RemoveFile(string fileName)
    {
        int i = -1;
        for (int k = 0; k < Files.Count; k++)
            if (Files[k].Name.Equals(fileName, StringComparison.OrdinalIgnoreCase)) { i = k; break; }
        if (i < 0) return false;

        var file = Files[i];
        var rest = Files.ToList();
        rest.RemoveAt(i);
        Files = rest.ToArray();
        OwnFilesSize -= file.Size;
        OwnFileCount--;
        SubtractFromTotals(file.Size, 1);
        return true;
    }

    private void SubtractFromTotals(long size, long files)
    {
        for (var n = this; n is not null; n = n.Parent)
        {
            n.Size = Math.Max(0, n.Size - size);
            n.FileCount = Math.Max(0, n.FileCount - files);
        }
    }

    public bool IsSameOrInside(FolderNode ancestor)
    {
        for (var n = this; n is not null; n = n.Parent)
            if (ReferenceEquals(n, ancestor)) return true;
        return false;
    }

    public string FullPath
    {
        get
        {
            var names = new Stack<string>();
            for (var n = this; n is not null; n = n.Parent) names.Push(n.Name);
            var path = names.Pop();
            while (names.Count > 0) path = PathUtil.Combine(path, names.Pop());
            return path;
        }
    }

    /// <summary>Finds the node for a path at or below this one; null if it is not part of this tree.</summary>
    public FolderNode? Find(string fullPath)
    {
        string target;
        try { target = PathUtil.Normalize(fullPath); }
        catch (ArgumentException) { return null; }

        var self = FullPath;
        if (string.Equals(target, self, StringComparison.OrdinalIgnoreCase)) return this;
        if (!PathUtil.IsUnder(target, self)) return null;

        var node = this;
        foreach (var part in target[self.Length..].TrimStart('\\').Split('\\'))
        {
            node = node.Children.FirstOrDefault(c => c.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
            if (node is null) return null;
        }
        return node;
    }
}
