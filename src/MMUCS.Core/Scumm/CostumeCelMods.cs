using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MMUCS.Core.Imaging;

namespace MMUCS.Core.Scumm;

/// <summary>
/// Costume cels from the mods (<see cref="GameMods"/>): a PNG where the export writes a costume's cel,
/// "rooms/&lt;room&gt;/costumes/akos_&lt;offset&gt;/cel_&lt;NNN&gt;.png" in a mod, is drawn in place of that cel
/// (<see cref="AkosData.ModCels"/>). It can be any size; the costume folder's one meta.json says where each
/// cel's top-left is from the game cel's, by file name:
/// <code>
/// { "cels": { "cel_037": { "x": -13, "y": -3 }, "cel_048": { "x": -13, "y": 0 } } }
/// </code>
/// (a cel it doesn't list goes at the game cel's corner; <c>mprobe mods --place</c> works the offsets out).
/// The PNG is taken into the costume's own colours, so the actor's colours, shadows and scaling apply as to
/// the game's: an opaque pixel is the costume colour nearest it; a dark pixel at least a fifth opaque (the
/// export's 40% black) is the costume's shadow colour (the one the game cel uses most); anything fainter, or
/// a pale see-through pixel under half opaque, is transparent. The first enabled mod by name with a cel wins.
/// </summary>
public sealed class CostumeCelMods
{
    private const string MetaFile = "meta.json";
    private readonly List<GameMods.Mod> _mods = [];

    /// <summary>The enabled mods that have a rooms/ folder (folder names).</summary>
    public List<string> Mods { get; } = [];

    /// <summary>What couldn't be read: a broken mod.json or meta.json, a PNG that won't decode.</summary>
    public List<string> Problems { get; } = [];

    /// <summary>The costume cels in the mods beside <paramref name="gameFile"/>; none without a mods folder.</summary>
    public static CostumeCelMods Load(string gameFile)
    {
        var mods = new CostumeCelMods();
        foreach (var mod in GameMods.Load(gameFile, mods.Problems))
        {
            if (!mod.Enabled || !Directory.Exists(Path.Combine(mod.Folder, "rooms"))) continue;
            mods._mods.Add(mod);
            mods.Mods.Add(mod.Name);
        }
        return mods;
    }

    /// <summary>A costume's folder from a mod's: rooms/&lt;its room&gt;/costumes/akos_&lt;offset&gt;, or null when it isn't in a room.</summary>
    public static string FolderOf(ScummBlock akos)
    {
        if (akos?.FindParent(ScummTag.LFLF) is not { } lflf) return null;
        int roomNo = 0;
        string name = null;
        if (lflf.GetMetaDataDict() is { } meta)
        {
            if (meta.TryGetValue(MetaValue.From(ScummMeta.LFLF.roomNo), out var no)) roomNo = (int)no;
            if (meta.TryGetValue(MetaValue.From(ScummMeta.LFLF.roomName), out var nm)) name = (string)nm;
        }
        long roomOffset = lflf.FindChild(ScummTag.ROOM)?.Offset ?? lflf.Offset;
        return Path.Combine("rooms", ScummExporters.RoomFolderName(roomNo, name, roomOffset), "costumes", ScummExporters.CostumeFolderName(akos));
    }

    /// <summary>A mod cel found: the mod, its PNG, and the offset meta.json gives (null: none).</summary>
    public sealed record Cel(string Mod, int Number, string File, int? X, int? Y);

    /// <summary>The cels the mods have for the costume in <paramref name="akos"/>, by cel number (the first mod's for each).</summary>
    public Dictionary<int, Cel> Find(ScummBlock akos)
    {
        var cels = new Dictionary<int, Cel>();
        if (_mods.Count == 0 || FolderOf(akos) is not { } relative) return cels;
        foreach (var mod in _mods)
        {
            string dir = Path.Combine(mod.Folder, relative);
            if (!Directory.Exists(dir)) continue;
            var offsets = ReadOffsets(mod, dir);
            foreach (var file in Directory.EnumerateFiles(dir, "cel_*.png").Order(StringComparer.Ordinal))
            {
                string stem = Path.GetFileNameWithoutExtension(file);
                if (!int.TryParse(stem.AsSpan(4), out int number) || cels.ContainsKey(number)) continue;
                var at = offsets.GetValueOrDefault(stem);
                cels[number] = new Cel(mod.Name, number, file, at.X, at.Y);
            }
        }
        return cels;
    }

    /// <summary>The costume folder's meta.json offsets by cel file name ("cel_037").</summary>
    private Dictionary<string, (int? X, int? Y)> ReadOffsets(GameMods.Mod mod, string dir)
    {
        var offsets = new Dictionary<string, (int?, int?)>(StringComparer.OrdinalIgnoreCase);
        string meta = Path.Combine(dir, MetaFile);
        if (!File.Exists(meta)) return offsets;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(meta), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (!doc.RootElement.TryGetProperty("cels", out var cels) || cels.ValueKind != JsonValueKind.Object) return offsets;
            foreach (var p in cels.EnumerateObject())
            {
                int? Int(string name) => p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty(name, out var v) && v.TryGetInt32(out int n) ? n : null;
                offsets[Path.GetFileNameWithoutExtension(p.Name)] = (Int("x"), Int("y"));
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
        {
            lock (Problems) Problems.Add($"{mod.Name}: {Path.Combine(Path.GetFileName(dir), MetaFile)}: {ex.Message}");
        }
        return offsets;
    }

    /// <summary>
    /// Puts the mods' cels for <paramref name="akos"/> into <paramref name="d"/> (its <see cref="AkosData.ModCels"/>),
    /// or clears them when the mods have none. <paramref name="d"/>'s colours (<see cref="AkosData.ResolvedColors"/>)
    /// must be the room palette's, as the parser leaves them.
    /// </summary>
    public void Apply(AkosData d, ScummBlock akos)
    {
        if (d == null) return;
        var found = Find(akos);
        if (found.Count == 0) { d.ModCels = null; return; }
        var cels = new Dictionary<int, AkosModCel>();
        foreach (var (number, cel) in found)
        {
            if (number >= d.CelOffsets.Length) continue;
            PngImage png;
            try { png = PngReader.Load(cel.File); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or IndexOutOfRangeException)
            {
                lock (Problems) Problems.Add($"{cel.Mod}: {Path.GetFileName(cel.File)}: {ex.Message}");
                continue;
            }
            var game = AkosDecoders.DecodeCel(d, number, default);
            cels[number] = new AkosModCel(Quantize(d, png.ToArgb(), png.Width, png.Height, game), cel.X ?? 0, cel.Y ?? 0, cel.File);
        }
        d.ModCels = cels.Count == 0 ? null : cels;
    }

    /// <summary>
    /// ARGB pixels as the costume's own indices (see the class summary); <paramref name="game"/> (the game's cel,
    /// or null) picks the shadow colour.
    /// </summary>
    internal static IndexedSurface Quantize(AkosData d, uint[] argb, int width, int height, IndexedSurface game)
    {
        var akpl = d.Palette.Span;
        bool passthrough = d.IsPassthroughPalette;
        byte transparent = d.TransparentIndex;
        var colors = d.ResolvedColors;
        int count = passthrough ? 256 : akpl.Length;
        var choices = new List<(byte Local, int R, int G, int B)>();
        var shadows = new List<byte>();
        for (int l = 0; l < count && l < 256; l++)
        {
            if (l == transparent) continue;
            int global = AkosFrames.Global((byte)l, akpl, passthrough);
            if (global is > 0 and < 8) { shadows.Add((byte)l); continue; }
            if (global >= colors.Length) continue;
            var c = colors[global];
            choices.Add(((byte)l, (int)Math.Round(c.R * 255), (int)Math.Round(c.G * 255), (int)Math.Round(c.B * 255)));
        }
        int shadow = -1;
        if (shadows.Count > 0)
        {
            // The shadow colour the game's cel draws most, else the costume's first.
            var used = new Dictionary<byte, int>();
            if (game != null)
                foreach (byte v in game.Pixels)
                    if (shadows.Contains(v)) used[v] = used.GetValueOrDefault(v) + 1;
            shadow = used.Count > 0 ? used.MaxBy(kv => kv.Value).Key : shadows[0];
        }

        var pixels = new byte[width * height];
        if (transparent != 0) Array.Fill(pixels, transparent);
        var nearest = new Dictionary<uint, byte>();
        for (int i = 0; i < pixels.Length; i++)
        {
            uint c = argb[i];
            uint alpha = c >> 24;
            if (alpha == 0) continue;
            int r = (int)(c >> 16) & 0xFF, g = (int)(c >> 8) & 0xFF, b = (int)c & 0xFF;
            if (alpha < 255)
            {
                bool dark = r < 48 && g < 48 && b < 48;
                if (dark && alpha >= 51 && shadow >= 0) { pixels[i] = (byte)shadow; continue; }
                if (dark || alpha < 128) continue;
            }
            uint rgb = c & 0xFFFFFF;
            if (!nearest.TryGetValue(rgb, out byte local))
            {
                int best = int.MaxValue;
                foreach (var ch in choices)
                {
                    int dr = ch.R - r, dg = ch.G - g, db = ch.B - b, dist = dr * dr + dg * dg + db * db;
                    if (dist < best) { best = dist; local = ch.Local; if (dist == 0) break; }
                }
                nearest[rgb] = local;
            }
            pixels[i] = local;
        }
        return new IndexedSurface { Width = width, Height = height, Pitch = width, Pixels = pixels, TransparentIndex = transparent };
    }

    /// <summary>
    /// Where the game's cel sits in a mod's (its top-left from the mod cel's, negated: the mod cel's offset), found by
    /// trying every place it fits and counting the pixels that match, with the share of the game cel's pixels that
    /// match there; null when it doesn't fit in the mod's at all.
    /// </summary>
    public static (int X, int Y, double Share)? Place(IndexedSurface game, IndexedSurface mod, byte transparent)
    {
        if (game == null || mod == null || game.Width > mod.Width || game.Height > mod.Height) return null;
        int opaque = 0;
        for (int i = 0; i < game.Height; i++)
            for (int j = 0; j < game.Width; j++)
                if (game.Pixels[i * game.Pitch + j] != transparent) opaque++;
        if (opaque == 0) return null;
        (int X, int Y)? best = null;
        int bestScore = -1;
        for (int y = 0; y <= mod.Height - game.Height; y++)
            for (int x = 0; x <= mod.Width - game.Width; x++)
            {
                int score = 0;
                for (int i = 0; i < game.Height; i++)
                    for (int j = 0; j < game.Width; j++)
                    {
                        byte v = game.Pixels[i * game.Pitch + j];
                        if (v != transparent && mod.Pixels[(y + i) * mod.Pitch + x + j] == v) score++;
                    }
                if (score > bestScore) { bestScore = score; best = (-x, -y); }
            }
        return best is { } at ? (at.X, at.Y, bestScore / (double)opaque) : null;
    }

    /// <summary>
    /// The share of the game cel's pixels the mod's has where an offset of <paramref name="offsetX"/>,<paramref name="offsetY"/>
    /// puts them (a game pixel outside the mod's counts as missing); 0 for a cel with no pixels.
    /// </summary>
    public static double Match(IndexedSurface game, IndexedSurface mod, byte transparent, int offsetX, int offsetY)
    {
        if (game == null || mod == null) return 0;
        int opaque = 0, score = 0;
        for (int i = 0; i < game.Height; i++)
            for (int j = 0; j < game.Width; j++)
            {
                byte v = game.Pixels[i * game.Pitch + j];
                if (v == transparent) continue;
                opaque++;
                int x = j - offsetX, y = i - offsetY;
                if (x >= 0 && y >= 0 && x < mod.Width && y < mod.Height && mod.Pixels[y * mod.Pitch + x] == v) score++;
            }
        return opaque == 0 ? 0 : score / (double)opaque;
    }
}
