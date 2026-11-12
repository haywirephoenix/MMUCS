using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace MMUCS.Desktop.Controls;

/// <summary>Maps content (image pixel) space to control (DIP) space.</summary>
public readonly record struct ViewTransform(double Zoom, double OffsetX, double OffsetY)
{
    public SKPoint Map(double x, double y) => new((float)(x * Zoom + OffsetX), (float)(y * Zoom + OffsetY));
    public SKRect Map(SKRect r) => new(
        (float)(r.Left * Zoom + OffsetX), (float)(r.Top * Zoom + OffsetY),
        (float)(r.Right * Zoom + OffsetX), (float)(r.Bottom * Zoom + OffsetY));
    public Point Unmap(Point p) => new((p.X - OffsetX) / Zoom, (p.Y - OffsetY) / Zoom);
    public SKMatrix Matrix => SKMatrix.CreateScaleTranslation((float)Zoom, (float)Zoom, (float)OffsetX, (float)OffsetY);
}

/// <summary>
/// An immutable snapshot of what to draw. Created on the UI thread, rendered on the render thread,
/// so implementations must not reference mutable control state.
/// </summary>
public interface IViewportScene
{
    void Render(SKCanvas canvas, in ViewTransform view, SKRect viewBounds);
}

/// <summary>
/// Base for Skia-backed pan/zoom viewports. Renders only on invalidation (zero idle cost):
///  - Mouse wheel zooms around the cursor; middle/right drag (or Space+left drag) pans.
///  - Double-click (or <see cref="ZoomToFit"/>) frames the content.
/// Content is drawn through <see cref="ICustomDrawOperation"/> directly onto Avalonia's SKCanvas.
/// </summary>
public abstract class SkiaViewport : Avalonia.Controls.Control
{
    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<SkiaViewport, double>(nameof(Zoom), 1.0);

    public static readonly StyledProperty<bool> ShowCheckerboardProperty =
        AvaloniaProperty.Register<SkiaViewport, bool>(nameof(ShowCheckerboard), true);

    public static readonly StyledProperty<bool> AutoFitProperty =
        AvaloniaProperty.Register<SkiaViewport, bool>(nameof(AutoFit), true);

    public double Zoom { get => GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }
    public bool ShowCheckerboard { get => GetValue(ShowCheckerboardProperty); set => SetValue(ShowCheckerboardProperty, value); }
    /// <summary>Re-frame content whenever its size changes.</summary>
    public bool AutoFit { get => GetValue(AutoFitProperty); set => SetValue(AutoFitProperty, value); }

    public const double MinZoom = 0.05;
    public const double MaxZoom = 64.0;

    private Vector _offset;
    private Point? _panStart;
    private bool _panIsRightButton, _panMoved;
    private Vector _panStartOffset;
    private bool _spaceHeld;
    private Size _lastContentSize;
    private bool _fitPending = true;

    protected Vector Offset => _offset;
    protected ViewTransform View => new(Zoom, _offset.X, _offset.Y);

    static SkiaViewport()
    {
        AffectsRender<SkiaViewport>(ZoomProperty, ShowCheckerboardProperty);
        FocusableProperty.OverrideDefaultValue<SkiaViewport>(true);
        ClipToBoundsProperty.OverrideDefaultValue<SkiaViewport>(true);
    }

    /// <summary>Size of the content in content pixels (e.g. room width/height).</summary>
    protected abstract Size ContentSize { get; }

    /// <summary>Build an immutable snapshot of the scene for the render thread.</summary>
    protected abstract IViewportScene CreateScene();

    /// <summary>Call when content changes; re-fits if the content size changed and AutoFit is on.</summary>
    protected void ContentChanged()
    {
        var size = ContentSize;
        if (size != _lastContentSize)
        {
            _lastContentSize = size;
            if (AutoFit) _fitPending = true;
        }
        // Never fit from inside Render(): Avalonia 12 throws if a visual is invalidated mid-render.
        if (_fitPending && Bounds.Width > 0) ZoomToFit();
        InvalidateVisual();
    }

    public void ZoomToFit()
    {
        var content = ContentSize;
        var bounds = Bounds.Size;
        if (content.Width <= 0 || content.Height <= 0 || bounds.Width <= 0 || bounds.Height <= 0) return;

        const double pad = 16;
        double z = Math.Min((bounds.Width - pad * 2) / content.Width, (bounds.Height - pad * 2) / content.Height);
        z = Math.Clamp(z, MinZoom, MaxZoom);

        // Snap to integer scale when magnifying so pixels stay perfectly square on screen.
        double dpi = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        if (z * dpi >= 2) z = Math.Floor(z * dpi) / dpi;

        Zoom = z;
        _offset = new Vector(
            Math.Round((bounds.Width - content.Width * z) / 2),
            Math.Round((bounds.Height - content.Height * z) / 2));
        _fitPending = false;
        InvalidateVisual();
    }

    public void ZoomToActual()
    {
        double dpi = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        ZoomAround(new Point(Bounds.Width / 2, Bounds.Height / 2), 1.0 / dpi);
    }

    public void ZoomAround(Point anchor, double newZoom)
    {
        newZoom = Math.Clamp(newZoom, MinZoom, MaxZoom);
        var contentPt = View.Unmap(anchor);
        Zoom = newZoom;
        _offset = new Vector(anchor.X - contentPt.X * newZoom, anchor.Y - contentPt.Y * newZoom);
        InvalidateVisual();
    }

    /// <summary>Converts a control-space point to content-space.</summary>
    public Point ToContent(Point controlPoint) => View.Unmap(controlPoint);

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (AutoFit || _fitPending) ZoomToFit();
    }

    // ── Input ────────────────────────────────────────────────────────────────

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        double factor = Math.Pow(1.2, e.Delta.Y);
        ZoomAround(e.GetPosition(this), Zoom * factor);
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var props = e.GetCurrentPoint(this).Properties;

        if (e.ClickCount == 2 && props.IsLeftButtonPressed)
        {
            ZoomToFit();
            e.Handled = true;
            return;
        }

        if (props.IsMiddleButtonPressed || props.IsRightButtonPressed || (props.IsLeftButtonPressed && _spaceHeld))
        {
            _panStart = e.GetPosition(this);
            _panStartOffset = _offset;
            _panIsRightButton = props.IsRightButtonPressed;
            _panMoved = false;
            e.Pointer.Capture(this);
            Cursor = new Cursor(StandardCursorType.SizeAll);
            e.Handled = true;
            return;
        }

        if (props.IsLeftButtonPressed)
            OnContentClicked(View.Unmap(e.GetPosition(this)), e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_panStart is { } start)
        {
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - start.X) > 3 || Math.Abs(p.Y - start.Y) > 3) _panMoved = true;
            _offset = _panStartOffset + (p - start);
            InvalidateVisual();
        }
        OnContentHover(View.Unmap(e.GetPosition(this)));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_panStart != null)
        {
            bool contextClick = _panIsRightButton && !_panMoved;
            _panStart = null;
            e.Pointer.Capture(null);
            Cursor = Cursor.Default;
            // A right-click that didn't drag is a context-menu request, not a pan.
            if (contextClick) OnContentContextRequested(View.Unmap(e.GetPosition(this)));
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Space: _spaceHeld = true; e.Handled = true; break;
            case Key.F: ZoomToFit(); e.Handled = true; break;
            case Key.D1: case Key.NumPad1: ZoomToActual(); e.Handled = true; break;
            case Key.OemPlus: case Key.Add: ZoomAround(new Point(Bounds.Width / 2, Bounds.Height / 2), Zoom * 1.25); e.Handled = true; break;
            case Key.OemMinus: case Key.Subtract: ZoomAround(new Point(Bounds.Width / 2, Bounds.Height / 2), Zoom / 1.25); e.Handled = true; break;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key == Key.Space) _spaceHeld = false;
    }

    protected virtual void OnContentClicked(Point contentPoint, PointerPressedEventArgs e) { }
    protected virtual void OnContentHover(Point contentPoint) { }
    protected virtual void OnContentContextRequested(Point contentPoint) { }

    // ── Rendering ────────────────────────────────────────────────────────────

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        // Transparent fill guarantees pointer hit-testing across the whole surface.
        context.FillRectangle(Brushes.Transparent, bounds);
        context.Custom(new SceneDrawOperation(bounds, View, ShowCheckerboard, ContentSize, CreateScene()));
    }

    private sealed class SceneDrawOperation : ICustomDrawOperation
    {
        private readonly ViewTransform _view;
        private readonly bool _checker;
        private readonly Size _contentSize;
        private readonly IViewportScene _scene;

        public SceneDrawOperation(Rect bounds, ViewTransform view, bool checker, Size contentSize, IViewportScene scene)
        {
            Bounds = bounds;
            _view = view;
            _checker = checker;
            _contentSize = contentSize;
            _scene = scene;
        }

        public Rect Bounds { get; }
        public bool HitTest(Point p) => Bounds.Contains(p);
        public bool Equals(ICustomDrawOperation other) => false;
        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (feature == null) return;

            using var lease = feature.Lease();
            var canvas = lease.SkCanvas;
            canvas.Save();
            var clip = new SKRect(0, 0, (float)Bounds.Width, (float)Bounds.Height);
            canvas.ClipRect(clip);

            if (_checker && _contentSize.Width > 0)
                DrawCheckerboard(canvas, _view.Map(new SKRect(0, 0, (float)_contentSize.Width, (float)_contentSize.Height)));

            _scene?.Render(canvas, _view, clip);
            canvas.Restore();
        }

        private static readonly SKPaint CheckerPaint = CreateCheckerPaint();

        private static SKPaint CreateCheckerPaint()
        {
            using var bmp = new SKBitmap(16, 16);
            using (var c = new SKCanvas(bmp))
            {
                c.Clear(new SKColor(0x2A, 0x2A, 0x2E));
                using var p = new SKPaint { Color = new SKColor(0x34, 0x34, 0x39) };
                c.DrawRect(0, 0, 8, 8, p);
                c.DrawRect(8, 8, 8, 8, p);
            }
            return new SKPaint
            {
                Shader = SKShader.CreateBitmap(bmp, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat),
            };
        }

        private static void DrawCheckerboard(SKCanvas canvas, SKRect rect) => canvas.DrawRect(rect, CheckerPaint);
    }
}
