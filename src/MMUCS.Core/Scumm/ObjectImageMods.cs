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
/// shown in, each pixel the index with its colour or the nearest, and the room's transparent colour is left for
/// see-through pixels. Object images have no translucency, so a partly see-through pixel is baked over the room's
/// background under it, as the image is placed: a dark one at least a fifth opaque (the export's 40% black for a
/// costume's shadow) is a shadow, the background darkened by a shadow table as the game draws a costume's (table 6,
/// or the .meta.json's "shadowTable"), and any other is blended. Without the background, a pixel less than half
/// opaque is see-through. The first enabled mod by name that has an
/// image wins. A PNG is read again when its file changes.
/// </summary>
public sealed class ObjectImageMods
{
    /// <summary>A mod's image for an object: its mod, the PNG, and where it goes (null: the object's place).</summary>
    /// <remarks><c>ShadowTable</c> is the shadow table its shadow pixels darken by (the .meta.json's "shadowTable"; null: 6).</remarks>
    public sealed record Image(string Mod, string File, int? X, int? Y, int? ShadowTable = null);

    /// <summary>
    /// COMI's shadow tables as its scripts set them (build-palette-shadow 6, 190… and 7, 220…), 8 RGB triples of 0–255:
    /// what a mod image's shadow is darkened by where the game's own tables aren't given.
    /// </summary>
    public static readonly byte[] DefaultShadowTables = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 189, 189, 189, 219, 219, 219];

    /// <summary>The shadow table a mod image's shadow uses unless its .meta.json says another: 6, the costumes' main one.</summary>
    public const int DefaultShadowTable = 6;

    private readonly List<GameMods.Mod> _mods = [];
    private readonly Dictionary<string, (DateTime Written, byte[] Palette, int Transparent, IndexedSurface Background, int X, int Y, string Shadow, IndexedSurface Surface)> _surfaces = [];

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
            // A hi-res mod's images are drawn at their scale in full colour (HiResMods), not taken into the palette.
            if (!mod.Enabled || mod.Scale > 1 || !Directory.Exists(Path.Combine(mod.Folder, "rooms"))) continue;
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
            var (x, y, shadow) = Place(mod, file);
            return new Image(mod.Name, file, x, y, shadow);
        }
        return null;
    }

    /// <summary>
    /// Saves where a mod's image goes (its top-left in the room) to the .meta.json beside its PNG, keeping
    /// anything else the file has; it's read again from then on.
    /// </summary>
    public static void SavePlace(Image image, int x, int y)
    {
        string meta = Path.ChangeExtension(image.File, ".meta.json");
        var root = GameMods.ReadForEdit(meta);
        root["x"] = x;
        root["y"] = y;
        GameMods.WriteEdited(meta, root);
    }

    /// <summary>Where a .meta.json beside <paramref name="png"/> puts it (x, y), or nulls.</summary>
    private (int? X, int? Y, int? ShadowTable) Place(GameMods.Mod mod, string png)
    {
        string meta = Path.ChangeExtension(png, ".meta.json");
        if (!File.Exists(meta)) return (null, null, null);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(meta), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            int? Int(string name) => doc.RootElement.TryGetProperty(name, out var v) && v.TryGetInt32(out int n) ? n : null;
            return (Int("x"), Int("y"), Int("shadowTable") is int t and >= 1 and <= 7 ? t : null);
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
        {
            lock (Problems) Problems.Add($"{mod.Name}: {Path.GetFileName(meta)}: {ex.Message}");
            return (null, null, null);
        }
    }

    /// <summary>
    /// <paramref name="image"/> in palette indices of <paramref name="palette"/> (256 RGB triples), with
    /// <paramref name="transparent"/> where it's see-through, or null when the PNG won't read. Its partly
    /// see-through pixels are blended over <paramref name="background"/> (the room's, in indices) with the image's
    /// top-left at <paramref name="x"/>,<paramref name="y"/> in it; its shadow pixels darken it by the image's shadow
    /// table in <paramref name="shadowTables"/> (8 RGB triples, the game's now: <c>ScreenPalette.ShadowFactors</c>; null or
    /// a table not set: <see cref="DefaultShadowTables"/>).
    /// </summary>
    public IndexedSurface Indexed(Image image, byte[] palette, int transparent, IndexedSurface background = null, int x = 0, int y = 0, byte[] shadowTables = null)
    {
        var shadow = ShadowOf(image, shadowTables);
        string shadowKey = Convert.ToHexString(shadow);
        if (image == null || palette == null || palette.Length < 768) return null;
        DateTime written;
        try { written = File.GetLastWriteTimeUtc(image.File); }
        catch (IOException) { return null; }
        lock (_surfaces)
            if (_surfaces.TryGetValue(image.File, out var cached) && cached.Written == written && cached.Transparent == transparent &&
                ReferenceEquals(cached.Background, background) && cached.X == x && cached.Y == y && cached.Shadow == shadowKey &&
                cached.Palette.AsSpan().SequenceEqual(palette.AsSpan(0, 768)))
                return cached.Surface;

        PngImage png;
        try { png = PngReader.Load(image.File); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or IndexOutOfRangeException)
        {
            lock (Problems) Problems.Add($"{image.Mod}: {Path.GetFileName(image.File)}: {ex.Message}");
            return null;
        }
        var surface = Quantize(png.ToArgb(), png.Width, png.Height, palette, transparent, background, x, y, shadow);
        lock (_surfaces) _surfaces[image.File] = (written, palette.AsSpan(0, 768).ToArray(), transparent, background, x, y, shadowKey, surface);
        return surface;
    }

    /// <summary>
    /// The mods' image for image <paramref name="frame"/> of <paramref name="obj"/> in the colours of the room's
    /// palette <paramref name="paletteIndex"/>, as the game shows it, with where it goes in the room; null without one.
    /// </summary>
    /// <summary>The RGB factors an image's shadow darkens by: its table in <paramref name="tables"/>, or COMI's usual one.</summary>
    private static byte[] ShadowOf(Image image, byte[] tables)
    {
        int t = (image?.ShadowTable ?? DefaultShadowTable) * 3;
        if (tables != null && tables.Length >= t + 3 && tables[t] + tables[t + 1] + tables[t + 2] > 0) return tables[t..(t + 3)];
        return DefaultShadowTables[t] + DefaultShadowTables[t + 1] + DefaultShadowTables[t + 2] > 0 ? DefaultShadowTables[t..(t + 3)] : DefaultShadowTables[18..21];
    }

    /// <remarks><paramref name="background"/> is the room's background in indices, for partly see-through pixels.</remarks>
    public (RgbaImage Image, int X, int Y)? Rgba(RoomModel room, RoomObject obj, int frame, int paletteIndex, IndexedSurface background = null)
    {
        if (Find(room, obj, frame) is not { } image) return null;
        var palette = ObimCache.ExtractPaletteContext(room.Room, paletteIndex).rawBytes;
        int transparent = ObimCache.GetTransparentIndex(room.Room, obj.Block, frame);
        int x = image.X ?? obj.X, y = image.Y ?? obj.Y;
        if (Indexed(image, palette, transparent, background, x, y) is not { } surface) return null;
        var rgba = ScummImageUtils.CreateImage(surface.Pixels, surface.Width, surface.Height, surface.Pitch, palette, (byte)transparent);
        return rgba == null ? null : (rgba, x, y);
    }

    /// <summary>
    /// ARGB pixels as palette indices: the index with the pixel's colour, else the nearest. A see-through pixel
    /// is <paramref name="transparent"/>; a partly see-through one is blended over <paramref name="background"/>
    /// (at <paramref name="x"/>,<paramref name="y"/>) where there's background under it, else counts as see-through below half.
    /// </summary>
    internal static IndexedSurface Quantize(uint[] argb, int width, int height, byte[] palette, int transparent,
        IndexedSurface background = null, int x = 0, int y = 0, byte[] shadow = null)
    {
        var pixels = new byte[width * height];
        var nearest = new Dictionary<uint, byte>();
        for (int i = 0; i < pixels.Length; i++)
        {
            uint c = argb[i];
            uint alpha = c >> 24;
            if (alpha == 0) { pixels[i] = (byte)transparent; continue; }
            if (alpha < 255)
            {
                int bx = x + i % width, by = y + i / width;
                if (background != null && bx >= 0 && by >= 0 && bx < background.Width && by < background.Height)
                {
                    int under = background.Pixels[by * background.Pitch + bx];
                    // A dark see-through pixel at least a fifth opaque (the export's 40% black) is a shadow, drawn as the
                    // game draws a costume's: what's under it times the shadow table. Others are blended.
                    bool dark = ((c >> 16) & 0xFF) < 48 && ((c >> 8) & 0xFF) < 48 && (c & 0xFF) < 48;
                    c = dark && alpha >= 51 && shadow is { Length: >= 3 } ? Shade(under, palette, shadow) : Blend(c, under, palette);
                }
                else if (alpha < 128) { pixels[i] = (byte)transparent; continue; }
            }
            uint rgb = c & 0xFFFFFF;
            if (!nearest.TryGetValue(rgb, out byte index)) nearest[rgb] = index = Nearest(rgb, palette, transparent);
            pixels[i] = index;
        }
        return new IndexedSurface { Width = width, Height = height, Pitch = width, Pixels = pixels, TransparentIndex = (byte)transparent };
    }

    /// <summary>Palette colour <paramref name="under"/> darkened by a shadow table's RGB factors (of 255), as an opaque RGB.</summary>
    private static uint Shade(int under, byte[] palette, byte[] shadow)
    {
        int Mul(int channel) => palette[under * 3 + channel] * shadow[channel] / 255;
        return 0xFF000000u | (uint)(Mul(0) << 16) | (uint)(Mul(1) << 8) | (uint)Mul(2);
    }

    /// <summary>A partly see-through ARGB pixel over palette colour <paramref name="under"/>, as an opaque RGB.</summary>
    private static uint Blend(uint argb, int under, byte[] palette)
    {
        int a = (int)(argb >> 24);
        int Mix(int over, int shift) => (((int)(argb >> shift) & 0xFF) * a + over * (255 - a) + 127) / 255;
        int r = Mix(palette[under * 3], 16), g = Mix(palette[under * 3 + 1], 8), b = Mix(palette[under * 3 + 2], 0);
        return 0xFF000000u | (uint)(r << 16) | (uint)(g << 8) | (uint)b;
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
