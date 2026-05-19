using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Rawr.Editor.App.Converters;

/// <summary>
/// bool → Visibility. Set <see cref="Invert"/> to map false→Visible. Set
/// <see cref="UseHidden"/> to map false → Hidden instead of Collapsed (keeps
/// the element's layout slot — useful when you want neighbouring controls to
/// stay aligned whether or not this one is showing).
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public bool UseHidden { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool b = value is true;
        if (Invert) b = !b;
        return b
            ? Visibility.Visible
            : (UseHidden ? Visibility.Hidden : Visibility.Collapsed);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Visible ? !Invert : Invert;
}
