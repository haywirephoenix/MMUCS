using System;
using System.Linq;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Input;
using MMUCS.Core.Scumm;
using SkiaSharp;

namespace MMUCS.Desktop.Controls;

/// <summary>
/// A decoded object image placed in the room, plus (for image-less hotspots) the frame of the
/// costumed actor that stands there, with its top-left corner in room coordinates.
/// </summary>
/// <summary>A costume frame being played elsewhere (the Costume viewer), drawn in place of <paramref name="Target"/>'s costume.</summary>
public sealed record LiveCostume(RoomObject Target, SKImage Image, int X, int Y, float Scale = 1, int ZPlane = 0);

/// <param name="CostumeDepth">The actor's y before elevation (<see cref="MMUCS.Core.Scumm.RoomActor.SortY"/>): SCUMM draws actors in order of it, lowest first.</param>
/// <param name="CostumeActor">The actor number, which breaks ties in that order.</param>
/// <param name="CostumeScale">The actor's scale as a factor; <see cref="CostumeX"/>/<see cref="CostumeY"/> are the scaled image's corner.</param>
public sealed record RoomObjectLayer(RoomObject Object, SKImage Image, SKImage CostumeImage = null, int CostumeX = 0, int CostumeY = 0,
    bool IsActorOnly = false, int CostumeDepth = 0, int CostumeActor = 0, float CostumeScale = 1, int CostumeZPlane = 0);

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

    /// <summary>When the background is a full-width object image, that object (gives its position).</summary>
    public static readonly StyledProperty<RoomObject> BackgroundObjectProperty =
        AvaloniaProperty.Register<RoomCanvas, RoomObject>(nameof(BackgroundObject));

    public static readonly StyledProperty<IReadOnlySet<RoomObject>> VisibleObjectsProperty =
        AvaloniaProperty.Register<RoomCanvas, IReadOnlySet<RoomObject>>(nameof(VisibleObjects));

    /// <summary>
    /// While true the canvas keeps showing what it last drew, so a room switch swaps the whole picture
    /// at once instead of showing the new room's size or overlays over the old background for a frame.
    /// </summary>
    public static readonly StyledProperty<bool> HoldSceneProperty =
        AvaloniaProperty.Register<RoomCanvas, bool>(nameof(HoldScene));

    /// <summary>The room's z-plane masks (index 0 = plane 1), opaque where they hide actors; null for none.</summary>
    public static readonly StyledProperty<IReadOnlyList<SKImage>> ZMasksProperty =
        AvaloniaProperty.Register<RoomCanvas, IReadOnlyList<SKImage>>(nameof(ZMasks));

    /// <summary>Every selected object (the Objects panel's multi-selection), outlined like the current one.</summary>
    public static readonly StyledProperty<IReadOnlySet<RoomObject>> SelectedObjectsProperty =
        AvaloniaProperty.Register<RoomCanvas, IReadOnlySet<RoomObject>>(nameof(SelectedObjects));

    /// <summary>Whether actors are cut by their z-plane.</summary>
    public static readonly StyledProperty<bool> MaskActorsProperty =
        AvaloniaProperty.Register<RoomCanvas, bool>(nameof(MaskActors), true);

    /// <summary>The z-plane overlay: -1 none, 0 every plane, N plane N.</summary>
    public static readonly StyledProperty<int> ZPlaneOverlayProperty =
        AvaloniaProperty.Register<RoomCanvas, int>(nameof(ZPlaneOverlay), -1);

    public static readonly StyledProperty<LiveCostume> LiveCostumeProperty =
        AvaloniaProperty.Register<RoomCanvas, LiveCostume>(nameof(LiveCostume));

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
    /// <summary>Objects whose image is drawn (hidden ones keep their hotspot, so they can be found and shown again).</summary>
    public IReadOnlySet<RoomObject> VisibleObjects { get => GetValue(VisibleObjectsProperty); set => SetValue(VisibleObjectsProperty, value); }
    public bool HoldScene { get => GetValue(HoldSceneProperty); set => SetValue(HoldSceneProperty, value); }
    /// <summary>The Costume viewer's playing frame, shown in place of that costume (Play in place).</summary>
    public LiveCostume LiveCostume { get => GetValue(LiveCostumeProperty); set => SetValue(LiveCostumeProperty, value); }
    public IReadOnlyList<SKImage> ZMasks { get => GetValue(ZMasksProperty); set => SetValue(ZMasksProperty, value); }
    public bool MaskActors { get => GetValue(MaskActorsProperty); set => SetValue(MaskActorsProperty, value); }
    public IReadOnlySet<RoomObject> SelectedObjects { get => GetValue(SelectedObjectsProperty); set => SetValue(SelectedObjectsProperty, value); }

    /// <summary>A Ctrl- or Shift-click on an object: the view adds it to or toggles it in the selection.</summary>
    public event Action<RoomObject, KeyModifiers> ObjectModifierClicked;
    public int ZPlaneOverlay { get => GetValue(ZPlaneOverlayProperty); set => SetValue(ZPlaneOverlayProperty, value); }

    /// <summary>Raised on a right-click (not a right-drag) with the object under the pointer, or null.</summary>
    public event Action<RoomObject> ObjectContextRequested;

    public RoomObject BackgroundObject { get => GetValue(BackgroundObjectProperty); set => SetValue(BackgroundObjectProperty, value); }
    /// <summary>Draw full-width (640px) object images even over a background; otherwise only when selected.</summary>
    public bool ShowFullScreenObjects { get => GetValue(ShowFullScreenObjectsProperty); set => SetValue(ShowFullScreenObjectsProperty, value); }

    private string _hoverText;
    public string HoverText { get => _hoverText; private set => SetAndRaise(HoverTextProperty, ref _hoverText, value); }

    static RoomCanvas()
    {
        AffectsRender<RoomCanvas>(
            BackgroundImageProperty, ObjectLayersProperty, WalkboxesProperty, SelectedObjectProperty,
            ShowBackgroundProperty, ShowObjectsProperty, ShowHotspotsProperty, ShowWalkboxesProperty,
            ShowLabelsProperty, ShowBoundsProperty, ShowFullScreenObjectsProperty, BackgroundObjectProperty, VisibleObjectsProperty, LiveCostumeProperty,
            ZMasksProperty, MaskActorsProperty, ZPlaneOverlayProperty, SelectedObjectsProperty);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == HoldSceneProperty)
        {
            if (!HoldScene) ContentChanged(); // re-fit to the new content and draw it
            return;
        }
        if (HoldScene) return; // applied when the hold ends
        if (change.Property == RoomSizeProperty || change.Property == BackgroundImageProperty
            || change.Property == ObjectLayersProperty || change.Property == BackgroundObjectProperty)
            ContentChanged();
    }

    protected override Size ContentSize
    {
        get
        {
            var s = RoomSize;
            if (s.Width > 0 && s.Height > 0) return s;
            var bg = BackgroundImage;
            if (bg != null)
            {
                var o = BackgroundObject;
                return new Size((o?.X ?? 0) + bg.Width, (o?.Y ?? 0) + bg.Height);
            }

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

    protected override bool HoldFrame => HoldScene;

    protected override IViewportScene CreateScene() => new Scene(
        ShowBackground ? BackgroundImage : null,
        ObjectLayers, // per-object visibility (incl. the View menu's Objects toggle) is in VisibleObjects
        ObjectLayers,
        ShowWalkboxes ? Walkboxes : null,
        SelectedObject,
        ShowHotspots,
        ShowLabels,
        ShowBounds,
        ContentSize,
        ShowFullScreenObjects,
        BackgroundObject,
        VisibleObjects,
        LiveCostume,
        MaskActors ? ZMasks : null,
        ZPlaneOverlay >= 0 ? ZMasks : null,
        ZPlaneOverlay,
        SelectedObjects);

    protected override void OnContentClicked(Point p, PointerPressedEventArgs e)
    {
        var mods = e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta | KeyModifiers.Shift);
        var hits = HitTestObjects(p);
        if (mods != KeyModifiers.None)
        {
            // Ctrl / Shift: the topmost thing under the pointer joins or leaves the selection.
            if (hits.Count > 0) ObjectModifierClicked?.Invoke(hits[0], mods);
        }
        else
        {
            // Clicking what's already selected goes through to the next thing under it (then back to the top).
            int at = SelectedObject == null ? -1 : hits.IndexOf(SelectedObject);
            SelectedObject = hits.Count == 0 ? null : hits[(at + 1) % hits.Count];
        }
        e.Handled = true;
    }

    protected override void OnContentContextRequested(Point p) => ObjectContextRequested?.Invoke(HitTestObject(p));

    protected override void OnContentHover(Point p)
    {
        var obj = HitTestObject(p);
        string pos = $"{(int)Math.Floor(p.X)}, {(int)Math.Floor(p.Y)}";
        HoverText = obj != null ? $"{pos}  —  {obj}" : pos;
    }

    /// <summary>Topmost, smallest object whose rect contains the point (smallest wins for nested objects).</summary>
    private RoomObject HitTestObject(Point p) => HitTestObjects(p).FirstOrDefault();

    /// <summary>
    /// Everything under a point, topmost first, as it's drawn: actors in reverse draw order (on their
    /// visible pixels: not see-through, not cut by their z-plane), then object images in reverse draw
    /// order (on their visible pixels), then image-less hotspots, smallest first.
    /// </summary>
    private List<RoomObject> HitTestObjects(Point p)
    {
        var result = new List<RoomObject>();
        var layers = ObjectLayers;
        if (layers == null) return result;
        var visible = VisibleObjects;
        bool Shown(RoomObject o) => visible == null || visible.Contains(o);
        void Add(RoomObject o) { if (!result.Contains(o)) result.Add(o); }

        // Actors, front first: the reverse of SCUMM's draw order.
        foreach (var l in layers.Where(l => l.CostumeImage != null && Shown(l.Object))
                                .OrderByDescending(l => l.CostumeDepth).ThenByDescending(l => l.CostumeActor))
        {
            float s = l.CostumeScale <= 0 ? 1 : l.CostumeScale;
            double ix = (p.X - l.CostumeX) / s, iy = (p.Y - l.CostumeY) / s;
            if (!Opaque(l.CostumeImage, ix, iy)) continue;
            var masks = MaskActors ? ZMasks : null;
            if (l.CostumeZPlane > 0 && masks != null && l.CostumeZPlane <= masks.Count && Opaque(masks[l.CostumeZPlane - 1], p.X, p.Y))
                continue; // behind the foreground there
            Add(l.Object);
        }

        // Object images, front first (later layers are drawn over earlier ones).
        if (ShowObjects)
            for (int i = layers.Count - 1; i >= 0; i--)
            {
                var l = layers[i];
                if (l.Image == null || !Shown(l.Object) || ReferenceEquals(l.Object, BackgroundObject)) continue;
                if (l.Image.Width >= 640 && !ShowFullScreenObjects) continue; // not drawn
                if (Opaque(l.Image, p.X - l.Object.X, p.Y - l.Object.Y)) Add(l.Object);
            }

        // Hotspots (objects without a drawn image), smallest first, as before.
        foreach (var o in layers.Select(l => l.Object)
                     .Where(o => o.Width > 0 && o.Height > 0 && p.X >= o.X && p.Y >= o.Y && p.X < o.X + o.Width && p.Y < o.Y + o.Height)
                     .OrderBy(o => (double)o.Width * o.Height))
            Add(o);
        return result;
    }

    /// <summary>Whether an image has a visible pixel at (x, y) in its own pixels.</summary>
    private static bool Opaque(SKImage image, double x, double y)
    {
        int px = (int)Math.Floor(x), py = (int)Math.Floor(y);
        if (image == null || px < 0 || py < 0 || px >= image.Width || py >= image.Height) return false;
        using var pixmap = image.PeekPixels();
        return pixmap == null || pixmap.GetPixelColor(px, py).Alpha > 0; // not raster: count the box
    }

    private sealed class Scene : IViewportScene
    {
        private readonly SKImage _bg;
        private readonly IReadOnlyList<RoomObjectLayer> _drawLayers;
        private readonly IReadOnlyList<RoomObjectLayer> _allLayers;
        private readonly IReadOnlyList<Walkbox> _boxes;
        private readonly RoomObject _selected;
        private readonly bool _hotspots, _labels, _bounds, _fullScreenObjects;
        private readonly RoomObject _bgObject;
        private readonly IReadOnlySet<RoomObject> _visible;
        private readonly LiveCostume _live;
        private readonly IReadOnlyList<SKImage> _zMasks;
        private readonly IReadOnlyList<SKImage> _overlayMasks;
        private readonly int _overlayPlane;
        private readonly IReadOnlySet<RoomObject> _selectedAll;
        private readonly Size _size;

        public Scene(SKImage bg, IReadOnlyList<RoomObjectLayer> drawLayers, IReadOnlyList<RoomObjectLayer> allLayers,
            IReadOnlyList<Walkbox> boxes, RoomObject selected, bool hotspots, bool labels, bool bounds, Size size, bool fullScreenObjects, RoomObject bgObject, IReadOnlySet<RoomObject> visible, LiveCostume live, IReadOnlyList<SKImage> zMasks,
            IReadOnlyList<SKImage> overlayMasks, int overlayPlane, IReadOnlySet<RoomObject> selectedAll)
        {
            _bg = bg; _drawLayers = drawLayers; _allLayers = allLayers; _boxes = boxes;
            _selected = selected; _hotspots = hotspots; _labels = labels; _bounds = bounds; _size = size; _fullScreenObjects = fullScreenObjects; _bgObject = bgObject; _visible = visible; _live = live; _zMasks = zMasks;
            _overlayMasks = overlayMasks; _overlayPlane = overlayPlane; _selectedAll = selectedAll;
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

        private static readonly SKPaint MaskOut = new() { BlendMode = SKBlendMode.DstOut };
        /// <summary>Plane 1 red, 2 green, 3 blue, 4 yellow, 5 magenta, 6 cyan, 7 white; half see-through.</summary>
        private static readonly SKColor[] PlaneColors =
        {
            new(255, 0, 0, 110), new(0, 255, 0, 110), new(0, 0, 255, 110), new(255, 255, 0, 110),
            new(255, 0, 255, 110), new(0, 255, 255, 110), new(255, 255, 255, 110),
        };
        // An alpha-only image is drawn in the paint's colour.
        private static readonly SKPaint PlanePaint = new();

        /// <summary>
        /// Draws an actor's frame with the pixels behind its z-plane removed: the frame goes into a layer
        /// and the plane's mask (room coordinates) cuts it out, as the engine skips masked pixels.
        /// </summary>
        private void DrawActor(SKCanvas canvas, SKImage image, int x, int y, float scale, int zplane, SKSamplingOptions sampling)
        {
            var mask = zplane > 0 && _zMasks != null && zplane <= _zMasks.Count ? _zMasks[zplane - 1] : null;
            if (mask == null)
            {
                DrawScaled(canvas, image, x, y, scale, sampling);
                return;
            }
            canvas.SaveLayer(SKRect.Create(x, y, image.Width * scale, image.Height * scale), null);
            DrawScaled(canvas, image, x, y, scale, sampling);
            canvas.DrawImage(mask, 0, 0, MaskOut);
            canvas.Restore();
        }

        private static void DrawScaled(SKCanvas canvas, SKImage image, int x, int y, float scale, SKSamplingOptions sampling)
        {
            if (scale == 1) canvas.DrawImage(image, x, y, sampling);
            else canvas.DrawImage(image, SKRect.Create(x, y, image.Width * scale, image.Height * scale), sampling);
        }

        public void Render(SKCanvas canvas, in ViewTransform view, SKRect viewBounds)
        {
            // ── Content-space pass (pixel art) ──
            canvas.Save();
            canvas.Concat(view.Matrix);
            var sampling = PickSampling(canvas);

            // The chosen background: the room's own image, or a full-width object image at its position.
            if (_bg != null)
                canvas.DrawImage(_bg, _bgObject?.X ?? 0, _bgObject?.Y ?? 0, sampling);

            if (_drawLayers != null)
            {
                foreach (var layer in _drawLayers)
                {
                    if (layer.Image == null || ReferenceEquals(layer.Object, _bgObject)) continue; // already the background
                    if (_visible == null || !_visible.Contains(layer.Object)) continue;        // toggled off, or not in the current list (stale layers)
                    // Other full-screen (640px) object images would cover the background (in the wrong
                    // palette), so they're drawn only when asked for; the Background spinbox switches to them.
                    bool isFullWidth = layer.Image.Width >= 640;
                    if (isFullWidth && !_fullScreenObjects) continue;
                    canvas.DrawImage(layer.Image, layer.Object.X, layer.Object.Y, sampling);
                }

                // The z-planes, tinted, between the scene and the actors: what each plane hides shows
                // through the actors' cut-outs.
                if (_overlayMasks != null)
                    for (int k = 0; k < _overlayMasks.Count; k++)
                        if (_overlayMasks[k] != null && (_overlayPlane == 0 || _overlayPlane == k + 1))
                        {
                            PlanePaint.Color = PlaneColors[k % PlaneColors.Length];
                            canvas.DrawImage(_overlayMasks[k], 0, 0, PlanePaint);
                        }

                // Actors are drawn over the objects, in SCUMM's order: by y before elevation, then actor
                // number, so an actor lifted by elevation still sorts by where it stands (cannon-v's cannon
                // stands at y 792 to be drawn in front of the water).
                foreach (var layer in _drawLayers.Where(l => l.CostumeImage != null).OrderBy(l => l.CostumeDepth).ThenBy(l => l.CostumeActor))
                {
                    if (layer.CostumeImage == null || _visible == null || !_visible.Contains(layer.Object)) continue;
                    if (_live != null && ReferenceEquals(layer.Object, _live.Target)) continue; // replaced by the playing frame
                    DrawActor(canvas, layer.CostumeImage, layer.CostumeX, layer.CostumeY, layer.CostumeScale, layer.CostumeZPlane, sampling);
                }
                // Played in place even when its entry is hidden: it was asked for from the Costume viewer.
                if (_live?.Image != null) DrawActor(canvas, _live.Image, _live.X, _live.Y, _live.Scale, _live.ZPlane, sampling);
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

            if (_allLayers != null && (_hotspots || _selected != null || _selectedAll is { Count: > 0 }))
            {
                foreach (var layer in _allLayers)
                {
                    var o = layer.Object;
                    bool isSel = ReferenceEquals(o, _selected) || _selectedAll?.Contains(o) == true;
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
