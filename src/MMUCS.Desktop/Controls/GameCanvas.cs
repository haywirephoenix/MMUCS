using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using MMUCS.Player;
using MMUCS.Runtime;
using SkiaSharp;

namespace MMUCS.Desktop.Controls;

/// <summary>
/// The game window's canvas: hosts the player (MMUCS.Player) by handing its Skia canvas to the current
/// <see cref="Frame"/>, which draws the 16:9 picture letterboxed to fit. Pointer positions go to the game
/// through <see cref="ToGame"/>; <see cref="ToControl"/> and <see cref="PictureRect"/> go the other way.
/// </summary>
public sealed class GameCanvas : Control
{
    public static readonly StyledProperty<GameFrame> FrameProperty =
        AvaloniaProperty.Register<GameCanvas, GameFrame>(nameof(Frame));

    /// <summary>The frame to show (the player captures a new one whenever the game changes).</summary>
    public GameFrame Frame { get => GetValue(FrameProperty); set => SetValue(FrameProperty, value); }

    static GameCanvas()
    {
        AffectsRender<GameCanvas>(FrameProperty);
        ClipToBoundsProperty.OverrideDefaultValue<GameCanvas>(true);
    }

    private SKRect Viewport => new(0, 0, (float)Bounds.Width, (float)Bounds.Height);

    /// <summary>The picture's rectangle in the control.</summary>
    public Rect PictureRect
    {
        get
        {
            var r = GameView.PictureRect(Viewport);
            return new Rect(r.Left, r.Top, r.Width, r.Height);
        }
    }

    /// <summary>A point in the control in game units (not clamped).</summary>
    public Point ToGame(Point control)
    {
        var p = GameView.ToGame(Viewport, new SKPoint((float)control.X, (float)control.Y));
        return new Point(p.X, p.Y);
    }

    /// <summary>A point in game units in the control.</summary>
    public Point ToControl(Point game)
    {
        var p = GameView.ToView(Viewport, new GamePoint(game.X, game.Y));
        return new Point(p.X, p.Y);
    }

    public override void Render(DrawingContext context)
    {
        if (Frame is { } frame) context.Custom(new FrameDrawOperation(new Rect(Bounds.Size), frame));
        else context.FillRectangle(Brushes.Black, new Rect(Bounds.Size));
    }

    /// <summary>Draws a frame on the render thread, onto the Skia canvas Avalonia draws with.</summary>
    private sealed class FrameDrawOperation(Rect bounds, GameFrame frame) : ICustomDrawOperation
    {
        public Rect Bounds { get; } = bounds;
        public bool HitTest(Point p) => Bounds.Contains(p);
        public bool Equals(ICustomDrawOperation other) => false;
        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature) return;
            using var lease = feature.Lease();
            frame.Render(lease.SkCanvas, new SKRect(0, 0, (float)Bounds.Width, (float)Bounds.Height));
        }
    }
}
