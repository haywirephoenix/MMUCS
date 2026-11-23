using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using MMUCS.Core.Imaging;
using MMUCS.Core.Scumm;
using MMUCS.Desktop.Rendering;

namespace MMUCS.Desktop.ViewModels;

/// <summary>An object image the game window loaded by name: its room and every state's image.</summary>
public sealed record GameObjectImages(RoomModel Room, RoomObject Object, IReadOnlyList<RgbaImage> States)
{
    /// <summary>State <paramref name="state"/>'s image (1-based, as the scripts count them), or null.</summary>
    public RgbaImage State(int state) => state >= 1 && state <= States.Count ? States[state - 1] : null;
}

/// <summary>
/// An image drawn on the game canvas, as super-blast-object draws one: where it goes in game units.
/// Its bitmap is made the first time the canvas asks for it.
/// </summary>
public sealed class GameBlast(RgbaImage image, Rect bounds)
{
    private Bitmap _bitmap;
    public RgbaImage Image { get; } = image;
    public Rect Bounds { get; } = bounds;
    public Bitmap Bitmap => _bitmap ??= Image.ToBitmap();
}

/// <summary>Finds object images (OBIMs) by name and decodes their states in their room's palette.</summary>
public static class GameObjects
{
    /// <summary>The object image named <paramref name="name"/> (the first in the game), or null when there's none.</summary>
    public static async Task<GameObjectImages> LoadAsync(ScummBlock root, string name, CancellationToken token = default)
    {
        var obim = root?.FindChildrenRecursive(ScummTag.OBIM).FirstOrDefault(b => NameOf(b) == name);
        if (obim == null || !RoomModel.TryCreate(obim, out var room)) return null;
        return await LoadAsync(room, room.Objects.First(o => ReferenceEquals(o.Block, obim)), token);
    }

    /// <summary>Every state of <paramref name="obj"/>, an object image of <paramref name="room"/>.</summary>
    public static async Task<GameObjectImages> LoadAsync(RoomModel room, RoomObject obj, CancellationToken token = default)
    {
        var obim = obj.Block;
        int palette = Math.Clamp(RoomPalettes.For(room.Lflf).Object(room, obj), 0, Math.Max(0, room.PaletteCount - 1));
        var states = new List<RgbaImage>();
        for (int f = 0; f < Math.Max(1, obj.ImageCount); f++)
        {
            var result = await ObimCache.GetCachedObimAsync(obim, f, palette, bakeImage: true, token);
            if (result.ExecutedImage is { } image) states.Add(image);
        }
        return new GameObjectImages(room, obj, states);
    }

    /// <summary>An OBIM's name, from its IMHD (null when it has none).</summary>
    public static string NameOf(ScummBlock obim)
    {
        if (obim.FindChild(ScummTag.IMHD) is not { } imhd) return null;
        var s = imhd.DataSpan;
        int len = 0;
        while (len < 32 && len < s.Length && s[len] != 0) len++;
        return System.Text.Encoding.ASCII.GetString(s[..len]);
    }
}
