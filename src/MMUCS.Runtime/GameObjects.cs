using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MMUCS.Core.Imaging;
using MMUCS.Core.Scumm;

namespace MMUCS.Runtime;

/// <summary>An object image the game loaded: its room and every state's image.</summary>
public sealed record GameObjectImages(RoomModel Room, RoomObject Object, IReadOnlyList<RgbaImage> States)
{
    /// <summary>State <paramref name="state"/>'s image (1-based, as the scripts count them), or null.</summary>
    public RgbaImage State(int state) => state >= 1 && state <= States.Count ? States[state - 1] : null;
}

/// <summary>An image drawn on the game canvas, as super-blast-object draws one: where it goes, in game units (its pixels are game pixels).</summary>
public sealed record GameBlast(RgbaImage Image, GameRect Bounds);

/// <summary>Finds object images (OBIMs) by name and decodes their states in their room's palette.</summary>
public static partial class GameObjects
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

    /// <summary>Every object's hover name (its OBNA, message code removed) by object id.</summary>
    public static Dictionary<int, string> HoverNames(ScummBlock root)
    {
        var names = new Dictionary<int, string>();
        if (root == null) return names;
        foreach (var obcd in root.FindChildrenRecursive(ScummTag.OBCD))
        {
            if (IdOf(obcd) is not int id || obcd.FindChild(ScummTag.OBNA) is not { } obna) continue;
            var s = obna.DataSpan;
            int len = s.IndexOf((byte)0);
            string title = RoomDataExport.StripMessageCode(System.Text.Encoding.Latin1.GetString(len < 0 ? s : s[..len]));
            if (!string.IsNullOrEmpty(title)) names.TryAdd(id, title);
        }
        return names;
    }

    /// <summary>
    /// The inventory object an icon stands for: what its verb 93 sets icon-item to (the scripts run
    /// <c>start-object rec icon verb 93</c> and read icon-item back). Null when it has no such verb.
    /// </summary>
    public static int? IconItem(RoomModel room, RoomObject icon)
    {
        var obcd = ((room.Lflf.FindChild(ScummTag.RMSC) ?? room.Room).Children)
            .FirstOrDefault(c => c.Tag == ScummTag.OBCD && IdOf(c) is int id && RoomScripts.ObjectName(c, id) == icon.Name);
        if (obcd == null || RoomScripts.For(obcd) is not { } entry) return null;
        bool inVerb = false;
        foreach (var line in RoomScripts.Decompile(entry))
        {
            string text = line.Text.Trim();
            if (text.StartsWith("verb ", StringComparison.Ordinal)) inVerb = text == "verb 93 {";
            else if (inVerb && IconItemLine().Match(text) is { Success: true } m) return int.Parse(m.Groups[1].Value);
        }
        return null;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^(?:icon-item|var\d+) = (\d+)$")]
    private static partial System.Text.RegularExpressions.Regex IconItemLine();

    /// <summary>An OBCD's object id, from its CDHD.</summary>
    private static int? IdOf(ScummBlock obcd) =>
        obcd.FindChild(ScummTag.CDHD) is { DataLength: >= 8 } cdhd ? (int)cdhd.DataSpan.U32LE(4) : null;

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
