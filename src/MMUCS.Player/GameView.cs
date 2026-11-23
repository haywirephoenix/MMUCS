using System;
using System.Linq;
using MMUCS.Player.Text;
using MMUCS.Runtime;
using SkiaSharp;

namespace MMUCS.Player;

/// <summary>
/// Shows a running <see cref="GameState"/>: <see cref="Capture"/> takes a <see cref="GameFrame"/> of it (on the
/// thread that drives the game), which any host draws onto a Skia canvas of any size. The host passes its input
/// to the game in game units, using <see cref="ToGame"/> for where its pointer is.
/// </summary>
public sealed class GameView
{
    private string _labelFor;
    private SubtitleRenderer _labelRenderer;
    private byte[] _labelPalette;
    private SubtitleOverlay _label;

    public GameView(GameState game) => Game = game;

    public GameState Game { get; }

    /// <summary>The font the game's text (the hover label) is drawn in; no text is drawn without one.</summary>
    public SubtitleRenderer Text { get; set; }

    /// <summary>The 16:9 picture's place in <paramref name="viewport"/>: as large as it fits, centred.</summary>
    public static SKRect PictureRect(SKRect viewport)
    {
        if (viewport.Width <= 0 || viewport.Height <= 0) return SKRect.Empty;
        float scale = (float)Math.Min(viewport.Width / GameState.CanvasWidth, viewport.Height / GameState.CanvasHeight);
        float w = (float)(GameState.CanvasWidth * scale), h = (float)(GameState.CanvasHeight * scale);
        float x = viewport.Left + (viewport.Width - w) / 2, y = viewport.Top + (viewport.Height - h) / 2;
        return new SKRect(x, y, x + w, y + h);
    }

    /// <summary>A point of <paramref name="viewport"/> in game units (not clamped to the picture).</summary>
    public static GamePoint ToGame(SKRect viewport, SKPoint point)
    {
        var picture = PictureRect(viewport);
        double scale = picture.Width / GameState.CanvasWidth;
        return scale <= 0 ? default : new GamePoint((point.X - picture.Left) / scale, (point.Y - picture.Top) / scale);
    }

    /// <summary>A point in game units in <paramref name="viewport"/>.</summary>
    public static SKPoint ToView(SKRect viewport, GamePoint point)
    {
        var picture = PictureRect(viewport);
        double scale = picture.Width / GameState.CanvasWidth;
        return new SKPoint((float)(picture.Left + point.X * scale), (float)(picture.Top + point.Y * scale));
    }

    /// <summary>What the game shows now, ready to draw on any thread.</summary>
    public GameFrame Capture()
    {
        var game = Game;
        var coin = game.Coin;
        return new GameFrame
        {
            IsRunning = game.IsPlaying,
            Hotspot = game.Hotspot,
            IsHotspotHovered = game.IsOverHotspot,
            Blasts = game.Inventory.IsOpen
                ? game.Inventory.Blasts.Select(b => new FrameImage(SkiaImages.Of(b.Image), b.Bounds)).ToList()
                : [],
            Coin = coin.Frame is { } c ? new FrameImage(SkiaImages.Of(c), coin.Bounds) : null,
            Label = game.Label,
            LabelText = LabelText(game.Label),
            Cursor = game.IsMouseCaptured && game.CursorFrame is { } cursor
                ? new FrameImage(SkiaImages.Of(cursor), new GameRect(game.CursorPosition - game.CursorHotspot, new GameSize(cursor.Width, cursor.Height)))
                : null,
        };
    }

    /// <summary>
    /// The label drawn as the game draws it (centred and wrapped at <see cref="GameState.LabelAt"/>, charset 1,
    /// colour 19, from the label palette), made again only when the text, the font or the palette changes. Old ones are left
    /// to the finalizer: a frame may still be drawing one on another thread.
    /// </summary>
    private SubtitleOverlay LabelText(string label)
    {
        if (string.IsNullOrEmpty(label) || Text == null) return null;
        if (label == _labelFor && ReferenceEquals(Text, _labelRenderer) && ReferenceEquals(Game.LabelPalette, _labelPalette)) return _label;
        _labelFor = label;
        _labelRenderer = Text;
        _labelPalette = Game.LabelPalette;
        _label = Text.BuildText(new Core.Smush.SmushText
        {
            X = (int)GameState.LabelAt.X, Y = (int)GameState.LabelAt.Y,
            Flags = Core.Smush.SmushTextFlags.Center | Core.Smush.SmushTextFlags.Wrap,
            Font = GameState.LabelCharset, Color = GameState.LabelColor, Text = label,
        }, Game.LabelPalette, (int)GameState.PageSize.Width, (int)GameState.PageSize.Height);
        return _label;
    }
}
