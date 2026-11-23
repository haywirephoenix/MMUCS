using System;
using System.Collections.Generic;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace MMUCS.Desktop.Controls;

/// <summary>
/// The game window's canvas: a <see cref="PictureSize"/> picture (16:9) scaled to the largest rectangle
/// that fits, letterboxed in black, with the game cursor drawn on it (nearest-neighbour, so the pixels
/// stay square) and a stand-in hotspot outlined. Positions are in game units; <see cref="ToGame"/> and
/// <see cref="ToControl"/> convert.
/// </summary>
public sealed class GameCanvas : Control
{
    public static readonly StyledProperty<Size> PictureSizeProperty =
        AvaloniaProperty.Register<GameCanvas, Size>(nameof(PictureSize), new Size(1280, 720));
    public static readonly StyledProperty<Bitmap> CursorImageProperty =
        AvaloniaProperty.Register<GameCanvas, Bitmap>(nameof(CursorImage));
    public static readonly StyledProperty<Point> CursorPositionProperty =
        AvaloniaProperty.Register<GameCanvas, Point>(nameof(CursorPosition));
    public static readonly StyledProperty<Point> CursorHotspotProperty =
        AvaloniaProperty.Register<GameCanvas, Point>(nameof(CursorHotspot));
    public static readonly StyledProperty<bool> ShowCursorProperty =
        AvaloniaProperty.Register<GameCanvas, bool>(nameof(ShowCursor));
    public static readonly StyledProperty<Rect> HotspotProperty =
        AvaloniaProperty.Register<GameCanvas, Rect>(nameof(Hotspot));
    public static readonly StyledProperty<bool> IsHotspotHoveredProperty =
        AvaloniaProperty.Register<GameCanvas, bool>(nameof(IsHotspotHovered));
    public static readonly StyledProperty<Bitmap> CoinImageProperty =
        AvaloniaProperty.Register<GameCanvas, Bitmap>(nameof(CoinImage));
    public static readonly StyledProperty<Rect> CoinBoundsProperty =
        AvaloniaProperty.Register<GameCanvas, Rect>(nameof(CoinBounds));
    public static readonly StyledProperty<IReadOnlyList<ViewModels.CanvasBlast>> BlastsProperty =
        AvaloniaProperty.Register<GameCanvas, IReadOnlyList<ViewModels.CanvasBlast>>(nameof(Blasts));
    public static readonly StyledProperty<Rendering.SubtitleOverlay> TextProperty =
        AvaloniaProperty.Register<GameCanvas, Rendering.SubtitleOverlay>(nameof(Text));
    public static readonly StyledProperty<Point> PageOriginProperty =
        AvaloniaProperty.Register<GameCanvas, Point>(nameof(PageOrigin));
    public static readonly StyledProperty<bool> IsRunningProperty =
        AvaloniaProperty.Register<GameCanvas, bool>(nameof(IsRunning));

    /// <summary>The picture's size in game units; its aspect ratio is kept whatever the control's size.</summary>
    public Size PictureSize { get => GetValue(PictureSizeProperty); set => SetValue(PictureSizeProperty, value); }
    public Bitmap CursorImage { get => GetValue(CursorImageProperty); set => SetValue(CursorImageProperty, value); }
    /// <summary>Where the cursor points, in game units.</summary>
    public Point CursorPosition { get => GetValue(CursorPositionProperty); set => SetValue(CursorPositionProperty, value); }
    /// <summary>The pixel of <see cref="CursorImage"/> that sits on <see cref="CursorPosition"/>.</summary>
    public Point CursorHotspot { get => GetValue(CursorHotspotProperty); set => SetValue(CursorHotspotProperty, value); }
    public bool ShowCursor { get => GetValue(ShowCursorProperty); set => SetValue(ShowCursorProperty, value); }
    /// <summary>A stand-in hotspot, in game units.</summary>
    public Rect Hotspot { get => GetValue(HotspotProperty); set => SetValue(HotspotProperty, value); }
    public bool IsHotspotHovered { get => GetValue(IsHotspotHoveredProperty); set => SetValue(IsHotspotHoveredProperty, value); }
    /// <summary>Images drawn over the room, back to front (the open inventory), placed in game units.</summary>
    public IReadOnlyList<ViewModels.CanvasBlast> Blasts { get => GetValue(BlastsProperty); set => SetValue(BlastsProperty, value); }
    /// <summary>Text drawn over everything but the cursor, in pixels of the game's 640×480 page (a hover label).</summary>
    public Rendering.SubtitleOverlay Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    /// <summary>Where the 640×480 page's top left sits, in game units.</summary>
    public Point PageOrigin { get => GetValue(PageOriginProperty); set => SetValue(PageOriginProperty, value); }
    /// <summary>The verb coin's frame (null when it isn't shown), drawn under the cursor.</summary>
    public Bitmap CoinImage { get => GetValue(CoinImageProperty); set => SetValue(CoinImageProperty, value); }
    /// <summary>Where <see cref="CoinImage"/> goes, in game units.</summary>
    public Rect CoinBounds { get => GetValue(CoinBoundsProperty); set => SetValue(CoinBoundsProperty, value); }
    /// <summary>Whether the game is playing (the picture is dimmed when it isn't).</summary>
    public bool IsRunning { get => GetValue(IsRunningProperty); set => SetValue(IsRunningProperty, value); }

    private static readonly IBrush Letterbox = Brushes.Black;
    private static readonly IBrush Stage = new SolidColorBrush(Color.FromRgb(0x1B, 0x1F, 0x2A));
    private static readonly IBrush StageStopped = new SolidColorBrush(Color.FromRgb(0x14, 0x15, 0x19));
    private static readonly IBrush HotspotFill = new SolidColorBrush(Color.FromArgb(0x30, 0x7D, 0xCF, 0xE8));
    private static readonly IBrush HotspotHoverFill = new SolidColorBrush(Color.FromArgb(0x50, 0xE8, 0xC0, 0x7D));
    private static readonly IPen HotspotPen = new Pen(new SolidColorBrush(Color.FromArgb(0xA0, 0x7D, 0xCF, 0xE8)), 1, DashStyle.Dash);
    private static readonly IPen HotspotHoverPen = new Pen(new SolidColorBrush(Color.FromRgb(0xE8, 0xC0, 0x7D)), 1.5);

    static GameCanvas()
    {
        AffectsRender<GameCanvas>(PictureSizeProperty, CursorImageProperty, CursorPositionProperty, CursorHotspotProperty,
            ShowCursorProperty, HotspotProperty, IsHotspotHoveredProperty, IsRunningProperty, CoinImageProperty, CoinBoundsProperty, BlastsProperty, TextProperty, PageOriginProperty);
        ClipToBoundsProperty.OverrideDefaultValue<GameCanvas>(true);
    }

    /// <summary>The picture's rectangle in the control, the largest of its aspect ratio that fits, centred.</summary>
    public Rect PictureRect
    {
        get
        {
            var game = PictureSize;
            var bounds = Bounds.Size;
            if (game.Width <= 0 || game.Height <= 0 || bounds.Width <= 0 || bounds.Height <= 0) return default;
            double scale = Math.Min(bounds.Width / game.Width, bounds.Height / game.Height);
            double w = game.Width * scale, h = game.Height * scale;
            return new Rect((bounds.Width - w) / 2, (bounds.Height - h) / 2, w, h);
        }
    }

    /// <summary>Control units per game unit.</summary>
    public double Scale => PictureSize.Width > 0 ? PictureRect.Width / PictureSize.Width : 1;

    /// <summary>A point in the control in game units (not clamped).</summary>
    public Point ToGame(Point control)
    {
        var r = PictureRect;
        double s = Scale;
        return s <= 0 ? default : new Point((control.X - r.X) / s, (control.Y - r.Y) / s);
    }

    /// <summary>A point in game units in the control.</summary>
    public Point ToControl(Point game)
    {
        var r = PictureRect;
        double s = Scale;
        return new Point(r.X + game.X * s, r.Y + game.Y * s);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Letterbox, bounds);
        var rect = PictureRect;
        if (rect.Width <= 0) return;
        context.FillRectangle(IsRunning ? Stage : StageStopped, rect);

        double s = Scale;
        if (IsRunning && Hotspot.Width > 0)
        {
            var h = new Rect(ToControl(Hotspot.TopLeft), ToControl(Hotspot.BottomRight));
            context.DrawRectangle(IsHotspotHovered ? HotspotHoverFill : HotspotFill, IsHotspotHovered ? HotspotHoverPen : HotspotPen, h);
        }

        using (context.PushClip(rect))
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.None }))
        {
            if (Blasts != null)
                foreach (var blast in Blasts)
                {
                    var b = blast.Bitmap;
                    context.DrawImage(b, new Rect(0, 0, b.PixelSize.Width, b.PixelSize.Height),
                        new Rect(ToControl(blast.Bounds.TopLeft), ToControl(blast.Bounds.BottomRight)));
                }
            if (CoinImage is { } coin)
            {
                var size = coin.PixelSize;
                context.DrawImage(coin, new Rect(0, 0, size.Width, size.Height), new Rect(ToControl(CoinBounds.TopLeft), ToControl(CoinBounds.BottomRight)));
            }
            if (Text is { IsEmpty: false } text)
                context.Custom(new TextDrawOperation(new Rect(Bounds.Size), rect, ToControl(PageOrigin), s, text));
            if (ShowCursor && CursorImage is { } cursor)
            {
                var at = ToControl(CursorPosition);
                var size = cursor.PixelSize;
                var dest = new Rect(at.X - CursorHotspot.X * s, at.Y - CursorHotspot.Y * s, size.Width * s, size.Height * s);
                context.DrawImage(cursor, new Rect(0, 0, size.Width, size.Height), dest);
            }
        }
    }

    /// <summary>Draws a text overlay with Skia, scaled from page pixels to the control (bitmap fonts stay pixel-sharp).</summary>
    private sealed class TextDrawOperation(Rect bounds, Rect clip, Point origin, double scale, Rendering.SubtitleOverlay text) : ICustomDrawOperation
    {
        public Rect Bounds { get; } = bounds;
        public bool HitTest(Point p) => false;
        public bool Equals(ICustomDrawOperation other) => false;
        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature) return;
            using var lease = feature.Lease();
            var canvas = lease.SkCanvas;
            canvas.Save();
            canvas.ClipRect(new SKRect((float)clip.X, (float)clip.Y, (float)clip.Right, (float)clip.Bottom));
            canvas.Translate((float)origin.X, (float)origin.Y);
            canvas.Scale((float)scale);
            text.Draw(canvas, smooth: scale < 0.999);
            canvas.Restore();
        }
    }
}
