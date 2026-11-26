using System;
using System.Collections.Generic;
using MMUCS.Core.Imaging;
using MMUCS.Core.Scumm;
using SkiaSharp;

namespace MMUCS.Player.Rooms;

/// <summary>
/// An object image in palette indices, placed in the room, with the index it leaves out. With <paramref name="HiRes"/>
/// a hi-res mod's picture is drawn in its place (<paramref name="Scale"/> of its pixels to a game pixel), tinted by
/// the image's indices from <paramref name="Palette"/>, the colours the picture was made in.
/// </summary>
public sealed record IndexedObject(RoomObject Object, IndexedSurface Surface, int Transparent, int X, int Y,
    SKImage HiRes = null, int Scale = 1, byte[] Palette = null)
{
    internal readonly Lazy<(SKImage Indices, bool[] Used)> Tint = new(() => (HiResTint.Indices(Surface), HiResTint.Used(Surface)));
}

/// <summary>
/// A room as the engine keeps it before it's shown: the background and the objects shown, in palette
/// indices, and the z-plane masks. <see cref="Colour"/> makes the <see cref="RoomScene"/> for the colours on
/// the screen, so the room changes with the palette (the scripts' fades and lights) as it does in the game.
/// </summary>
public sealed class IndexedRoom
{
    public int Width { get; init; }
    public int Height { get; init; }
    public IndexedSurface Background { get; init; }
    public IReadOnlyList<IndexedObject> Objects { get; init; } = [];
    public IReadOnlyList<SKImage> ZMasks { get; init; } = [];

    /// <summary>A hi-res mod's background drawn over the room in place of <see cref="Background"/>, or null.</summary>
    public SKImage HiResBackground { get; init; }

    /// <summary>The colours the background was made in (256 RGB triples; the room's palette 0): what a hi-res background's tint starts from.</summary>
    public byte[] Palette { get; init; }

    private (SKImage Indices, bool[] Used)? _backgroundTint;

    /// <summary>The room in <paramref name="palette"/>'s colours (256 RGB triples); the z-planes are shared.</summary>
    public RoomScene Colour(byte[] palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        SKImage background = null;
        HiResImage hiRes = null;
        SKImage screen = null;
        if (HiResBackground != null && Background is { } under && Palette != null)
        {
            _backgroundTint ??= (HiResTint.Indices(under), HiResTint.Used(under));
            hiRes = HiRes(HiResBackground, 0, 0, under.Width, under.Height, _backgroundTint.Value, Palette, palette, ref screen);
        }
        else if (Background is { } bg)
            background = SkiaImages.Of(ScummImageUtils.CreateImage(bg.Pixels, bg.Width, bg.Height, bg.Pitch, palette));
        var objects = new List<SceneObject>(Objects.Count);
        foreach (var o in Objects)
        {
            if (o.HiRes != null && o.Palette != null)
            {
                var tint = o.Tint.Value;
                objects.Add(new SceneObject(o.Object, null, o.X, o.Y,
                    HiRes(o.HiRes, o.X, o.Y, o.HiRes.Width / (float)o.Scale, o.HiRes.Height / (float)o.Scale, tint, o.Palette, palette, ref screen)));
                continue;
            }
            var rgba = ScummImageUtils.CreateImage(o.Surface.Pixels, o.Surface.Width, o.Surface.Height, o.Surface.Pitch, palette, o.Transparent);
            if (rgba != null) objects.Add(new SceneObject(o.Object, SkiaImages.Of(rgba), o.X, o.Y));
        }
        return new RoomScene { Width = Width, Height = Height, Background = background, HiResBackground = hiRes, Objects = objects, ZMasks = ZMasks };
    }

    /// <summary>A hi-res picture placed in the room, tinted when the screen shows its indices in other colours than its base ones.</summary>
    private static HiResImage HiRes(SKImage image, float x, float y, float width, float height, (SKImage Indices, bool[] Used) tint,
        byte[] basePalette, byte[] screenPalette, ref SKImage screen)
    {
        SKShader tinted = null;
        if (HiResTint.Differs(tint.Used, basePalette, screenPalette))
        {
            screen ??= HiResTint.Strip(screenPalette);
            tinted = HiResTint.Create(image, x, y, width, height, tint.Indices, HiResTint.Strip(basePalette), screen);
        }
        return new HiResImage(image, x, y, width, height, tinted);
    }
}
