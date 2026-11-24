using System;
using System.Collections.Generic;
using MMUCS.Core.Imaging;
using MMUCS.Core.Scumm;
using SkiaSharp;

namespace MMUCS.Player.Rooms;

/// <summary>An object image in palette indices, placed in the room, with the index it leaves out.</summary>
public sealed record IndexedObject(RoomObject Object, IndexedSurface Surface, int Transparent, int X, int Y);

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

    /// <summary>The room in <paramref name="palette"/>'s colours (256 RGB triples); the z-planes are shared.</summary>
    public RoomScene Colour(byte[] palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        SKImage background = null;
        if (Background is { } bg)
            background = SkiaImages.Of(ScummImageUtils.CreateImage(bg.Pixels, bg.Width, bg.Height, bg.Pitch, palette));
        var objects = new List<SceneObject>(Objects.Count);
        foreach (var o in Objects)
        {
            var rgba = ScummImageUtils.CreateImage(o.Surface.Pixels, o.Surface.Width, o.Surface.Height, o.Surface.Pitch, palette, o.Transparent);
            if (rgba != null) objects.Add(new SceneObject(o.Object, SkiaImages.Of(rgba), o.X, o.Y));
        }
        return new RoomScene { Width = Width, Height = Height, Background = background, Objects = objects, ZMasks = ZMasks };
    }
}
