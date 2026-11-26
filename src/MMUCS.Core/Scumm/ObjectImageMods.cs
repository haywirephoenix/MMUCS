using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MMUCS.Core.Imaging;

namespace MMUCS.Core.Scumm;

/// <summary>
/// Object images from the mods (<see cref="GameMods"/>): a PNG where the export writes the object's image,
/// "rooms/&lt;room&gt;/objects/&lt;index&gt;_&lt;name&gt;_f&lt;image&gt;.png" in a mod ("rooms/022_clring-a/objects/041_clring-a-haggis-caber_f0.png"),
/// is shown in place of that image. It can be any size: it goes at the object's place unless a
/// "&lt;same name&gt;.meta.json" beside it says where its top-left corner is in the room (<c>{ "x": 236, "y": 392 }</c>).
/// The game draws in palette colours, so the PNG is taken into the colours of the palette the object is
/// shown in, each pixel the index with its colour or the nearest; a pixel less than half opaque is
/// transparent, and the room's transparent colour is left for that. The first enabled mod by name that has an
/// image wins. A PNG is read again when its file changes.
/// </summary>
public sealed class ObjectImageMods
{
    /// <summary>A mod's image for an object: its mod, the PNG, and where it goes (null: the object's place).</summary>
    public sealed record Image(string Mod, string File, int? X, int? Y);

    private readonly List<GameMods.Mod> _mods = [];
    private readonly Dictionary<string, (DateTime Written, byte[] Palette, int Transparent, IndexedSurface Surface)> _surfaces = [];

    /// <summary>The enabled mods that have a rooms/ folder (folder names).</summary>
    public List<string> Mods { get; } = [];

    /// <summary>What couldn't be read: a broken mod.json or .meta.json, a PNG that won't decode.</summary>
    public List<string> Problems { get; } = [];

    /// <summary>The object images in the mods beside <paramref name="gameFile"/>; none without a mods folder.</summary>
    public static ObjectImageMods Load(string gameFile)
    {
        var mods = new ObjectImageMods();
        foreach (var mod in GameMods.Load(gameFile, mods.Problems))
        {
            if (!mod.Enabled || !Directory.Exists(Path.Combine(mod.Folder, "rooms"))) continue;
            mods._mods.Add(mod);
            mods.Mods.Add(mod.Name);
        }
        return mods;
    }

    /// <summary>The mods' image for image <paramref name="frame"/> (from 0) of <paramref name="obj"/> in <paramref name="room"/>, or null.</summary>
    public Image Find(RoomModel room, RoomObject obj, int frame)
    {
        if (_mods.Count == 0 || room == null || obj == null) return null;
        string relative = Path.Combine("rooms", ScummExporters.RoomFolderName(room), "objects", ScummExporters.ObjectFrameFile(obj, frame));
        foreach (var mod in _mods)
        {
            string file = Path.Combine(mod.Folder, relative);
            if (!File.Exists(file)) continue;
            var (x, y) = Place(mod, file);
            return new Image(mod.Name, file, x, y);
        }
        return null;
    }

    /// <summary>Where a .meta.json beside <paramref name="png"/> puts it (x, y), or nulls.</summary>
    private (int? X, int? Y) Place(GameMods.Mod mod, string png)
    {
        string meta = Path.ChangeExtension(png, ".meta.json");
        if (!File.Exists(meta)) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(meta), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            int? Int(string name) => doc.RootElement.TryGetProperty(name, out var v) && v.TryGetInt32(out int n) ? n : null;
            return (Int("x"), Int("y"));
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
        {
            lock (Problems) Problems.Add($"{mod.Name}: {Path.GetFileName(meta)}: {ex.Message}");
            return (null, null);
        }
    }

    /// <summary>
    /// <paramref name="image"/> in palette indices of <paramref name="palette"/> (256 RGB triples), with
    /// <paramref name="transparent"/> where it's see-through, or null when the PNG won't read.
    /// </summary>
    public IndexedSurface Indexed(Image image, byte[] palette, int transparent)
    {
        if (image == null || palette == null || palette.Length < 768) return null;
        DateTime written;
        try { written = File.GetLastWriteTimeUtc(image.File); }
        catch (IOException) { return null; }
        lock (_surfaces)
            if (_surfaces.TryGetValue(image.File, out var cached) && cached.Written == written && cached.Transparent == transparent &&
                cached.Palette.AsSpan().SequenceEqual(palette.AsSpan(0, 768)))
                return cached.Surface;

        PngImage png;
        try { png = PngReader.Load(image.File); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or IndexOutOfRangeException)
        {
            lock (Problems) Problems.Add($"{image.Mod}: {Path.GetFileName(image.File)}: {ex.Message}");
            return null;
        }
        var surface = Quantize(png.ToArgb(), png.Width, png.Height, palette, transparent);
        lock (_surfaces) _surfaces[image.File] = (written, palette.AsSpan(0, 768).ToArray(), transparent, surface);
        return surface;
    }

    /// <summary>ARGB pixels as palette indices: the index with the pixel's colour, else the nearest; less than half opaque is <paramref name="transparent"/>.</summary>
    internal static IndexedSurface Quantize(uint[] argb, int width, int height, byte[] palette, int transparent)
    {
        var pixels = new byte[width * height];
        var nearest = new Dictionary<uint, byte>();
        for (int i = 0; i < pixels.Length; i++)
        {
            uint c = argb[i];
            if (c >> 24 < 128) { pixels[i] = (byte)transparent; continue; }
            uint rgb = c & 0xFFFFFF;
            if (!nearest.TryGetValue(rgb, out byte index)) nearest[rgb] = index = Nearest(rgb, palette, transparent);
            pixels[i] = index;
        }
        return new IndexedSurface { Width = width, Height = height, Pitch = width, Pixels = pixels, TransparentIndex = (byte)transparent };
    }

    private static byte Nearest(uint rgb, byte[] palette, int transparent)
    {
        int r = (int)(rgb >> 16) & 0xFF, g = (int)(rgb >> 8) & 0xFF, b = (int)rgb & 0xFF;
        int best = -1, bestDistance = int.MaxValue;
        for (int i = 0; i < 256; i++)
        {
            if (i == transparent) continue;
            int dr = palette[i * 3] - r, dg = palette[i * 3 + 1] - g, db = palette[i * 3 + 2] - b;
            int d = dr * dr + dg * dg + db * db;
            if (d < bestDistance) { best = i; bestDistance = d; if (d == 0) break; }
        }
        return (byte)Math.Max(best, 0);
    }
}
