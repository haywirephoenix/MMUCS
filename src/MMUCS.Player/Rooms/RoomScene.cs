using System;
using System.Collections.Generic;
using System.Linq;
using MMUCS.Core.Scumm;
using SkiaSharp;

namespace MMUCS.Player.Rooms;

/// <summary>An object image placed in the room (its top left in room pixels), or a hi-res mod's picture drawn in its place.</summary>
public sealed record SceneObject(RoomObject Object, SKImage Image, int X, int Y, HiResImage HiRes = null);

/// <summary>
/// An actor's frame placed in the room: its top left in room pixels, drawn <paramref name="Scale"/> times its
/// size, cut by z-plane <paramref name="ZPlane"/> (0: none). Actors are drawn in order of
/// <paramref name="Depth"/> (y before elevation), then <paramref name="Actor"/>.
/// </summary>
public sealed record SceneActor(SKImage Image, float X, float Y, float Scale = 1, int ZPlane = 0, int Depth = 0, int Actor = 0, SKImage Shadow = null);

/// <summary>
/// A room as the engine draws it, ready to draw on any thread: the background, the objects shown in the
/// engine's draw order, then the actors in theirs, each cut by its z-plane (the room's planes with the shown
/// objects' planes over them). Everything is in room pixels; the images belong to whoever built the scene
/// (the Room viewer frees its own, <see cref="RoomSceneBuilder"/> leaves them to the garbage collector).
/// </summary>
public sealed class RoomScene
{
    private static readonly SKPaint MaskOut = new() { BlendMode = SKBlendMode.DstOut };

    /// <summary>A room with nothing in it (the canvas stays black).</summary>
    public static RoomScene Blank { get; } = new();

    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>The background and where its top left is (a full-width object image can stand in for the room's own).</summary>
    public SKImage Background { get; init; }
    public int BackgroundX { get; init; }
    public int BackgroundY { get; init; }

    /// <summary>A hi-res mod's background, drawn in place of <see cref="Background"/>, or null.</summary>
    public HiResImage HiResBackground { get; init; }

    /// <summary>
    /// Where the background covers, in room pixels: the hi-res background's place, else the background image's,
    /// else the room's size. The game view cuts the room's objects and actors to it, so a room whose background
    /// isn't widened yet shows black beside it rather than actors and objects over nothing.
    /// </summary>
    public SKRect BackgroundBounds =>
        HiResBackground is { } hi ? SKRect.Create(hi.X, hi.Y, hi.Width, hi.Height)
        : Background != null ? SKRect.Create(BackgroundX, BackgroundY, Background.Width, Background.Height)
        : SKRect.Create(0, 0, Width, Height);

    /// <summary>Object images in draw order, later ones on top.</summary>
    public IReadOnlyList<SceneObject> Objects { get; init; } = [];

    /// <summary>Actors, in any order: <see cref="Draw"/> sorts them as the engine does.</summary>
    public IReadOnlyList<SceneActor> Actors { get; init; } = [];

    /// <summary>
    /// The colours on the screen as a palette strip (<see cref="PaletteImages.Strip"/>) when the background's,
    /// objects' and actors' images are index images (<see cref="PaletteImages"/>), drawn in these colours; null
    /// when they're in colour already.
    /// </summary>
    public SKImage Palette { get; init; }

    /// <summary>The z-plane masks (plane 1 first) as alpha images of the room's size: opaque where they hide actors.</summary>
    public IReadOnlyList<SKImage> ZMasks { get; init; } = [];

    /// <summary>The same room with other actors (the background, objects and planes are shared).</summary>
    public RoomScene WithActors(IReadOnlyList<SceneActor> actors) => new()
    {
        Width = Width, Height = Height,
        Background = Background, BackgroundX = BackgroundX, BackgroundY = BackgroundY, HiResBackground = HiResBackground,
        Objects = Objects, Actors = actors ?? [], ZMasks = ZMasks, Palette = Palette,
    };

    /// <summary>
    /// Draws the room at room pixels on <paramref name="canvas"/> (transform it to place and scale the room).
    /// <paramref name="beforeActors"/> draws between the objects and the actors (the Room viewer's tinted
    /// planes), so what a plane hides shows through the actors' cut-outs. With <paramref name="cutColor"/>,
    /// pixels a plane hides are painted in that colour instead of left out.
    /// </summary>
    public void Draw(SKCanvas canvas, SKSamplingOptions sampling, Action<SKCanvas> beforeActors = null, SKColor? cutColor = null)
    {
        if (HiResBackground != null) HiResBackground.Draw(canvas);
        else if (Background != null) DrawImage(canvas, Background, SKRect.Create(BackgroundX, BackgroundY, Background.Width, Background.Height), sampling, Palette);
        foreach (var o in Objects)
            if (o.HiRes != null) o.HiRes.Draw(canvas);
            else if (o.Image != null) DrawImage(canvas, o.Image, SKRect.Create(o.X, o.Y, o.Image.Width, o.Image.Height), sampling, Palette);
        beforeActors?.Invoke(canvas);
        foreach (var a in Ordered(Actors))
            DrawActor(canvas, a, Mask(a.ZPlane), sampling, cutColor, Palette);
    }

    /// <summary>An image over <paramref name="bounds"/>: in colour, or an index image in <paramref name="palette"/>'s colours.</summary>
    private static void DrawImage(SKCanvas canvas, SKImage image, SKRect bounds, SKSamplingOptions sampling, SKImage palette, SKPaint paint = null)
    {
        if (palette != null) PaletteImages.Draw(canvas, image, palette, bounds, sampling, paint);
        else canvas.DrawImage(image, bounds, sampling, paint);
    }

    /// <summary>Actors in the engine's draw order: by y before elevation, then actor number.</summary>
    public static IEnumerable<SceneActor> Ordered(IEnumerable<SceneActor> actors) =>
        actors.Where(a => a?.Image != null).OrderBy(a => a.Depth).ThenBy(a => a.Actor);

    /// <summary>The mask of plane <paramref name="plane"/> (from 1), or null.</summary>
    public SKImage Mask(int plane) => plane > 0 && plane <= ZMasks.Count ? ZMasks[plane - 1] : null;

    /// <summary>
    /// Draws an actor's frame with the pixels behind its z-plane removed (the engine skips masked pixels):
    /// the frame goes into a layer and the plane's mask, in room pixels, cuts it out. With <paramref name="palette"/>
    /// the frame is an index image, drawn in its colours.
    /// </summary>
    public static void DrawActor(SKCanvas canvas, SceneActor a, SKImage mask, SKSamplingOptions sampling, SKColor? cutColor = null, SKImage palette = null)
    {
        var bounds = SKRect.Create(a.X, a.Y, a.Image.Width * a.Scale, a.Image.Height * a.Scale);
        if (mask == null)
        {
            DrawImage(canvas, a.Image, bounds, sampling, palette);
            if (a.Shadow != null) canvas.DrawImage(a.Shadow, bounds, sampling, Multiply);
            return;
        }
        if (cutColor is { } color)
        {
            // The hidden pixels in the colour: the frame's shape, tinted, kept only where the mask is.
            canvas.SaveLayer(bounds, null);
            using (var tint = new SKPaint { ColorFilter = SKColorFilter.CreateBlendMode(color, SKBlendMode.SrcIn) })
                DrawImage(canvas, a.Image, bounds, sampling, palette, tint);
            using (var keep = new SKPaint { BlendMode = SKBlendMode.DstIn })
                canvas.DrawImage(mask, 0, 0, keep);
            canvas.Restore();
        }
        else if (a.Scale == 1)
        {
            // The frame and its shade cut by the plane once for where they stand (on the CPU, in room pixels),
            // and drawn as they are: no offscreen layers, and nothing cut where the plane has nothing under them.
            int x = (int)MathF.Round(a.X), y = (int)MathF.Round(a.Y);
            DrawImage(canvas, Cut(a.Image, x, y, mask), bounds, sampling, palette);
            if (a.Shadow != null) canvas.DrawImage(Cut(a.Shadow, x, y, mask), bounds, sampling, Multiply);
            return;
        }
        canvas.SaveLayer(bounds, null);
        DrawImage(canvas, a.Image, bounds, sampling, palette);
        canvas.DrawImage(mask, 0, 0, MaskOut);
        canvas.Restore();
        if (a.Shadow == null) return;
        // The shade, cut by the same plane, multiplied onto what's under it (the actor included).
        canvas.SaveLayer(bounds, Multiply);
        canvas.DrawImage(a.Shadow, bounds, sampling);
        canvas.DrawImage(mask, 0, 0, MaskOut);
        canvas.Restore();
    }

    /// <summary>The cuts made of an image, by where it stood and the mask: an actor standing still is cut once.</summary>
    private sealed class Cuts
    {
        public readonly Dictionary<(int X, int Y, SKImage Mask), SKImage> ByPlace = [];
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SKImage, Cuts> CutsOf = [];

    /// <summary>
    /// <paramref name="image"/> (32-bit, in memory) with the pixels <paramref name="mask"/> hides at (<paramref name="x"/>,
    /// <paramref name="y"/>) in the room left out; the image itself when the mask hides none of it, or when either
    /// isn't in memory. Kept while the image lives, for a few places (an actor walking makes a new cut each step).
    /// </summary>
    public static SKImage Cut(SKImage image, int x, int y, SKImage mask)
    {
        var cuts = CutsOf.GetOrCreateValue(image);
        lock (cuts)
        {
            if (cuts.ByPlace.TryGetValue((x, y, mask), out var cut)) return cut;
            cut = MakeCut(image, x, y, mask) ?? image;
            if (cuts.ByPlace.Count >= 8) cuts.ByPlace.Clear(); // the old cuts are left to the finalizer: a frame may still be drawing one
            cuts.ByPlace[(x, y, mask)] = cut;
            return cut;
        }
    }

    private static SKImage MakeCut(SKImage image, int x, int y, SKImage mask)
    {
        using var pixels = image.PeekPixels();
        using var plane = mask.PeekPixels();
        if (pixels == null || plane == null || pixels.ColorType is not (SKColorType.Rgba8888 or SKColorType.Bgra8888) || plane.ColorType != SKColorType.Alpha8) return null;
        var from = pixels.GetPixelSpan();
        var hides = plane.GetPixelSpan();
        int w = image.Width, h = image.Height, mw = mask.Width, mh = mask.Height;
        byte[] rgba = null;
        for (int j = 0; j < h; j++)
        {
            int my = y + j;
            if (my < 0 || my >= mh) continue;
            for (int i = 0; i < w; i++)
            {
                int mx = x + i;
                if (mx < 0 || mx >= mw || hides[my * plane.RowBytes + mx] == 0) continue;
                int o = j * pixels.RowBytes + i * 4;
                if (from[o + 3] == 0) continue;
                if (rgba == null)
                {
                    rgba = new byte[w * h * 4];
                    for (int r = 0; r < h; r++) from.Slice(r * pixels.RowBytes, w * 4).CopyTo(rgba.AsSpan(r * w * 4));
                }
                rgba.AsSpan(j * w * 4 + i * 4, 4).Clear();
            }
        }
        return rgba == null ? null : SKImage.FromPixelCopy(new SKImageInfo(w, h, pixels.ColorType, pixels.AlphaType), rgba, w * 4);
    }

    /// <summary>
    /// How a shadow layer draws: the engine turns each colour under a shadow pixel into its shadow table's
    /// (the colour scaled by the table's factors), so the layer, in those factors, multiplies.
    /// </summary>
    private static readonly SKPaint Multiply = new() { BlendMode = SKBlendMode.Multiply };

    /// <summary>A z-plane as an alpha image of <paramref name="width"/>×<paramref name="height"/>: opaque where it hides actors.</summary>
    public static SKImage MaskImage(ZPlane plane, int width, int height)
    {
        var alpha = new byte[width * height];
        for (int y = 0; y < height && y < plane.Height; y++)
        {
            int row = y * plane.Strips, o = y * width;
            for (int s = 0; s < plane.Strips; s++)
            {
                byte b = plane.Data[row + s];
                if (b == 0) continue;
                for (int bit = 0, x = s * 8; bit < 8 && x < width; bit++, x++)
                    if ((b & (0x80 >> bit)) != 0) alpha[o + x] = 255;
            }
        }
        var info = new SKImageInfo(width, height, SKColorType.Alpha8, SKAlphaType.Premul);
        return SKImage.FromPixelCopy(info, alpha, width);
    }

    /// <summary>
    /// The planes actors are cut by while <paramref name="shown"/> objects are drawn (in draw order, each
    /// with the image it shows, from 0): the room's planes with each object's written over them, a later one
    /// over an earlier one's. Callers that keep the decoded planes pass them in (<paramref name="roomPlanes"/>,
    /// <paramref name="objectPlanes"/>); otherwise they're decoded here.
    /// </summary>
    public static ZPlane[] Planes(RoomModel room, IEnumerable<(RoomObject Object, int Image)> shown,
        Func<RoomObject, int, ZPlane[]> objectPlanes = null, ZPlane[] roomPlanes = null)
    {
        int count = ZPlanes.Count(room);
        if (count == 0 || room.Width <= 0) return [];
        objectPlanes ??= (o, image) => ZPlanes.Object(o, image);
        return ZPlanes.Compose(roomPlanes ?? ZPlanes.Room(room), count, room.Width, room.Height, shown.Select(s => (s.Object, objectPlanes(s.Object, s.Image))));
    }
}
