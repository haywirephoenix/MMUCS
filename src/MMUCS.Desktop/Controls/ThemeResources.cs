using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using SkiaSharp;

namespace MMUCS.Desktop.Controls;

/// <summary>
/// Looks up the named colours in Styles/Colors.axaml from code, for controls that draw themselves.
/// Lookups follow the control's theme variant; use <see cref="OnThemeChanged"/> to drop cached
/// brushes and repaint when the theme or resources change.
/// </summary>
internal static class ThemeResources
{
    /// <summary>Shown for a key that isn't defined, so a missing colour is easy to spot.</summary>
    private static readonly IBrush Missing = new ImmutableSolidColorBrush(0xFFFF00FF);

    public static IBrush FindBrush(this Control control, string key)
    {
        if (control.TryFindResource(key, control.ActualThemeVariant, out var value))
        {
            if (value is IBrush brush) return brush;
            if (value is Color color) return new ImmutableSolidColorBrush(color);
        }
        return Missing;
    }

    public static Color FindColor(this Control control, string key) =>
        control.TryFindResource(key, control.ActualThemeVariant, out var value) switch
        {
            true when value is Color c => c,
            true when value is ISolidColorBrush b => b.Color.WithAlpha(b.Opacity),
            _ => Colors.Magenta,
        };

    public static SKColor FindSKColor(this Control control, string key) => control.FindColor(key).ToSKColor();

    public static SKColor ToSKColor(this Color c) => new(c.R, c.G, c.B, c.A);

    /// <summary>Calls <paramref name="changed"/> when the control's theme variant or resources change.</summary>
    public static void OnThemeChanged(this Control control, Action changed)
    {
        control.ActualThemeVariantChanged += (_, _) => changed();
        control.ResourcesChanged += (_, _) => changed();
    }

    private static Color WithAlpha(this Color c, double opacity) =>
        opacity >= 1 ? c : Color.FromArgb((byte)Math.Round(c.A * opacity), c.R, c.G, c.B);
}
