using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinCleaner.Core;

namespace WinCleaner.App.ViewModels;

public enum ExploreKind { Folder, File, Others }

/// <summary>One block in the treemap and one row in the list: a sub-folder, a file, or a group of small ones.</summary>
public partial class ExploreItem : ObservableObject
{
    private readonly Action<ExploreItem> _navigate, _reveal, _delete;

    public ExploreItem(ExploreKind kind, string name, long size, double percent, string detail, Color color,
        string? path, string? lockReason, FolderNode? node, string? fileName,
        Action<ExploreItem> navigate, Action<ExploreItem> reveal, Action<ExploreItem> delete)
    {
        Kind = kind; Name = name; Size = size; Percent = percent; Detail = detail; Color = color;
        Path = path; LockReason = lockReason; Node = node; FileName = fileName;
        _navigate = navigate; _reveal = reveal; _delete = delete;
        var b = new SolidColorBrush(color);
        b.Freeze();
        Brush = b;
    }

    public ExploreKind Kind { get; }
    public string Name { get; }
    public long Size { get; }
    public double Percent { get; }
    public string Detail { get; }
    public Color Color { get; }
    public Brush Brush { get; }

    /// <summary>Full path on disk; null for the "other files" / "smaller items" groups.</summary>
    public string? Path { get; }
    public FolderNode? Node { get; }
    public string? FileName { get; }

    /// <summary>Why this item may not be deleted (protected system location, etc.); null if it may be.</summary>
    public string? LockReason { get; }

    public bool IsFolder => Kind == ExploreKind.Folder;
    public bool ShowDelete => Kind != ExploreKind.Others;
    public bool CanDelete => ShowDelete && LockReason is null;
    public string DeleteTip => CanDelete ? "Delete…" : LockReason ?? "";
    public string SizeText => Format.Size(Size);
    public string PercentText => Size > 0 && Percent < 0.1 ? "<0.1%" : Percent.ToString("0.#") + "%";

    /// <summary>True while the pointer is over this item in either the treemap or the list.</summary>
    [ObservableProperty] private bool _isHot;

    [RelayCommand]
    private void Navigate()
    {
        if (IsFolder) _navigate(this);
    }

    [RelayCommand]
    private void Reveal() => _reveal(this);

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void Delete() => _delete(this);
}

/// <summary>One clickable segment of the path bar.</summary>
public sealed class Crumb
{
    public Crumb(FolderNode node, string text, bool isLast, Action<FolderNode> navigate)
    {
        Node = node;
        Text = text;
        IsLast = isLast;
        NavigateCommand = new RelayCommand(() => navigate(node));
    }

    public FolderNode Node { get; }
    public string Text { get; }
    public bool IsLast { get; }
    public ICommand NavigateCommand { get; }
}
