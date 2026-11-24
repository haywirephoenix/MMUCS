using System;
using System.Collections.Generic;
using System.Linq;
using MMUCS.Core.Fonts;

/// <summary>
/// <c>mprobe fonts [N] [--char C …]</c>: the game's NUT fonts (charset N is fontN.nut) as MMUCS reads them:
/// glyphs, line height, the ink and dark-edge values and what the edge is (an outline all round, a drop
/// shadow), every pixel value with how often it's used, and glyphs drawn as text.
/// </summary>
internal static class FontsCommand
{
    public static int Run(List<string> a, Func<List<string>, string, string> takeOption, string gameFile)
    {
        var chars = new List<char>();
        for (string c; (c = takeOption(a, "--char")) != null;)
            chars.Add(c.Length == 1 ? c[0] : int.TryParse(c, out int code) ? (char)code : throw new UsageException($"--char {c}: one character or its code"));
        int? only = a.Count > 1 ? int.TryParse(a[1], out int n) ? n : throw new UsageException($"{a[1]} isn't a font number") : null;

        var fonts = NutFont.LoadAll(gameFile);
        if (fonts.Length == 0) throw new UsageException("no fontN.nut beside the game or in its resource folder");
        for (int i = 0; i < fonts.Length; i++)
        {
            if (only is int o && o != i) continue;
            var f = fonts[i];
            if (f == null)
            {
                Console.WriteLine($"font {i}: none");
                continue;
            }
            var counts = new long[256];
            int glyphs = 0;
            foreach (var g in f.Glyphs)
            {
                if (g.IsEmpty) continue;
                glyphs++;
                if (!g.IsIndexed) continue;
                foreach (byte v in g.Indices)
                    if (v != g.Transparent) counts[v]++;
            }
            string edge = f.Outline is not { } e ? "none"
                : $"{e} ({(f.OutlineAllRound ? "outline all round" : "no outline")}{(f.HasShadow ? ", drop shadow down-right" : "")})";
            Console.WriteLine($"font {i} ({f.Name}): {glyphs} glyphs, line height {f.LineHeight}, capitals {f.CapHeight} high, baseline {f.Baseline}");
            Console.WriteLine($"  ink {f.Ink}, dark edge {edge}");
            Console.WriteLine("  values (in room text 1 is drawn in the text's colour, the rest as palette indices): " +
                              string.Join(" ", Enumerable.Range(0, 256).Where(v => counts[v] > 0).Select(v => $"{v}×{counts[v]}")));
            foreach (char c in chars) DrawGlyph(f, c);
        }
        return 0;
    }

    /// <summary>A glyph as text: <c>#</c> ink, <c>o</c> the dark edge, <c>.</c> transparent, other values by number.</summary>
    private static void DrawGlyph(GlyphFont f, char c)
    {
        var g = f[c];
        Console.WriteLine($"  '{c}' ({(int)c}): {g.Width}×{g.Height}, offset {g.OffsetX},{g.OffsetY}, advance {g.PenAdvance}");
        if (g.IsEmpty) return;
        for (int y = 0; y < g.Height; y++)
        {
            var row = new System.Text.StringBuilder("    ");
            for (int x = 0; x < g.Width; x++)
            {
                int i = y * g.Width + x;
                if (!g.IsIndexed)
                {
                    row.Append(g.Argb[i] >> 24 >= 128 ? " #" : " .");
                    continue;
                }
                byte v = g.Indices[i];
                row.Append(v == g.Transparent ? " ." : v == f.Ink ? " #" : v == f.Outline ? " o" : $"{v % 100,2}");
            }
            Console.WriteLine(row.ToString().TrimEnd());
        }
    }
}
