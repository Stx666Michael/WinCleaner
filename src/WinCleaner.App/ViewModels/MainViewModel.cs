using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using WinCleaner.Core;

namespace WinCleaner.App.ViewModels;

public enum AppState { Home, Scanning, Results, Cleaning, Summary }

public partial class MainViewModel : ObservableObject
{
    private readonly Rules _rules = Rules.CreateDefault();
    private readonly Scanner _scanner;
    private readonly Cleaner _cleaner;
    private CancellationTokenSource? _cts;
    private bool _bulkChange;
    private string? _sortColumn = "Size";
    private bool _sortDescending = true;
    private List<FindingItem> _lastCleanItems = new();
    private CleanResult? _lastResult;

    public MainViewModel()
    {
        _scanner = new Scanner(_rules);
        _cleaner = new Cleaner(_rules);
        IsAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

        var sysDrive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                Drives.Add(new DriveItem(d, string.Equals(d.Name, sysDrive, StringComparison.OrdinalIgnoreCase)));
            }
            catch { /* drive vanished while listing */ }
        }
        // Folders passed on the command line (e.g. "Open with" / a shortcut) are scanned instead of the system drive.
        foreach (var arg in Environment.GetCommandLineArgs().Skip(1).Where(Directory.Exists))
        {
            foreach (var d in Drives) d.IsSelected = false;
            CustomFolders.Add(Path.GetFullPath(arg));
        }
        foreach (var d in Drives) d.PropertyChanged += (_, _) => OnPropertyChanged(nameof(CanScan));
        CustomFolders.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CanScan));

        Thresholds = new[]
        {
            new ThresholdOption("100 MB", 100L << 20), new ThresholdOption("250 MB", 250L << 20),
            new ThresholdOption("500 MB", 500L << 20), new ThresholdOption("1 GB", 1L << 30),
            new ThresholdOption("5 GB", 5L << 30),
        };
        _selectedThreshold = Thresholds[2];

        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = o => o is FindingItem i && Matches(i);
        ApplySort();
    }

    // ---- State -------------------------------------------------------------------------------------
    [ObservableProperty] private AppState _state = AppState.Home;
    public bool IsAdmin { get; }

    // ---- Home --------------------------------------------------------------------------------------
    public ObservableCollection<DriveItem> Drives { get; } = new();
    public ObservableCollection<string> CustomFolders { get; } = new();
    public IReadOnlyList<ThresholdOption> Thresholds { get; }
    [ObservableProperty] private ThresholdOption _selectedThreshold;
    public bool CanScan => Drives.Any(d => d.IsSelected) || CustomFolders.Count > 0;

    [RelayCommand]
    private void AddFolder()
    {
        var dlg = new OpenFolderDialog { Title = "Choose a folder to scan" };
        if (dlg.ShowDialog() != true) return;
        if (!CustomFolders.Contains(dlg.FolderName, StringComparer.OrdinalIgnoreCase)) CustomFolders.Add(dlg.FolderName);
    }

    [RelayCommand]
    private void RemoveFolder(string folder) => CustomFolders.Remove(folder);

    // ---- Scanning ----------------------------------------------------------------------------------
    [ObservableProperty] private string _scanCurrentFolder = "";
    [ObservableProperty] private string _scanStats = "";
    [ObservableProperty] private string _scanFound = "";
    [ObservableProperty] private int _deniedFolders;
    [ObservableProperty] private string _scanSummary = "";

    private List<string> CurrentRoots() =>
        Drives.Where(d => d.IsSelected).Select(d => d.Root).Concat(CustomFolders).ToList();

    [RelayCommand]
    private async Task ScanAsync()
    {
        var roots = CurrentRoots();
        if (roots.Count == 0) return;

        State = AppState.Scanning;
        ScanCurrentFolder = ""; ScanStats = "Starting…"; ScanFound = "";
        _cts = new CancellationTokenSource();

        var progress = new Progress<ScanProgress>(p =>
        {
            ScanCurrentFolder = p.CurrentFolder;
            ScanStats = $"{p.FilesScanned:N0} files · {Format.Size(p.BytesScanned)} checked";
            ScanFound = $"{Format.Size(p.FoundBytes)} cleanable found so far";
        });

        ScanResult result;
        try { result = await _scanner.ScanAsync(new ScanOptions(roots, SelectedThreshold.Bytes), progress, _cts.Token); }
        catch (Exception ex)
        {
            MessageBox.Show($"The scan failed: {ex.Message}", "WinCleaner", MessageBoxButton.OK, MessageBoxImage.Error);
            State = AppState.Home;
            return;
        }

        if (result.Cancelled) { State = AppState.Home; return; }

        LoadResults(result);
        State = AppState.Results;
    }

    [RelayCommand]
    private void CancelScan() => _cts?.Cancel();

    // ---- Results -----------------------------------------------------------------------------------
    public ObservableCollection<FindingItem> Items { get; } = new();
    public ICollectionView ItemsView { get; }
    public ObservableCollection<CategoryGroup> Categories { get; } = new();

    [ObservableProperty] private CategoryGroup? _selectedCategory;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private long _selectedBytes;
    [ObservableProperty] private bool _hasNoItems;

    public string SelectedText => SelectedCount == 0
        ? "Nothing selected"
        : $"{SelectedCount:N0} selected · {Format.Size(SelectedBytes)}";

    public bool ShowAdminBanner => DeniedFolders > 0 && !IsAdmin;

    partial void OnDeniedFoldersChanged(int value) => OnPropertyChanged(nameof(ShowAdminBanner));
    partial void OnSelectedCategoryChanged(CategoryGroup? value) => ItemsView.Refresh();
    partial void OnSearchTextChanged(string value) => ItemsView.Refresh();
    partial void OnSelectedCountChanged(int value)
    {
        OnPropertyChanged(nameof(SelectedText));
        OnPropertyChanged(nameof(HasSelection));
    }

    public bool HasSelection => SelectedCount > 0;
    partial void OnSelectedBytesChanged(long value) => OnPropertyChanged(nameof(SelectedText));

    private bool Matches(FindingItem i)
    {
        if (SelectedCategory is { } c && !c.Matches(i)) return false;
        if (string.IsNullOrWhiteSpace(SearchText)) return true;
        var q = SearchText.Trim();
        return i.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || i.Folder.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void LoadResults(ScanResult result)
    {
        _bulkChange = true;
        Items.Clear();
        foreach (var f in result.Findings) Items.Add(new FindingItem(f, OnItemSelectionChanged));
        _bulkChange = false;

        DeniedFolders = result.DeniedFolders;
        BuildCategories();
        RecomputeSelection();
        var cleanable = Items.Where(i => !i.IsLocked).Sum(i => i.Size);
        ScanSummary = $"Checked {result.FilesScanned:N0} files ({Format.Size(result.BytesScanned)}) in {result.Elapsed.TotalSeconds:F0}s · {Format.Size(cleanable)} can be cleaned";
    }

    private void BuildCategories()
    {
        var previous = SelectedCategory?.Key;
        Categories.Clear();
        Categories.Add(new CategoryGroup("all", "All cleanable items", RiskTier.Review, i => !i.IsLocked));
        foreach (var cat in Enum.GetValues<Category>())
        {
            var c = cat;
            var members = Items.Where(i => !i.IsLocked && i.Finding.Category == c).ToList();
            if (members.Count == 0) continue;
            var tier = members.All(m => m.Tier == RiskTier.Safe) ? RiskTier.Safe : RiskTier.Review;
            Categories.Add(new CategoryGroup(c.ToString(), CategoryInfo.Name(c), tier, i => !i.IsLocked && i.Finding.Category == c));
        }
        if (Items.Any(i => i.IsLocked))
            Categories.Add(new CategoryGroup("locked", "Protected (cannot be removed)", RiskTier.Locked, i => i.IsLocked));

        foreach (var g in Categories) g.Update(Items);
        SelectedCategory = Categories.FirstOrDefault(g => g.Key == previous) ?? Categories[0];
        HasNoItems = !Items.Any(i => !i.IsLocked);
    }

    private void OnItemSelectionChanged(FindingItem item)
    {
        if (_bulkChange) return;
        SelectedCount += item.IsSelected ? 1 : -1;
        SelectedBytes += item.IsSelected ? item.Size : -item.Size;
    }

    private void RecomputeSelection()
    {
        SelectedCount = Items.Count(i => i.IsSelected);
        SelectedBytes = Items.Where(i => i.IsSelected).Sum(i => i.Size);
    }

    private void SetSelection(IEnumerable<FindingItem> items, Func<FindingItem, bool> value)
    {
        _bulkChange = true;
        foreach (var i in items) i.IsSelected = !i.IsLocked && value(i);
        _bulkChange = false;
        RecomputeSelection();
    }

    /// <summary>Safe items only; items that need administrator rights are left alone unless we are elevated.</summary>
    [RelayCommand]
    private void SelectSafe() =>
        SetSelection(Items, i => i.Tier == RiskTier.Safe && (IsAdmin || !i.NeedsAdmin));

    [RelayCommand]
    private void SelectVisible()
    {
        _bulkChange = true;
        foreach (var o in ItemsView.Cast<FindingItem>()) o.IsSelected = !o.IsLocked;
        _bulkChange = false;
        RecomputeSelection();
    }

    [RelayCommand]
    private void ClearSelection() => SetSelection(Items, _ => false);

    [RelayCommand]
    private void SortBy(string column)
    {
        if (_sortColumn == column) _sortDescending = !_sortDescending;
        else { _sortColumn = column; _sortDescending = column != "Name"; }
        ApplySort();
    }

    private void ApplySort()
    {
        var prop = _sortColumn switch { "Name" => nameof(FindingItem.Name), "Modified" => nameof(FindingItem.Modified), _ => nameof(FindingItem.Size) };
        ItemsView.SortDescriptions.Clear();
        ItemsView.SortDescriptions.Add(new SortDescription(prop, _sortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending));
    }

    [RelayCommand]
    private void BackToHome() => State = AppState.Home;

    [RelayCommand]
    private void RestartAsAdmin()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" });
            Application.Current.Shutdown();
        }
        catch (System.ComponentModel.Win32Exception) { /* user declined the UAC prompt */ }
    }

    // ---- Confirm + clean ---------------------------------------------------------------------------
    [ObservableProperty] private bool _showConfirm;
    [ObservableProperty] private bool _permanentDelete;
    [ObservableProperty] private string _confirmTitle = "";
    [ObservableProperty] private bool _confirmHasBin;
    [ObservableProperty] private bool _confirmHasReview;
    [ObservableProperty] private int _confirmReviewCount;

    [RelayCommand]
    private void RequestClean()
    {
        var sel = Items.Where(i => i.IsSelected).ToList();
        if (sel.Count == 0) return;
        ConfirmTitle = $"Clean {sel.Count:N0} item{(sel.Count == 1 ? "" : "s")} ({Format.Size(sel.Sum(i => i.Size))})?";
        ConfirmHasBin = sel.Any(i => i.Finding.Category == Category.RecycleBin);
        ConfirmReviewCount = sel.Count(i => i.Tier == RiskTier.Review);
        ConfirmHasReview = ConfirmReviewCount > 0;
        PermanentDelete = false;
        ShowConfirm = true;
    }

    [RelayCommand]
    private void CancelConfirm() => ShowConfirm = false;

    [ObservableProperty] private int _cleanDone;
    [ObservableProperty] private int _cleanTotal;
    [ObservableProperty] private string _cleanCurrent = "";

    [RelayCommand]
    private async Task ConfirmCleanAsync()
    {
        ShowConfirm = false;
        _lastCleanItems = Items.Where(i => i.IsSelected && !i.IsLocked).ToList();
        var findings = _lastCleanItems.Select(i => i.Finding).ToList();
        var mode = PermanentDelete ? CleanMode.Permanent : CleanMode.RecycleBin;

        CleanDone = 0; CleanTotal = findings.Count; CleanCurrent = "";
        State = AppState.Cleaning;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var progress = new Progress<CleanProgress>(p => { CleanDone = p.Done; CleanTotal = p.Total; CleanCurrent = p.Current; });

        CleanResult result;
        try { result = await Task.Run(() => _cleaner.Clean(findings, mode, progress, ct)); }
        catch (Exception ex)
        {
            MessageBox.Show($"Cleaning failed: {ex.Message}", "WinCleaner", MessageBoxButton.OK, MessageBoxImage.Error);
            State = AppState.Results;
            return;
        }
        ShowSummary(result);
    }

    [RelayCommand]
    private void CancelClean() => _cts?.Cancel();

    // ---- Summary -----------------------------------------------------------------------------------
    public ObservableCollection<FailureItem> Failures { get; } = new();
    [ObservableProperty] private string _summaryHeadline = "";
    [ObservableProperty] private string _summaryDetail = "";
    [ObservableProperty] private bool _summaryHasFailures;
    [ObservableProperty] private string _failuresTitle = "";

    private void ShowSummary(CleanResult r)
    {
        _lastResult = r;
        var freed = r.BytesFreed + r.BytesRecycled;
        var recycled = r.Mode == CleanMode.RecycleBin && r.BytesRecycled > 0;

        SummaryHeadline = r.Mode == CleanMode.Permanent || r.BytesRecycled == 0
            ? $"{Format.Size(r.BytesFreed)} freed"
            : $"{Format.Size(r.BytesRecycled)} moved to the Recycle Bin";
        var parts = new List<string> { $"{r.ItemsCleaned:N0} item{(r.ItemsCleaned == 1 ? "" : "s")} cleaned" };
        if (recycled) parts.Add("Space is freed once you empty the Recycle Bin");
        if (r.BytesFreed > 0 && r.Mode == CleanMode.RecycleBin) parts.Add($"{Format.Size(r.BytesFreed)} permanently removed (Recycle Bin emptied)");
        if (r.Cancelled) parts.Add("Cancelled before finishing");
        SummaryDetail = string.Join(" · ", parts);

        Failures.Clear();
        foreach (var f in r.Failures) Failures.Add(new FailureItem(f.Path, f.Reason));
        SummaryHasFailures = Failures.Count > 0;
        FailuresTitle = $"{Failures.Count:N0} item{(Failures.Count == 1 ? " was" : "s were")} skipped or only partly cleaned";
        State = AppState.Summary;
    }

    /// <summary>Back to the results list, without the items that were cleaned.</summary>
    [RelayCommand]
    private void BackToResults()
    {
        if (_lastResult is { } r)
        {
            var gone = new HashSet<string>(r.CleanedPaths, StringComparer.OrdinalIgnoreCase);
            _bulkChange = true;
            foreach (var item in _lastCleanItems.Where(i => gone.Contains(i.Finding.Path))) Items.Remove(item);
            _bulkChange = false;
            _lastCleanItems = new();
            BuildCategories();
            RecomputeSelection();
        }
        State = AppState.Results;
    }

    [RelayCommand]
    private void OpenLog()
    {
        try
        {
            var path = _lastResult?.LogPath ?? Cleaner.DefaultLogPath;
            if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch { }
    }
}
