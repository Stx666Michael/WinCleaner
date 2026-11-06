using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using WinCleaner.Core;

namespace WinCleaner.App;

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => !(value is true);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => !(value is true);
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Visible when the bound AppState equals the converter parameter ("Home", "Results"...).</summary>
public sealed class StateToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class TierBrushConverter : IValueConverter
{
    public static readonly Brush Safe = Frozen(Color.FromRgb(0x1E, 0x9E, 0x5A));
    public static readonly Brush Review = Frozen(Color.FromRgb(0xC7, 0x7C, 0x00));
    public static readonly Brush Locked = Frozen(Color.FromRgb(0x7A, 0x7F, 0x87));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        RiskTier.Safe => Safe,
        RiskTier.Review => Review,
        _ => Locked,
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();

    internal static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
}

public sealed class TierBackgroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var c = ((SolidColorBrush)new TierBrushConverter().Convert(value, targetType, parameter, culture)).Color;
        return TierBrushConverter.Frozen(Color.FromArgb(0x26, c.R, c.G, c.B));
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
