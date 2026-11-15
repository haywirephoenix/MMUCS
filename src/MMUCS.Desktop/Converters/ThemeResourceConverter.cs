using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace MMUCS.Desktop.Converters;

/// <summary>
/// Turns a resource key (e.g. "TagRoomBrush") into that resource from the app's resources, for the
/// app's current theme variant. Lets a view model choose a colour by name from Styles/Colors.axaml
/// without holding brushes itself. Null or unknown keys give null.
/// </summary>
public sealed class ThemeResourceConverter : IValueConverter
{
    public static readonly ThemeResourceConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var app = Application.Current;
        return value is string key && app != null && app.TryGetResource(key, app.ActualThemeVariant, out var resource)
            ? resource
            : null;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
