using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using VTalk.Core;

namespace VTalk.UI;

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value == null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class StatusToBrushConverter : IValueConverter
{
    private static readonly Brush Online = Freeze(new SolidColorBrush(Color.FromRgb(0x2E, 0xB8, 0x4B)));
    private static readonly Brush Busy = Freeze(new SolidColorBrush(Color.FromRgb(0xE0, 0x3B, 0x2E)));
    private static readonly Brush Away = Freeze(new SolidColorBrush(Color.FromRgb(0xF2, 0xA2, 0x1B)));
    private static readonly Brush Offline = Freeze(new SolidColorBrush(Color.FromRgb(0xA8, 0xB0, 0xBA)));

    private static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        PresenceStatus.Online => Online,
        PresenceStatus.Busy => Busy,
        PresenceStatus.Away => Away,
        _ => Offline,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
