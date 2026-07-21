using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Rawr.Editor.App.Converters;

/// <summary>
/// null → Collapsed, anything else → Visible; <see cref="Invert"/> swaps the
/// two. Used by the Masks panel, whose editor only exists while a mask is
/// selected and whose empty-state hint only exists while one is not.
///
/// <para>Distinct from <see cref="BoolToVisibilityConverter"/> rather than folded
/// into it: that one answers "is this flag set", and routing a reference through
/// it would make <c>false</c> and <c>null</c> indistinguishable — a selected mask
/// whose value happened to be <c>false</c> would read as no selection at all.</para>
/// </summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool present = value is not null;
        if (Invert) present = !present;
        return present ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
