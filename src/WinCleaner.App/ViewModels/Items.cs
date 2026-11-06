using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinCleaner.Core;

namespace WinCleaner.App.ViewModels;

public partial class DriveItem : ObservableObject
{
    public string Root { get; }
    public string Title { get; }
    public double UsedPercent { get; }
    public string UsageText { get; }

    [ObservableProperty] private bool _isSelected;

    public DriveItem(DriveInfo d, bool selected)
    {
        Root = d.Name;
        var label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "Local Disk" : d.VolumeLabel;
        Title = $"{label} ({d.Name.TrimEnd('\\')})";
        long total = d.TotalSize, free = d.TotalFreeSpace;
        UsedPercent = total > 0 ? (total - free) * 100.0 / total : 0;
        UsageText = $"{Format.Size(free)} free of {Format.Size(total)}";
        _isSelected = selected;
    }

    [RelayCommand]
    private void Toggle() => IsSelected = !IsSelected;
}

public partial class FindingItem : ObservableObject
{
    private readonly Action<FindingItem> _selectionChanged;

    public Finding Finding { get; }
    public string Name { get; }
    public string Folder { get; }
    public string Reason => Finding.Reason;
    public long Size => Finding.Size;
    public string SizeText { get; }
    public DateTime Modified { get; }
    public string ModifiedText { get; }
    public RiskTier Tier => Finding.Tier;
    public string TierText => Finding.Tier.ToString();
    public bool IsLocked => Finding.Tier == RiskTier.Locked;
    public bool NeedsAdmin => Finding.NeedsAdmin;
    public string Icon => Finding.Category == Category.RecycleBin ? "Delete24"
        : Finding.IsDirectory ? "Folder24" : "Document24";

    [ObservableProperty] private bool _isSelected;

    public FindingItem(Finding f, Action<FindingItem> selectionChanged)
    {
        Finding = f;
        _selectionChanged = selectionChanged;
        Name = f.Label ?? System.IO.Path.GetFileName(f.Path);
        Folder = f.ContentsOnly ? f.Path : System.IO.Path.GetDirectoryName(f.Path) ?? f.Path;
        SizeText = Format.Size(f.Size);
        Modified = f.ModifiedUtc.ToLocalTime();
        ModifiedText = Modified.ToString("yyyy-MM-dd");
    }

    partial void OnIsSelectedChanged(bool value) => _selectionChanged(this);

    [RelayCommand]
    private void OpenLocation()
    {
        try
        {
            if (Finding.Category == Category.RecycleBin) Process.Start(new ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder") { UseShellExecute = true });
            else if (Finding.ContentsOnly) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Finding.Path}\"") { UseShellExecute = true });
            else Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{Finding.Path}\"") { UseShellExecute = true });
        }
        catch { /* nothing useful to do if Explorer cannot open */ }
    }
}

/// <summary>One row in the left-hand list: a category, "All", or the protected items.</summary>
public partial class CategoryGroup : ObservableObject
{
    public string Key { get; }
    public string Name { get; }
    public RiskTier Tier { get; }
    public Func<FindingItem, bool> Matches { get; }

    [ObservableProperty] private int _count;
    [ObservableProperty] private string _sizeText = "";

    public CategoryGroup(string key, string name, RiskTier tier, Func<FindingItem, bool> matches)
    {
        Key = key; Name = name; Tier = tier; Matches = matches;
    }

    public void Update(IEnumerable<FindingItem> all)
    {
        long bytes = 0; int n = 0;
        foreach (var i in all)
            if (Matches(i)) { n++; bytes += i.Size; }
        Count = n;
        SizeText = Format.Size(bytes);
    }
}

public sealed record ThresholdOption(string Label, long Bytes);
public sealed record FailureItem(string Path, string Reason);
