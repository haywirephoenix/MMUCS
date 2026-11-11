using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using MMUCS.Core.Imaging;

namespace MMUCS.Desktop.Controls;

/// <summary>16x16 swatch grid for a 256-entry palette. Hover reports index and RGB.</summary>
public sealed class PaletteGrid : Control
{
    public static readonly StyledProperty<ScummColor[]> ColorsProperty =
        AvaloniaProperty.Register<PaletteGrid, ScummColor[]>(nameof(Colors));
    public static readonly DirectProperty<PaletteGrid, string> HoverTextProperty =
        AvaloniaProperty.RegisterDirect<PaletteGrid, string>(nameof(HoverText), o => o.HoverText);

    public ScummColor[] Colors { get => GetValue(ColorsProperty); set => SetValue(ColorsProperty, value); }

    private string _hoverText;
    public string HoverText { get => _hoverText; private set => SetAndRaise(HoverTextProperty, ref _hoverText, value); }

    static PaletteGrid() => AffectsRender<PaletteGrid>(ColorsProperty);

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsInfinity(availableSize.Width) ? 256 : availableSize.Width;
        return new Size(w, w);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var colors = Colors;
        if (colors == null) return;
        double cell = Math.Min(Bounds.Width, Bounds.Height) / 16;
        var p = e.GetPosition(this);
        int cx = (int)(p.X / cell), cy = (int)(p.Y / cell);
        if (cx < 0 || cy < 0 || cx > 15 || cy > 15) { HoverText = null; return; }
        int i = cy * 16 + cx;
        if (i >= colors.Length) { HoverText = $"#{i}"; return; }
        var c = colors[i];
        HoverText = $"#{i}  (0x{i:X2})  RGB {c.R8},{c.G8},{c.B8}  A {c.A8}";
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        HoverText = null;
    }

    public override void Render(DrawingContext ctx)
    {
        var colors = Colors;
        double cell = Math.Min(Bounds.Width, Bounds.Height) / 16;
        ctx.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (colors == null) return;

        for (int i = 0; i < 256; i++)
        {
            var rect = new Rect((i % 16) * cell, (i / 16) * cell, cell, cell).Deflate(0.5);
            if (i < colors.Length && colors[i].A > 0)
            {
                var c = colors[i];
                ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(c.R8, c.G8, c.B8)), rect);
            }
            else
            {
                ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), 1), rect.Deflate(cell * 0.3));
            }
        }
    }
}
