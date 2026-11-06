using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WinCleaner.App.ViewModels;
using WinCleaner.Core;

namespace WinCleaner.App.Controls;

/// <summary>
/// Draws the items of a folder as a squarified treemap: block area = size. Hovering highlights a block
/// (shared with the list through <see cref="HoverItem"/>), clicking a folder block opens it.
/// </summary>
public sealed class TreemapControl : FrameworkElement
{
    private const int MaxBlocks = 80;
    private static readonly Color TailColor = Color.FromRgb(0x45, 0x4B, 0x54);

    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(
        nameof(Items), typeof(IReadOnlyList<ExploreItem>), typeof(TreemapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender,
            (d, _) => ((TreemapControl)d).OnItemsChanged()));

    public static readonly DependencyProperty HoverItemProperty = DependencyProperty.Register(
        nameof(HoverItem), typeof(ExploreItem), typeof(TreemapControl),
        new FrameworkPropertyMetadata(null,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public IReadOnlyList<ExploreItem>? Items
    {
        get => (IReadOnlyList<ExploreItem>?)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public ExploreItem? HoverItem
    {
        get => (ExploreItem?)GetValue(HoverItemProperty);
        set => SetValue(HoverItemProperty, value);
    }

    private sealed record Block(Rect Rect, ExploreItem? Item, Color Color, string Name, string Sub, string Tip);

    private List<Block>? _blocks;
    private Size _laidOutFor;
    private string? _tip;

    private void OnItemsChanged()
    {
        _blocks = null;
        // A quick fade makes it clear that you moved to another folder.
        BeginAnimation(OpacityProperty, new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(180)));
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _blocks = null;
        InvalidateVisual();
    }

    private void EnsureLayout()
    {
        var size = new Size(ActualWidth, ActualHeight);
        if (_blocks is not null && size == _laidOutFor) return;
        _laidOutFor = size;

        var blocks = new List<Block>();
        var items = Items?.Where(i => i.Size > 0).OrderByDescending(i => i.Size).ToList() ?? new List<ExploreItem>();
        if (items.Count > 0 && size.Width > 4 && size.Height > 4)
        {
            var head = items.Take(MaxBlocks).ToList();
            var tail = items.Skip(MaxBlocks).ToList();
            var values = head.Select(i => (double)i.Size).ToList();
            if (tail.Count > 0) values.Add(tail.Sum(i => (double)i.Size));

            var rects = Treemap.Layout(values, new RectD(0, 0, size.Width, size.Height));
            for (int i = 0; i < head.Count; i++)
            {
                var it = head[i];
                blocks.Add(new Block(ToRect(rects[i]), it, it.Color, it.Name,
                    $"{it.SizeText} · {it.PercentText}",
                    $"{it.Name}\n{it.SizeText} ({it.PercentText})" + (it.IsFolder ? "\nClick to open" : "")));
            }
            if (tail.Count > 0)
            {
                long sum = tail.Sum(i => i.Size);
                blocks.Add(new Block(ToRect(rects[^1]), null, TailColor, $"{tail.Count:N0} smaller items", Format.Size(sum),
                    $"{tail.Count:N0} smaller items\n{Format.Size(sum)} together"));
            }
        }
        _blocks = blocks;
    }

    private static Rect ToRect(RectD r) => new(r.X, r.Y, r.W, r.H);

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        EnsureLayout();
        var hotPen = new Pen(Brushes.White, 2);
        hotPen.Freeze();

        foreach (var b in _blocks!)
        {
            var r = new Rect(b.Rect.X + 1, b.Rect.Y + 1, Math.Max(0, b.Rect.Width - 2), Math.Max(0, b.Rect.Height - 2));
            if (r.Width < 1 || r.Height < 1) continue;

            bool hot = b.Item is not null && ReferenceEquals(b.Item, HoverItem);
            var fill = new SolidColorBrush(hot ? Lighten(b.Color, 0.22) : b.Color);
            fill.Freeze();
            dc.DrawRoundedRectangle(fill, hot ? hotPen : null, r, 4, 4);
            DrawLabel(dc, r, b.Name, b.Sub);
        }
    }

    private void DrawLabel(DrawingContext dc, Rect r, string name, string sub)
    {
        if (r.Width < 58 || r.Height < 24) return;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var family = (FontFamily)GetValue(TextElement.FontFamilyProperty);
        var bold = new Typeface(family, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var normal = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        dc.PushClip(new RectangleGeometry(r));
        var title = new FormattedText(name, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, bold, 13, Brushes.White, dpi)
        {
            MaxTextWidth = Math.Max(1, r.Width - 12),
            MaxTextHeight = 18,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        dc.DrawText(title, new Point(r.X + 6, r.Y + 5));

        if (r.Height >= 46)
        {
            var subBrush = new SolidColorBrush(Color.FromArgb(0xD0, 255, 255, 255));
            subBrush.Freeze();
            var s = new FormattedText(sub, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, normal, 12, subBrush, dpi)
            {
                MaxTextWidth = Math.Max(1, r.Width - 12),
                MaxTextHeight = 16,
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            dc.DrawText(s, new Point(r.X + 6, r.Y + 5 + title.Height + 1));
        }
        dc.Pop();
    }

    private static Color Lighten(Color c, double amount) => Color.FromRgb(
        (byte)(c.R + (255 - c.R) * amount), (byte)(c.G + (255 - c.G) * amount), (byte)(c.B + (255 - c.B) * amount));

    private Block? Hit(Point p) => _blocks?.FirstOrDefault(b => b.Rect.Contains(p));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var b = Hit(e.GetPosition(this));
        if (!ReferenceEquals(b?.Item, HoverItem)) HoverItem = b?.Item;
        Cursor = b?.Item is { IsFolder: true } ? Cursors.Hand : Cursors.Arrow;

        var tip = b?.Tip;
        if (tip != _tip) { _tip = tip; ToolTip = tip; }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        HoverItem = null;
        _tip = null;
        ToolTip = null;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (Hit(e.GetPosition(this))?.Item is { IsFolder: true } item) item.NavigateCommand.Execute(null);
    }
}
