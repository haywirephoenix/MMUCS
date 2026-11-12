using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Input;
using MMUCS.Core.Scumm;
using SkiaSharp;

namespace MMUCS.Desktop.Controls;

/// <summary>A decoded object image placed in the room.</summary>
public sealed record RoomObjectLayer(RoomObject Object, SKImage Image);

/// <summary>
/// Room viewer: background, object images, object bounds (hotspots), walkbox polygons and room bounds.
/// Overlay strokes are drawn in screen space so they stay 1px-crisp at any zoom level.
/// </summary>
public sealed class RoomCanvas : SkiaViewport
{
    public static readonly StyledProperty<SKImage> BackgroundImageProperty =
        AvaloniaProperty.Register<RoomCanvas, SKImage>(nameof(BackgroundImage));
    public static readonly StyledProperty<IReadOnlyList<RoomObjectLayer>> ObjectLayersProperty =
        AvaloniaProperty.Register<RoomCanvas, IReadOnlyList<RoomObjectLayer>>(nameof(ObjectLayers));
    public static readonly StyledProperty<IReadOnlyList<Walkbox>> WalkboxesProperty =
        AvaloniaProperty.Register<RoomCanvas, IReadOnlyList<Walkbox>>(nameof(Walkboxes));
    public static readonly StyledProperty<Size> RoomSizeProperty =
        AvaloniaProperty.Register<RoomCanvas, Size>(nameof(RoomSize));
    public static readonly StyledProperty<RoomObject> SelectedObjectProperty =
        AvaloniaProperty.Register<RoomCanvas, RoomObject>(nameof(SelectedObject), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<bool> ShowBackgroundProperty =
        AvaloniaProperty.Register<RoomCanvas, bool>(nameof(ShowBackground), true);
    public static readonly StyledProperty<bool> ShowObjectsProperty =
        AvaloniaProperty.Register<RoomCanvas, bool>(nameof(ShowObjects), true);
    public static readonly StyledProperty<bool> ShowHotspotsProperty =
        AvaloniaProperty.Register<RoomCanvas, bool>(nameof(ShowHotspots));
    public static readonly StyledProperty<bool> ShowWalkboxesProperty =
        AvaloniaProperty.Register<RoomCanvas, bool>(nameof(ShowWalkboxes));
    public static readonly StyledProperty<bool> ShowLabelsProperty =
        AvaloniaProperty.Register<RoomCanvas, bool>(nameof(ShowLabels), true);
    public static readonly StyledProperty<bool> ShowBoundsProperty =
        AvaloniaProperty.Register<RoomCanvas, bool>(nameof(ShowBounds));

    public static readonly StyledProperty<bool> ShowFullScreenObjectsProperty =
        AvaloniaProperty.Register<RoomCanvas, bool>(nameof(ShowFullScreenObjects));

    public static readonly DirectProperty<RoomCanvas, string> HoverTextProperty =
        AvaloniaProperty.RegisterDirect<RoomCanvas, string>(nameof(HoverText), o => o.HoverText);

    public SKImage BackgroundImage { get => GetValue(BackgroundImageProperty); set => SetValue(BackgroundImageProperty, value); }
    public IReadOnlyList<RoomObjectLayer> ObjectLayers { get => GetValue(ObjectLayersProperty); set => SetValue(ObjectLayersProperty, value); }
    public IReadOnlyList<Walkbox> Walkboxes { get => GetValue(WalkboxesProperty); set => SetValue(WalkboxesProperty, value); }
    public Size RoomSize { get => GetValue(RoomSizeProperty); set => SetValue(RoomSizeProperty, value); }
    public RoomObject SelectedObject { get => GetValue(SelectedObjectProperty); set => SetValue(SelectedObjectProperty, value); }
    public bool ShowBackground { get => GetValue(ShowBackgroundProperty); set => SetValue(ShowBackgroundProperty, value); }
    public bool ShowObjects { get => GetValue(ShowObjectsProperty); set => SetValue(ShowObjectsProperty, value); }
    public bool ShowHotspots { get => GetValue(ShowHotspotsProperty); set => SetValue(ShowHotspotsProperty, value); }
    public bool ShowWalkboxes { get => GetValue(ShowWalkboxesProperty); set => SetValue(ShowWalkboxesProperty, value); }
    public bool ShowLabels { get => GetValue(ShowLabelsProperty); set => SetValue(ShowLabelsProperty, value); }
    public bool ShowBounds { get => GetValue(ShowBoundsProperty); set => SetValue(ShowBoundsProperty, value); }
    /// <summary>Draw full-width (640px) object images even over a background; otherwise only when selected.</summary>
    public bool ShowFullScreenObjects { get => GetValue(ShowFullScreenObjectsProperty); set => SetValue(ShowFullScreenObjectsProperty, value); }

    private string _hoverText;
    public string HoverText { get => _hoverText; private set => SetAndRaise(HoverTextProperty, ref _hoverText, value); }

    static RoomCanvas()
    {
        AffectsRender<RoomCanvas>(
            BackgroundImageProperty, ObjectLayersProperty, WalkboxesProperty, SelectedObjectProperty,
            ShowBackgroundProperty, ShowObjectsProperty, ShowHotspotsProperty, ShowWalkboxesProperty,
            ShowLabelsProperty, ShowBoundsProperty, ShowFullScreenObjectsProperty);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RoomSizeProperty || change.Property == BackgroundImageProperty
            || change.Property == ObjectLayersProperty)
            ContentChanged();
    }

    protected override Size ContentSize
    {
        get
        {
            var s = RoomSize;
            if (s.Width > 0 && s.Height > 0) return s;
            var bg = BackgroundImage;
            if (bg != null) return new Size(bg.Width, bg.Height);

            // Background-less rooms (e.g. the icons/inventory library): fit to the objects' extent.
            double w = 0, h = 0;
            foreach (var l in ObjectLayers ?? Array.Empty<RoomObjectLayer>())
            {
                if (l.Image == null) continue;
                w = Math.Max(w, l.Object.X + l.Image.Width);
                h = Math.Max(h, l.Object.Y + l.Image.Height);
            }
            return w > 0 && h > 0 ? new Size(w, h) : default;
        }
    }

    protected override IViewportScene CreateScene() => new Scene(
        ShowBackground ? BackgroundImage : null,
        ShowObjects ? ObjectLayers : null,
        ObjectLayers,
        ShowWalkboxes ? Walkboxes : null,
        SelectedObject,
        ShowHotspots,
        ShowLabels,
        ShowBounds,
        ContentSize,
        ShowFullScreenObjects || BackgroundImage == null);

    protected override void OnContentClicked(Point p, PointerPressedEventArgs e)
    {
        SelectedObject = HitTestObject(p);
        e.Handled = true;
    }

    protected override void OnContentHover(Point p)
    {
        var obj = HitTestObject(p);
        string pos = $"{(int)Math.Floor(p.X)}, {(int)Math.Floor(p.Y)}";
        HoverText = obj != null ? $"{pos}  —  {obj}" : pos;
    }

    /// <summary>Topmost, smallest object whose rect contains the point (smallest wins for nested objects).</summary>
    private RoomObject HitTestObject(Point p)
    {
        var layers = ObjectLayers;
        if (layers == null) return null;
        RoomObject best = null;
        double bestArea = double.MaxValue;
        foreach (var l in layers)
        {
            var o = l.Object;
            if (o.Width <= 0 || o.Height <= 0) continue;
            if (p.X < o.X || p.Y < o.Y || p.X >= o.X + o.Width || p.Y >= o.Y + o.Height) continue;
            double area = (double)o.Width * o.Height;
            if (area < bestArea) { best = o; bestArea = area; }
        }
        return best;
    }

    private sealed class Scene : IViewportScene
    {
        private readonly SKImage _bg;
        private readonly IReadOnlyList<RoomObjectLayer> _drawLayers;
        private readonly IReadOnlyList<RoomObjectLayer> _allLayers;
        private readonly IReadOnlyList<Walkbox> _boxes;
        private readonly RoomObject _selected;
        private readonly bool _hotspots, _labels, _bounds, _fullScreenObjects;
        private readonly Size _size;

        public Scene(SKImage bg, IReadOnlyList<RoomObjectLayer> drawLayers, IReadOnlyList<RoomObjectLayer> allLayers,
            IReadOnlyList<Walkbox> boxes, RoomObject selected, bool hotspots, bool labels, bool bounds, Size size, bool fullScreenObjects)
        {
            _bg = bg; _drawLayers = drawLayers; _allLayers = allLayers; _boxes = boxes;
            _selected = selected; _hotspots = hotspots; _labels = labels; _bounds = bounds; _size = size; _fullScreenObjects = fullScreenObjects;
        }

        private static readonly SKPaint HotspotFill = new() { Color = new SKColor(255, 220, 0, 38), Style = SKPaintStyle.Fill };
        private static readonly SKPaint HotspotStroke = new() { Color = new SKColor(255, 220, 0, 170), Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = false };
        private static readonly SKPaint SelectStroke = new() { Color = new SKColor(80, 200, 255), Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = false };
        private static readonly SKPaint BoxFill = new() { Color = new SKColor(0, 255, 140, 40), Style = SKPaintStyle.Fill, IsAntialias = true };
        private static readonly SKPaint BoxStroke = new() { Color = new SKColor(0, 255, 140, 210), Style = SKPaintStyle.Stroke, StrokeWidth = 1.25f, IsAntialias = true };
        private static readonly SKPaint BoundsStroke = new() { Color = SKColors.Orange, Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
        private static readonly SKPaint LabelPaint = new() { Color = new SKColor(255, 235, 120), IsAntialias = true };
        private static readonly SKPaint LabelShadow = new() { Color = new SKColor(0, 0, 0, 200), IsAntialias = true };
        private static readonly SKPaint BoxLabelPaint = new() { Color = new SKColor(160, 255, 200), IsAntialias = true };
        private static readonly SKFont LabelFont = new(SKTypeface.Default, 11) { Subpixel = true, Edging = SKFontEdging.SubpixelAntialias };

        public void Render(SKCanvas canvas, in ViewTransform view, SKRect viewBounds)
        {
            // ── Content-space pass (pixel art) ──
            canvas.Save();
            canvas.Concat(view.Matrix);
            var sampling = PickSampling(canvas);

            if (_bg != null)
                canvas.DrawImage(_bg, 0, 0, sampling);

            if (_drawLayers != null)
            {
                foreach (var layer in _drawLayers)
                {
                    if (layer.Image == null) continue;
                    // Full-screen (640px) object images would cover the background, so they're drawn only
                    // when selected, when asked for, or in rooms that have no background to cover.
                    bool isFullWidth = layer.Image.Width >= 640;
                    if (isFullWidth && !_fullScreenObjects && !ReferenceEquals(layer.Object, _selected)) continue;
                    canvas.DrawImage(layer.Image, layer.Object.X, layer.Object.Y, sampling);
                }
            }
            canvas.Restore();

            // ── Screen-space pass (crisp vector overlays) ──
            if (_boxes != null)
            {
                using var path = new SKPath();
                foreach (var b in _boxes)
                {
                    if (b.IsPlaceholder) continue;
                    path.Reset();
                    path.MoveTo(view.Map(b.UpperLeft.X, b.UpperLeft.Y));
                    path.LineTo(view.Map(b.UpperRight.X, b.UpperRight.Y));
                    path.LineTo(view.Map(b.LowerRight.X, b.LowerRight.Y));
                    path.LineTo(view.Map(b.LowerLeft.X, b.LowerLeft.Y));
                    path.Close();
                    canvas.DrawPath(path, BoxFill);
                    canvas.DrawPath(path, BoxStroke);

                    if (_labels)
                    {
                        var c = view.Map(
                            (b.UpperLeft.X + b.UpperRight.X + b.LowerRight.X + b.LowerLeft.X) / 4.0,
                            (b.UpperLeft.Y + b.UpperRight.Y + b.LowerRight.Y + b.LowerLeft.Y) / 4.0);
                        canvas.DrawText(b.Index.ToString(), c.X, c.Y, SKTextAlign.Center, LabelFont, BoxLabelPaint);
                    }
                }
            }

            if (_allLayers != null && (_hotspots || _selected != null))
            {
                foreach (var layer in _allLayers)
                {
                    var o = layer.Object;
                    bool isSel = ReferenceEquals(o, _selected);
                    if (!_hotspots && !isSel) continue;

                    int w = o.Width > 0 ? o.Width : 100, h = o.Height > 0 ? o.Height : 100;
                    var r = Snap(view.Map(new SKRect(o.X, o.Y, o.X + w, o.Y + h)));

                    if (_hotspots)
                    {
                        canvas.DrawRect(r, HotspotFill);
                        canvas.DrawRect(r, HotspotStroke);
                    }
                    if (isSel) canvas.DrawRect(r, SelectStroke);

                    if (_labels && (_hotspots || isSel))
                    {
                        string text = o.ToString();
                        canvas.DrawText(text, r.Left + 4, r.Top + 13, SKTextAlign.Left, LabelFont, LabelShadow);
                        canvas.DrawText(text, r.Left + 3, r.Top + 12, SKTextAlign.Left, LabelFont, LabelPaint);
                    }
                }
            }

            if (_bounds && _size.Width > 0)
                canvas.DrawRect(Snap(view.Map(new SKRect(0, 0, (float)_size.Width, (float)_size.Height))), BoundsStroke);
        }

        /// <summary>Nearest-neighbour when magnifying (crisp pixels), mipmapped linear when minifying (no shimmer).</summary>
        internal static SKSamplingOptions PickSampling(SKCanvas canvas)
        {
            float scale = canvas.TotalMatrix.ScaleX;
            return scale >= 0.999f
                ? new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None)
                : new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
        }

        /// <summary>Align a 1px stroke rect to pixel centres.</summary>
        private static SKRect Snap(SKRect r) => new(
            MathF.Floor(r.Left) + 0.5f, MathF.Floor(r.Top) + 0.5f,
            MathF.Floor(r.Right) - 0.5f, MathF.Floor(r.Bottom) - 0.5f);
    }
}
