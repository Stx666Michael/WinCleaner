using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinCleaner.Core;

namespace WinCleaner.App.ViewModels;

/// <summary>Interactive disk-space explorer: a treemap and list of the current folder, with history, breadcrumbs and delete.</summary>
public partial class ExploreViewModel : ObservableObject
{
    private const int MaxRows = 400;

    private static readonly Color[] FolderPalette =
    {
        Color.FromRgb(0x3B, 0x82, 0xC4), Color.FromRgb(0x2E, 0x9E, 0x8F), Color.FromRgb(0x8B, 0x5C, 0xC6),
        Color.FromRgb(0xC4, 0x6A, 0x3B), Color.FromRgb(0x4F, 0x9D, 0x4F), Color.FromRgb(0xB5, 0x48, 0x7A),
        Color.FromRgb(0x6B, 0x7F, 0xD7), Color.FromRgb(0xB0, 0x8A, 0x2E),
    };
    private static readonly Color FileColor = Color.FromRgb(0x5B, 0x65, 0x73);
    private static readonly Color OtherColor = Color.FromRgb(0x45, 0x4B, 0x54);

    private readonly FolderTreeScanner _scanner = new();
    private readonly Rules _rules;
    private readonly Cleaner _cleaner;
    private readonly List<FolderNode> _back = new(), _forward = new();
    private CancellationTokenSource? _cts;
    private ExploreItem? _deleteTarget;
    private string? _currentDeleteBlock;

    public ExploreViewModel(Rules rules, Cleaner cleaner, bool isAdmin)
    {
        _rules = rules;
        _cleaner = cleaner;
        IsAdmin = isAdmin;
    }

    public bool IsAdmin { get; }

    /// <summary>Raised when the user leaves the explorer (or a scan failed or was cancelled).</summary>
    public event Action? CloseRequested;

    /// <summary>Navigation shortcuts only act while the explorer is the visible screen.</summary>
    [ObservableProperty] private bool _isActive;
    partial void OnIsActiveChanged(bool value) => NotifyCommands();

    // ---- scanning ------------------------------------------------------------------------------------
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _scanStats = "";
    [ObservableProperty] private string _scanCurrent = "";
    [ObservableProperty] private int _deniedFolders;
    public bool ShowAdminHint => DeniedFolders > 0 && !IsAdmin;
    partial void OnDeniedFoldersChanged(int value) => OnPropertyChanged(nameof(ShowAdminHint));

    public async Task StartAsync(string rootPath, bool keepLocation = false)
    {
        var keep = keepLocation ? Current?.FullPath : null;
        ClearStatus();
        IsScanning = true;
        ScanStats = "Starting…";
        ScanCurrent = "";
        _cts = new CancellationTokenSource();
        var progress = new Progress<TreeProgress>(p =>
        {
            ScanStats = $"{p.Files:N0} files · {Format.Size(p.Bytes)} mapped";
            ScanCurrent = p.CurrentFolder;
        });

        FolderTreeResult result;
        try { result = await _scanner.ScanAsync(rootPath, progress, _cts.Token); }
        catch (Exception ex)
        {
            IsScanning = false;
            MessageBox.Show($"Could not scan this folder: {ex.Message}", "WinCleaner", MessageBoxButton.OK, MessageBoxImage.Error);
            CloseRequested?.Invoke();
            return;
        }
        IsScanning = false;

        if (result.Cancelled)
        {
            if (Root is null || !keepLocation) CloseRequested?.Invoke();   // a cancelled rescan keeps the old view
            return;
        }

        Root = result.Root;
        DeniedFolders = result.DeniedFolders;
        Title = result.Root.FullPath;
        _back.Clear();
        _forward.Clear();
        SetCurrent(keep is not null ? result.Root.Find(keep) ?? result.Root : result.Root);
    }

    [RelayCommand]
    private void CancelScan() => _cts?.Cancel();

    [RelayCommand]
    private async Task RescanAsync()
    {
        if (Root is not null) await StartAsync(Root.FullPath, keepLocation: true);
    }

    [RelayCommand]
    private void Close()
    {
        _cts?.Cancel();
        CloseRequested?.Invoke();
    }

    // ---- current folder ------------------------------------------------------------------------------
    [ObservableProperty] private FolderNode? _root;
    [ObservableProperty] private FolderNode? _current;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private string _emptyText = "";
    [ObservableProperty] private IReadOnlyList<ExploreItem> _items = Array.Empty<ExploreItem>();
    [ObservableProperty] private ExploreItem? _hoverItem;
    public ObservableCollection<Crumb> Crumbs { get; } = new();

    partial void OnHoverItemChanged(ExploreItem? oldValue, ExploreItem? newValue)
    {
        if (oldValue is not null) oldValue.IsHot = false;
        if (newValue is not null) newValue.IsHot = true;
    }

    private void SetCurrent(FolderNode node)
    {
        Current = node;
        HoverItem = null;
        _currentDeleteBlock = ReferenceEquals(node, Root) ? "This is the folder you are exploring" : _rules.GetDeleteBlockReason(node.FullPath);
        BuildItems();
        BuildCrumbs();

        Summary = $"{Format.Size(node.Size)} in {node.FileCount:N0} file{(node.FileCount == 1 ? "" : "s")}"
                  + (Root is { Size: > 0 } root && !ReferenceEquals(node, root) ? $" · {node.Size * 100.0 / root.Size:0.#}% of {root.Name}" : "");
        IsEmpty = !Items.Any(i => i.Size > 0);
        EmptyText = node.Denied ? "This folder could not be read (access denied)." : "This folder is empty.";
        OnPropertyChanged(nameof(DeleteFolderTip));
        NotifyCommands();
    }

    partial void OnIsScanningChanged(bool value) => NotifyCommands();
    partial void OnShowDeleteConfirmChanged(bool value) => NotifyCommands();
    partial void OnIsDeletingChanged(bool value) => NotifyCommands();

    private void BuildItems()
    {
        var cur = Current!;
        var curPath = cur.FullPath;
        double total = cur.Size;

        var all = new List<(ExploreKind Kind, string Name, long Size, FolderNode? Node, string? File)>();
        foreach (var c in cur.Children) all.Add((ExploreKind.Folder, c.Name, c.Size, c, null));
        foreach (var f in cur.Files) all.Add((ExploreKind.File, f.Name, f.Size, null, f.Name));
        if (cur.OtherFileCount > 0)
            all.Add((ExploreKind.Others, $"{cur.OtherFileCount:N0} other files", cur.OtherFilesSize, null, null));

        var sorted = all.OrderByDescending(a => a.Size).ToList();
        var list = new List<ExploreItem>();
        int folderRank = 0;

        foreach (var a in sorted.Take(MaxRows))
        {
            string? path = a.Kind == ExploreKind.Others ? null : PathUtil.Combine(curPath, a.Name);
            string? lockReason = path is null ? null : _rules.GetDeleteBlockReason(path);
            double pct = total > 0 ? a.Size * 100.0 / total : 0;

            string detail; Color color;
            switch (a.Kind)
            {
                case ExploreKind.Folder:
                    detail = $"{a.Node!.FileCount:N0} file{(a.Node.FileCount == 1 ? "" : "s")}" + (a.Node.Denied ? " · access denied" : "");
                    color = FolderPalette[folderRank++ % FolderPalette.Length];
                    break;
                case ExploreKind.File:
                    detail = cur.Files.First(f => f.Name == a.File).ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd");
                    color = FileColor;
                    break;
                default:
                    detail = "Files too small to list one by one";
                    color = OtherColor;
                    break;
            }
            list.Add(new ExploreItem(a.Kind, a.Name, a.Size, pct, detail, color, path, lockReason, a.Node, a.File,
                OpenItem, RevealItem, RequestDelete));
        }

        if (sorted.Count > MaxRows)
        {
            var rest = sorted.Skip(MaxRows).ToList();
            long size = rest.Sum(r => r.Size);
            list.Add(new ExploreItem(ExploreKind.Others, $"{rest.Count:N0} smaller items", size,
                total > 0 ? size * 100.0 / total : 0, "Too small to list one by one", OtherColor,
                null, null, null, null, OpenItem, RevealItem, RequestDelete));
        }
        Items = list;
    }

    private void BuildCrumbs()
    {
        var chain = new List<FolderNode>();
        for (var n = Current; n is not null; n = n.Parent) chain.Add(n);
        chain.Reverse();
        Crumbs.Clear();
        for (int i = 0; i < chain.Count; i++)
            Crumbs.Add(new Crumb(chain[i], chain[i].Name, i == chain.Count - 1, NavigateTo));
    }

    // ---- navigation ------------------------------------------------------------------------------------
    private bool CanGoBack => IsActive && _back.Count > 0;
    private bool CanGoForward => IsActive && _forward.Count > 0;
    private bool CanGoUp => IsActive && Current?.Parent is not null;

    private void NotifyCommands()
    {
        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
        GoUpCommand.NotifyCanExecuteChanged();
        DeleteFolderCommand.NotifyCanExecuteChanged();
        DeleteHoveredCommand.NotifyCanExecuteChanged();
    }

    private void OpenItem(ExploreItem item)
    {
        if (item.IsFolder && item.Node is not null) NavigateTo(item.Node);
    }

    private void NavigateTo(FolderNode node)
    {
        if (Current is null || ReferenceEquals(node, Current)) return;
        _back.Add(Current);
        _forward.Clear();
        SetCurrent(node);
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void GoBack()
    {
        if (Current is null || _back.Count == 0) return;
        _forward.Add(Current);
        var target = _back[^1];
        _back.RemoveAt(_back.Count - 1);
        SetCurrent(target);
    }

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void GoForward()
    {
        if (Current is null || _forward.Count == 0) return;
        _back.Add(Current);
        var target = _forward[^1];
        _forward.RemoveAt(_forward.Count - 1);
        SetCurrent(target);
    }

    [RelayCommand(CanExecute = nameof(CanGoUp))]
    private void GoUp()
    {
        if (Current?.Parent is { } parent) NavigateTo(parent);
    }

    // ---- Explorer integration --------------------------------------------------------------------------
    [RelayCommand]
    private void OpenCurrentInExplorer()
    {
        if (Current is not null) StartExplorer($"\"{Current.FullPath}\"");
    }

    private void RevealItem(ExploreItem item)
    {
        if (item.Path is null) OpenCurrentInExplorer();
        else if (item.IsFolder) StartExplorer($"\"{item.Path}\"");
        else StartExplorer($"/select,\"{item.Path}\"");
    }

    private static void StartExplorer(string args)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true }); }
        catch { /* nothing useful to do if Explorer cannot open */ }
    }

    // ---- status banner ---------------------------------------------------------------------------------
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _statusNeedsRescan;
    public bool HasStatus => Status.Length > 0;
    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    private void SetStatus(string text, bool needsRescan = false)
    {
        Status = text;
        StatusNeedsRescan = needsRescan;
    }

    [RelayCommand]
    private void ClearStatus() => SetStatus("");

    // ---- delete ------------------------------------------------------------------------------------------
    [ObservableProperty] private bool _showDeleteConfirm;
    [ObservableProperty] private bool _deletePermanent;
    [ObservableProperty] private bool _isDeleting;
    [ObservableProperty] private string _deleteTitle = "";
    [ObservableProperty] private string _deleteDetail = "";
    [ObservableProperty] private string _deletingName = "";

    private bool CanDeleteCurrent => IsActive && Current is not null && !IsScanning && _currentDeleteBlock is null;
    public string DeleteFolderTip => _currentDeleteBlock is null ? "Delete this folder…" : _currentDeleteBlock;

    private void RequestDelete(ExploreItem item)
    {
        if (!item.CanDelete || item.Path is null || ShowDeleteConfirm || IsDeleting) return;
        _deleteTarget = item;
        DeleteTitle = $"Delete “{item.Name}”?";
        DeleteDetail = item.IsFolder
            ? $"{item.SizeText} in {item.Node!.FileCount:N0} file{(item.Node.FileCount == 1 ? "" : "s")}\n{item.Path}"
            : $"{item.SizeText}\n{item.Path}";
        DeletePermanent = false;
        ShowDeleteConfirm = true;
    }

    [RelayCommand(CanExecute = nameof(CanDeleteCurrent))]
    private void DeleteFolder()
    {
        if (Current is null) return;
        var cur = Current;
        // Describe the folder you are standing in as an item, so it follows the same confirm-and-clean path.
        RequestDelete(new ExploreItem(ExploreKind.Folder, cur.Name, cur.Size, 100, "", FolderPalette[0],
            cur.FullPath, _currentDeleteBlock, cur, null, OpenItem, RevealItem, RequestDelete));
    }

    [RelayCommand(CanExecute = nameof(CanDeleteHovered))]
    private void DeleteHovered()
    {
        if (HoverItem is { CanDelete: true } item) RequestDelete(item);
    }
    private bool CanDeleteHovered => IsActive && !IsScanning && !ShowDeleteConfirm && !IsDeleting;

    [RelayCommand]
    private void CancelDelete() => ShowDeleteConfirm = false;

    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        var item = _deleteTarget;
        if (item?.Path is null) return;
        ShowDeleteConfirm = false;
        DeletingName = item.Name;
        IsDeleting = true;

        var finding = BuildFinding(item);
        var mode = DeletePermanent ? CleanMode.Permanent : CleanMode.RecycleBin;
        CleanResult result;
        try { result = await Task.Run(() => _cleaner.Clean(new[] { finding }, mode)); }
        catch (Exception ex)
        {
            IsDeleting = false;
            SetStatus($"Could not delete “{item.Name}”: {ex.Message}");
            return;
        }
        IsDeleting = false;
        ApplyDeleteResult(item, result, mode);
    }

    /// <summary>
    /// A fresh snapshot of the item as it is on disk right now. The user is looking at this exact item and has just
    /// confirmed it, so "changed since the scan" is not the right check here; every other safety check still runs.
    /// </summary>
    private static Finding BuildFinding(ExploreItem item)
    {
        var path = item.Path!;
        if (item.IsFolder)
        {
            var di = new DirectoryInfo(path);
            var stamp = di.Exists ? di.LastWriteTimeUtc : DateTime.UtcNow;
            return new Finding(path, true, item.Size, stamp, stamp, Category.LargeFile, RiskTier.Review, "Deleted from the explorer");
        }
        var fi = new FileInfo(path);
        return fi.Exists
            ? new Finding(path, false, fi.Length, fi.LastWriteTimeUtc, fi.LastWriteTimeUtc, Category.LargeFile, RiskTier.Review, "Deleted from the explorer")
            : new Finding(path, false, item.Size, DateTime.UtcNow, DateTime.UtcNow, Category.LargeFile, RiskTier.Review, "Deleted from the explorer");
    }

    private void ApplyDeleteResult(ExploreItem item, CleanResult result, CleanMode mode)
    {
        bool cleaned = result.CleanedPaths.Any(p => string.Equals(p, item.Path, StringComparison.OrdinalIgnoreCase));
        if (cleaned)
        {
            RemoveFromTree(item);
            SetStatus(mode == CleanMode.RecycleBin
                ? $"Moved “{item.Name}” ({item.SizeText}) to the Recycle Bin. The space is freed once you empty it."
                : $"Deleted “{item.Name}” ({item.SizeText}).");
            return;
        }

        var reason = result.Failures.FirstOrDefault()?.Reason ?? "unknown reason";
        if (reason.Contains("no longer exists", StringComparison.OrdinalIgnoreCase) || reason.Equals("Already gone", StringComparison.OrdinalIgnoreCase))
        {
            RemoveFromTree(item);
            SetStatus($"“{item.Name}” was already gone.");
        }
        else if (reason.StartsWith("Partly cleaned", StringComparison.OrdinalIgnoreCase))
        {
            SetStatus($"“{item.Name}” was only partly deleted. {reason} Rescan to refresh the sizes.", needsRescan: true);
        }
        else
        {
            SetStatus($"Could not delete “{item.Name}”: {reason}");
        }
    }

    private void RemoveFromTree(ExploreItem item)
    {
        if (Current is null) return;
        if (item.IsFolder && item.Node is { Parent: { } parent } node)
        {
            parent.RemoveChild(node);
            _back.RemoveAll(n => n.IsSameOrInside(node));
            _forward.RemoveAll(n => n.IsSameOrInside(node));
            SetCurrent(Current.IsSameOrInside(node) ? parent : Current);   // deleted the folder we were in: step up
        }
        else if (item.FileName is not null)
        {
            Current.RemoveFile(item.FileName);
            SetCurrent(Current);
        }
    }
}
