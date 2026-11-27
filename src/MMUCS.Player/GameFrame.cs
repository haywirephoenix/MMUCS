using System;
using System.Collections.Generic;
using MMUCS.Player.Rooms;
using MMUCS.Player.Text;
using MMUCS.Runtime;
using SkiaSharp;

namespace MMUCS.Player;

/// <summary>An image placed on the picture, in game units; <paramref name="Smooth"/> for a hi-res mod's picture, drawn smoothly whatever the window's size.</summary>
public readonly record struct FrameImage(SKImage Image, GameRect Bounds, bool Smooth = false);

/// <summary>
/// One frame of the game as it should look: taken from the game by <see cref="GameView.Capture"/>, then drawn
/// with <see cref="Render"/> on any thread (a host's render thread, a test's off-screen surface). Everything
/// in it is immutable.
/// </summary>
public sealed class GameFrame
{
    private static readonly SKColor Letterbox = SKColors.Black;
    private static readonly SKColor Stage = new(0x1B, 0x1F, 0x2A), StageStopped = new(0x14, 0x15, 0x19);
    private static readonly SKColor HotspotFill = new(0x7D, 0xCF, 0xE8, 0x30), HotspotHoverFill = new(0xE8, 0xC0, 0x7D, 0x50);
    private static readonly SKColor HotspotLine = new(0x7D, 0xCF, 0xE8, 0xA0), HotspotHoverLine = new(0xE8, 0xC0, 0x7D);

    /// <summary>
    /// A cutscene picture shown on the page instead of the room (a video a script started), or null. The frame
    /// holds a reference to it: whoever replaces a frame calls <see cref="Release"/>, and a renderer that draws
    /// it later retains it for as long as it draws.
    /// </summary>
    public CutsceneFrame Cutscene { get; init; }

    /// <summary>Lets go of the frame's cutscene picture (once, when the frame is replaced).</summary>
    public void Release() => Cutscene?.Release();

    /// <summary>The room on screen, or null.</summary>
    public RoomScene Room { get; init; }

    /// <summary>
    /// Where the camera looks, in room pixels: its centre is drawn at the canvas's centre, so the 640×480 page
    /// shows what the game shows and a 16:9 canvas shows more of a wide room on each side.
    /// </summary>
    public GamePoint Camera { get; init; } = new(320, 240);

    /// <summary>Whether the room and its actors are cut to the 640×480 page, the canvas's sides black (the banjo duel's halves).</summary>
    public bool PageOnly { get; init; }

    /// <summary>Whether the game is playing (the picture is dimmed and the hotspot hidden when it isn't).</summary>
    public bool IsRunning { get; init; }

    /// <summary>The stand-in hotspot, and whether the cursor is on it.</summary>
    public GameRect Hotspot { get; init; }
    public bool IsHotspotHovered { get; init; }

    /// <summary>Images over the room, back to front (the open inventory).</summary>
    public IReadOnlyList<FrameImage> Blasts { get; init; } = [];

    /// <summary>The verb coin's frame, or null.</summary>
    public FrameImage? Coin { get; init; }

    /// <summary>The hover label's text, and the label drawn in pixels of the 640×480 page (null when there's none or no font yet).</summary>
    public string Label { get; init; }
    public SubtitleOverlay LabelText { get; init; }

    /// <summary>Actors the engine draws over the blasted objects (layer below 0), in room pixels like <see cref="Room"/>'s.</summary>
    public RoomScene UpperActors { get; init; }

    /// <summary>The scripts' texts on the page (talk lines, blasted labels), drawn in order over the room.</summary>
    public IReadOnlyList<SubtitleOverlay> Texts { get; init; } = [];

    /// <summary>The talk lines placed over their actors, drawn after <see cref="Texts"/> and moved with the room by <see cref="TextShift"/>.</summary>
    public IReadOnlyList<SubtitleOverlay> RoomTexts { get; init; } = [];

    /// <summary>How far <see cref="RoomTexts"/> move to stay with the room when the camera shown isn't the engine's (a smooth camera easing).</summary>
    public GamePoint TextShift { get; init; }

    /// <summary>The game cursor, placed with its hotspot on the cursor's position; null while the game hasn't got the mouse.</summary>
    public FrameImage? Cursor { get; init; }

    /// <summary>
    /// Draws the frame into <paramref name="viewport"/> on <paramref name="canvas"/>: the 16:9 picture as large as it
    /// fits, centred, black around it; game pixels stay square (nearest-neighbour when enlarged).
    /// </summary>
    public void Render(SKCanvas canvas, SKRect viewport) => Render(canvas, viewport, cursor: true);

    /// <summary>
    /// The 640×480 page as a PNG of <paramref name="width"/>×<paramref name="height"/> (a save's thumbnail), drawn
    /// as <see cref="Render(SKCanvas, SKRect)"/> draws it, without the cursor.
    /// </summary>
    public byte[] PagePng(int width, int height)
    {
        int canvasWidth = (int)Math.Ceiling(GameState.CanvasWidth), canvasHeight = (int)GameState.CanvasHeight;
        using var full = SKSurface.Create(new SKImageInfo(canvasWidth, canvasHeight));
        Render(full.Canvas, new SKRect(0, 0, (float)GameState.CanvasWidth, canvasHeight), cursor: false);
        using var picture = full.Snapshot();
        using var small = SKSurface.Create(new SKImageInfo(width, height));
        var page = SKRect.Create((float)GameState.PageOrigin.X, (float)GameState.PageOrigin.Y, (float)GameState.PageSize.Width, (float)GameState.PageSize.Height);
        small.Canvas.DrawImage(picture, page, new SKRect(0, 0, width, height), new SKSamplingOptions(SKCubicResampler.Mitchell));
        using var thumbnail = small.Snapshot();
        using var png = thumbnail.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }

    private void Render(SKCanvas canvas, SKRect viewport, bool cursor)
    {
        using var paint = new SKPaint { IsAntialias = false };
        paint.Color = Letterbox;
        canvas.DrawRect(viewport, paint);
        var picture = GameView.PictureRect(viewport);
        if (picture.Width <= 0 || picture.Height <= 0) return;
        paint.Color = Room != null ? Letterbox : IsRunning ? Stage : StageStopped;
        canvas.DrawRect(picture, paint);

        float scale = picture.Width / (float)GameState.CanvasWidth;
        canvas.Save();
        canvas.ClipRect(picture);
        canvas.Translate(picture.Left, picture.Top);
        canvas.Scale(scale);
        var sampling = new SKSamplingOptions(scale < 0.999f ? SKFilterMode.Linear : SKFilterMode.Nearest, SKMipmapMode.None);

        if (Cutscene != null)
        {
            // The video on the 640×480 page, the 16:9 canvas's sides black.
            var page = GameState.PageSize;
            float s = (float)Math.Min(page.Width / Cutscene.Width, page.Height / Cutscene.Height);
            canvas.Save();
            canvas.Translate((float)(GameState.PageOrigin.X + (page.Width - Cutscene.Width * s) / 2), (float)(GameState.PageOrigin.Y + (page.Height - Cutscene.Height * s) / 2));
            canvas.Scale(s);
            Cutscene.Draw(canvas, linear: scale * s < 0.999f);
            canvas.Restore();
        }
        else if (Room != null)
        {
            canvas.Save();
            if (PageOnly) canvas.ClipRect(PageRect);
            canvas.Translate((float)(GameState.CanvasWidth / 2 - Camera.X), (float)(GameState.CanvasHeight / 2 - Camera.Y));
            ClipToBackground(canvas, Room);
            Room.Draw(canvas, sampling);
            canvas.Restore();
        }

        if (IsRunning && !Hotspot.IsEmpty)
        {
            var rect = Rect(Hotspot);
            paint.Color = IsHotspotHovered ? HotspotHoverFill : HotspotFill;
            canvas.DrawRect(rect, paint);
            using var line = new SKPaint
            {
                Style = SKPaintStyle.Stroke, IsAntialias = true, Color = IsHotspotHovered ? HotspotHoverLine : HotspotLine,
                StrokeWidth = (IsHotspotHovered ? 1.5f : 1f) / scale,
                PathEffect = IsHotspotHovered ? null : SKPathEffect.CreateDash([4 / scale, 3 / scale], 0),
            };
            canvas.DrawRect(rect, line);
        }

        foreach (var blast in Blasts) canvas.DrawImage(blast.Image, Rect(blast.Bounds), blast.Smooth ? HiResImage.Sampling : sampling);
        if (Coin is { } coin) canvas.DrawImage(coin.Image, Rect(coin.Bounds), sampling);
        if (UpperActors != null && Room != null && Cutscene == null)
        {
            canvas.Save();
            if (PageOnly) canvas.ClipRect(PageRect);
            canvas.Translate((float)(GameState.CanvasWidth / 2 - Camera.X), (float)(GameState.CanvasHeight / 2 - Camera.Y));
            ClipToBackground(canvas, Room);
            UpperActors.Draw(canvas, sampling);
            canvas.Restore();
        }
        if (Texts.Count > 0)
        {
            canvas.Save();
            canvas.Translate((float)GameState.PageOrigin.X, (float)GameState.PageOrigin.Y);
            foreach (var text in Texts) text?.Draw(canvas, smooth: scale < 0.999f);
            canvas.Restore();
        }
        if (RoomTexts.Count > 0)
        {
            canvas.Save();
            canvas.Translate((float)(GameState.PageOrigin.X + TextShift.X), (float)(GameState.PageOrigin.Y + TextShift.Y));
            foreach (var text in RoomTexts) text?.Draw(canvas, smooth: scale < 0.999f);
            canvas.Restore();
        }
        if (LabelText is { IsEmpty: false } label)
        {
            canvas.Save();
            canvas.Translate((float)GameState.PageOrigin.X, (float)GameState.PageOrigin.Y);
            label.Draw(canvas, smooth: scale < 0.999f);
            canvas.Restore();
        }
        if (cursor && Cursor is { } pointer) canvas.DrawImage(pointer.Image, Rect(pointer.Bounds), sampling);
        canvas.Restore();
    }

    /// <summary>Cuts what's drawn next to where the room's background covers (black beyond it, as the letterbox is).</summary>
    private static void ClipToBackground(SKCanvas canvas, RoomScene room)
    {
        var bounds = room.BackgroundBounds;
        if (bounds.Width > 0 && bounds.Height > 0) canvas.ClipRect(bounds);
    }

    private static SKRect PageRect => SKRect.Create((float)GameState.PageOrigin.X, (float)GameState.PageOrigin.Y, (float)GameState.PageSize.Width, (float)GameState.PageSize.Height);

    private static SKRect Rect(GameRect r) => new((float)r.X, (float)r.Y, (float)r.Right, (float)r.Bottom);
}
