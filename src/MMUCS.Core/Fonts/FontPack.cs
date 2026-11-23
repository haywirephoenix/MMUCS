using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MMUCS.Core.Diagnostics;
using MMUCS.Core.Imaging;

namespace MMUCS.Core.Fonts;

/// <summary>
/// A font unpacked into editable files, following the asset IDs in docs/bundle-format.md:
/// <code>
/// out/font/0000/font.json          line height, ink and outline values, and every glyph's size and offset
/// out/font/0000/glyph/0065.png     one glyph per character number (65 is 'A')
/// </code>
/// The same folder is how a font is modded: edit or replace glyph PNGs (any size) and load the folder in
/// place of the game's font.
/// <list type="bullet">
/// <item>An <b>indexed</b> PNG keeps the game's values (palette index = value), so the glyph is coloured
/// as the game colours it. The exported palette only makes the values visible (ink white, outline black,
/// transparent clear); the colours on screen come from the cutscene. Transparent is the first palette
/// entry with alpha 0.</item>
/// <item>A <b>full-colour</b> PNG (RGB, RGBA, greyscale) is drawn as painted, multiplied by the colour
/// the font's ink would have been: white art takes the text's colour.</item>
/// <item>A glyph without a PNG keeps the font it replaces (the game's), so a pack can change a few
/// characters. font.json may give a glyph an <c>advance</c> (pen movement) and <c>x</c>/<c>y</c> offsets.</item>
/// </list>
/// </summary>
public static class FontPack
{
    public const string MetricsFile = "font.json";
    public const string GlyphFolder = "glyph";

    /// <summary>The asset folder of font <paramref name="number"/> under an export root (<c>out/font/0003</c>).</summary>
    public static string FolderOf(string root, int number) => Path.Combine(root, "font", number.ToString("D4"));

    /// <summary>Writes a font's folder; returns the number of files written.</summary>
    public static int Export(GlyphFont font, int number, string root, string source = null)
    {
        string dir = FolderOf(root, number);
        string glyphDir = Path.Combine(dir, GlyphFolder);
        Directory.CreateDirectory(glyphDir);
        int written = 0;

        // Values shown as: ink white, outline black, transparent clear, anything else a grey of its own.
        var palette = new byte[768];
        var alpha = new byte[256];
        for (int v = 0; v < 256; v++)
        {
            byte grey = v == font.Ink ? (byte)255 : v == font.Outline ? (byte)0 : (byte)(64 + v / 2);
            palette[v * 3] = palette[v * 3 + 1] = palette[v * 3 + 2] = grey;
            alpha[v] = 255;
        }

        for (int code = 0; code < font.Count; code++)
        {
            var g = font.Glyphs[code];
            if (g.IsEmpty) continue;
            using var fs = File.Create(Path.Combine(glyphDir, code.ToString("D4") + ".png"));
            if (g.IsIndexed)
            {
                var a = (byte[])alpha.Clone();
                a[g.Transparent] = 0;
                PngWriter.WriteIndexed(fs, g.Width, g.Height, g.Indices, palette, alpha: a);
            }
            else
            {
                var rgba = new byte[g.Argb.Length * 4];
                for (int i = 0; i < g.Argb.Length; i++)
                {
                    uint c = g.Argb[i];
                    rgba[i * 4] = (byte)(c >> 16); rgba[i * 4 + 1] = (byte)(c >> 8); rgba[i * 4 + 2] = (byte)c; rgba[i * 4 + 3] = (byte)(c >> 24);
                }
                PngWriter.Write(fs, g.Width, g.Height, rgba);
            }
            written++;
        }

        using (var fs = File.Create(Path.Combine(dir, MetricsFile)))
        using (var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("id", "font/" + number.ToString("D4"));
            w.WriteString("name", font.Name);
            if (source != null) w.WriteString("source", source);
            w.WriteNumber("lineHeight", font.LineHeight);
            w.WriteNumber("ink", font.Ink);
            if (font.Outline is { } o) w.WriteNumber("outline", o); else w.WriteNull("outline");
            w.WriteString("glyphFiles", GlyphFolder + "/{code:D4}.png");
            w.WriteStartArray("glyphs");
            for (int code = 0; code < font.Count; code++)
            {
                var g = font.Glyphs[code];
                // One line per glyph keeps the file easy to read and diff.
                var line = new System.Text.StringBuilder();
                line.Append($"{{\"code\":{code},\"width\":{g.Width},\"height\":{g.Height}");
                if (g.OffsetX != 0 || g.OffsetY != 0) line.Append($",\"x\":{g.OffsetX},\"y\":{g.OffsetY}");
                if (g.Advance > 0) line.Append($",\"advance\":{g.Advance}");
                if (g.IsIndexed) line.Append($",\"transparent\":{g.Transparent}");
                w.WriteRawValue(line.Append('}').ToString(), skipInputValidation: true);
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return written + 1;
    }

    /// <summary>The font number a font folder declares (<c>"id": "font/0003"</c>), else its folder name, or -1.</summary>
    public static int NumberOf(string dir)
    {
        try
        {
            string metrics = Path.Combine(dir, MetricsFile);
            if (File.Exists(metrics))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(metrics));
                if (doc.RootElement.TryGetProperty("id", out var id) && id.GetString() is { } s
                    && s.StartsWith("font/", StringComparison.Ordinal) && int.TryParse(s[5..], out int n))
                    return n;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException) { }
        return int.TryParse(Path.GetFileName(Path.TrimEndingDirectorySeparator(dir)), out int f) ? f : -1;
    }

    /// <summary>
    /// The font folders at or under <paramref name="path"/>: a font folder itself, an export root
    /// (holding <c>font/NNNN</c>) or a <c>font</c> folder. Keyed by font number.
    /// </summary>
    public static Dictionary<int, string> Find(string path)
    {
        var found = new Dictionary<int, string>();
        if (!Directory.Exists(path)) return found;
        void Consider(string dir)
        {
            if (!File.Exists(Path.Combine(dir, MetricsFile)) && !Directory.Exists(Path.Combine(dir, GlyphFolder))) return;
            int n = NumberOf(dir);
            if (n >= 0) found.TryAdd(n, dir);
        }
        Consider(path);
        if (found.Count > 0) return found;
        string fontDir = Path.Combine(path, "font");
        foreach (var dir in Directory.GetDirectories(Directory.Exists(fontDir) ? fontDir : path).OrderBy(d => d, StringComparer.Ordinal))
            Consider(dir);
        return found;
    }

    /// <summary>
    /// Loads a font folder. Glyphs it has no PNG for come from <paramref name="fallback"/> (the font it
    /// replaces), so a pack may change only some characters.
    /// </summary>
    public static GlyphFont Load(string dir, GlyphFont fallback = null)
    {
        var meta = new Dictionary<int, JsonElement>();
        int lineHeight = 0;
        (byte, byte?)? roles = fallback == null ? null : (fallback.Ink, fallback.Outline);
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(dir));
        string metrics = Path.Combine(dir, MetricsFile);
        JsonDocument doc = null;
        if (File.Exists(metrics))
        {
            doc = JsonDocument.Parse(File.ReadAllText(metrics));
            var root = doc.RootElement;
            if (root.TryGetProperty("lineHeight", out var lh) && lh.TryGetDouble(out double l)) lineHeight = (int)Math.Round(l);
            if (root.TryGetProperty("name", out var n) && n.GetString() is { } s) name = s;
            if (root.TryGetProperty("ink", out var ink) && ink.TryGetByte(out byte inkValue))
                roles = (inkValue, root.TryGetProperty("outline", out var o) && o.TryGetByte(out byte ov) ? ov : null);
            if (root.TryGetProperty("glyphs", out var glyphs))
                foreach (var g in glyphs.EnumerateArray())
                    if (g.TryGetProperty("code", out var c)) meta[c.GetInt32()] = g;
        }

        try
        {
            var files = new Dictionary<int, string>();
            string glyphDir = Path.Combine(dir, GlyphFolder);
            if (Directory.Exists(glyphDir))
                foreach (var f in Directory.GetFiles(glyphDir, "*.png"))
                    if (int.TryParse(Path.GetFileNameWithoutExtension(f), out int code) && code is >= 0 and < 256)
                        files[code] = f;

            int count = Math.Max(fallback?.Count ?? 0, files.Count == 0 ? 0 : files.Keys.Max() + 1);
            var result = new Glyph[count];
            for (int code = 0; code < count; code++)
            {
                meta.TryGetValue(code, out var m);
                var baseGlyph = fallback != null && code < fallback.Count ? fallback.Glyphs[code] : Glyph.Empty;
                if (!files.TryGetValue(code, out var file))
                {
                    result[code] = baseGlyph;
                    continue;
                }
                try { result[code] = LoadGlyph(file, m, baseGlyph); }
                catch (Exception ex) when (ex is InvalidDataException or IOException or EndOfStreamException)
                {
                    Log.Warn($"Font {name}: {Path.GetFileName(file)}: {ex.Message}");
                    result[code] = baseGlyph;
                }
            }
            return new GlyphFont(name, result, lineHeight > 0 ? lineHeight : (int)(fallback?.LineHeight ?? 0), roles);
        }
        finally { doc?.Dispose(); }
    }

    private static Glyph LoadGlyph(string file, JsonElement meta, Glyph baseGlyph)
    {
        int Int(string key, int otherwise) =>
            meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty(key, out var v) && v.TryGetInt32(out int i) ? i : otherwise;

        var png = PngReader.Load(file);
        int x = Int("x", baseGlyph.OffsetX), y = Int("y", baseGlyph.OffsetY), advance = Int("advance", 0);
        if (png.IsIndexed)
        {
            int transparent = Array.FindIndex(png.Palette, c => c >> 24 == 0);
            if (transparent < 0) transparent = Int("transparent", baseGlyph.IsIndexed ? baseGlyph.Transparent : 0);
            return new Glyph
            {
                Width = png.Width, Height = png.Height, OffsetX = x, OffsetY = y, Advance = advance,
                Indices = png.Indices, Transparent = (byte)transparent,
            };
        }
        return new Glyph { Width = png.Width, Height = png.Height, OffsetX = x, OffsetY = y, Advance = advance, Argb = png.Argb };
    }
}
