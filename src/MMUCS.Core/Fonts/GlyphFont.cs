using System;
using System.Collections.Generic;
using System.Linq;

namespace MMUCS.Core.Fonts;

/// <summary>
/// One character's picture. A glyph is either indexed (the game's own values, coloured through the text
/// colour and the palette on screen, as the game does) or full colour (a mod's RGBA art, multiplied by the
/// text colour).
/// </summary>
public sealed class Glyph
{
    public int Width { get; init; }
    public int Height { get; init; }
    /// <summary>Where the picture sits from the pen position. The game's NUT fonts store them but never use them (always 0).</summary>
    public int OffsetX { get; init; }
    public int OffsetY { get; init; }
    /// <summary>How far the pen moves after the glyph when a font says so (mods); otherwise its width, as in the game.</summary>
    public int Advance { get; init; }
    public int PenAdvance => Advance > 0 ? Advance : Width;
    /// <summary>Width × height values, or null for a full-colour glyph.</summary>
    public byte[] Indices { get; init; }
    /// <summary>The value in <see cref="Indices"/> that draws nothing.</summary>
    public byte Transparent { get; init; }
    /// <summary>Width × height straight-alpha colours (0xAARRGGBB), or null for an indexed glyph.</summary>
    public uint[] Argb { get; init; }

    public bool IsIndexed => Indices != null;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static readonly Glyph Empty = new() { Indices = [] };
}

/// <summary>Character sizes for laying out text, in screen pixels (640×480 for COMI).</summary>
public interface IGlyphMetrics
{
    /// <summary>How far the pen moves after the character.</summary>
    float CharWidth(char c);
    float CharHeight(char c);
    /// <summary>The height of a line with nothing measurable on it.</summary>
    float LineHeight { get; }
}

/// <summary>
/// A bitmap font: up to 256 glyphs indexed by the character's byte (Windows-1252 / Latin-1 in COMI's
/// text). Loaded from the game's NUT files (<see cref="NutFont"/>) or from an unpacked font folder
/// (<see cref="FontPack"/>), which is how fonts are modded.
/// </summary>
public sealed class GlyphFont : IGlyphMetrics
{
    /// <summary>The value COMI's fonts use for their dark outline (and font 0 for its only ink, see <see cref="Ink"/>).</summary>
    public const byte OutlineValue = 225;

    /// <param name="roles">The ink and outline values when known (a font pack says them); otherwise found from the glyphs.</param>
    public GlyphFont(string name, IReadOnlyList<Glyph> glyphs, int lineHeight = 0, (byte Ink, byte? Outline)? roles = null)
    {
        Name = name;
        Glyphs = glyphs;
        LineHeight = lineHeight > 0 ? lineHeight : glyphs.Count == 0 ? 0 : glyphs.Max(g => g.Height);
        (Ink, Outline) = roles ?? FindRoles(glyphs);
    }

    /// <summary>The font's asset name (<c>font0</c>).</summary>
    public string Name { get; }
    public IReadOnlyList<Glyph> Glyphs { get; }
    public int Count => Glyphs.Count;
    /// <summary>The game takes the height of the font from its glyphs (all the same in COMI's fonts).</summary>
    public float LineHeight { get; }

    /// <summary>
    /// The value that draws the letters themselves: what a replacement font (a TTF, or full-colour glyphs)
    /// is coloured as. 225 in font 0, 1 in fonts 1–3, 224 in font 4.
    /// </summary>
    public byte Ink { get; }
    /// <summary>The value of the dark edge around the letters, or null when the font has none (fonts 0 and 3).</summary>
    public byte? Outline { get; }

    public Glyph this[char c] => c < Glyphs.Count ? Glyphs[c] : Glyph.Empty;

    public float CharWidth(char c) => this[c].PenAdvance;
    public float CharHeight(char c) => c < Glyphs.Count ? Glyphs[c].Height : LineHeight;

    private (bool AllRound, bool Shadow)? _edge;

    /// <summary>
    /// Whether the dark edge (<see cref="Outline"/>) goes all round the letters: a pixel on every side, as
    /// in fonts 1 and 2. Font 4's edge is only a shadow.
    /// </summary>
    public bool OutlineAllRound => (_edge ??= MeasureEdge()).AllRound;

    /// <summary>
    /// Whether the dark edge is heavier down and to the right: a hard drop shadow a pixel down-right (fonts 1,
    /// 2 and 4), which a replacement typeface draws too.
    /// </summary>
    public bool HasShadow => (_edge ??= MeasureEdge()).Shadow;

    /// <summary>
    /// Where the edge lies against the ink, over every glyph: an outline has as much edge left as right of the
    /// letters; a shadow has half as much again two pixels right and below as left and above.
    /// </summary>
    private (bool, bool) MeasureEdge()
    {
        if (Outline is not { } edge) return (false, false);
        int left = 0, right = 0, farLeft = 0, farRight = 0, farUp = 0, farDown = 0;
        foreach (var g in Glyphs)
        {
            if (!g.IsIndexed || g.Width == 0) continue;
            bool Is(int x, int y, byte v) => x >= 0 && y >= 0 && x < g.Width && y < g.Height && g.Indices[y * g.Width + x] == v;
            for (int y = 0; y < g.Height; y++)
                for (int x = 0; x < g.Width; x++)
                {
                    if (!Is(x, y, Ink)) continue;
                    if (Is(x - 1, y, edge)) left++;
                    if (Is(x + 1, y, edge)) right++;
                    if (!Is(x - 1, y, Ink) && Is(x - 2, y, edge)) farLeft++;
                    if (!Is(x + 1, y, Ink) && Is(x + 2, y, edge)) farRight++;
                    if (!Is(x, y - 1, Ink) && Is(x, y - 2, edge)) farUp++;
                    if (!Is(x, y + 1, Ink) && Is(x, y + 2, edge)) farDown++;
                }
        }
        return (left * 2 >= right, (farRight + farDown) * 2 > (farLeft + farUp) * 3);
    }

    private (float Cap, float Baseline)? _capMetrics;

    /// <summary>
    /// How tall the capitals' ink is, in pixels (outline not counted): measured from H, I and E. A
    /// replacement typeface is sized to match it, so its letters look as big as the game's whatever its
    /// own line spacing.
    /// </summary>
    public float CapHeight => (_capMetrics ??= MeasureCaps()).Cap;
    /// <summary>The row under the capitals' ink, from the top of the glyph cell: where a replacement typeface's baseline goes.</summary>
    public float Baseline => (_capMetrics ??= MeasureCaps()).Baseline;

    private (float Cap, float Baseline) MeasureCaps()
    {
        int top = int.MaxValue, bottom = int.MinValue;
        foreach (char c in "HIE")
        {
            var g = this[c];
            if (g.IsEmpty) continue;
            for (int y = 0; y < g.Height; y++)
            {
                for (int x = 0; x < g.Width; x++)
                {
                    int i = y * g.Width + x;
                    bool ink = g.IsIndexed ? g.Indices[i] == Ink : g.Argb[i] >> 24 >= 128;
                    if (!ink) continue;
                    top = Math.Min(top, y + g.OffsetY);
                    bottom = Math.Max(bottom, y + g.OffsetY + 1);
                    break;
                }
            }
        }
        // No capitals to measure: the proportions of COMI's font 0 (27 px line, 15 px capitals, baseline at 21).
        if (bottom <= top) return (LineHeight * 0.56f, LineHeight * 0.78f);
        return (bottom - top, bottom);
    }

    /// <summary>
    /// The ink and outline values, from how often each appears: 0 and 225 are the outline when the font
    /// has anything else, everything else is ink (the most used of it wins).
    /// </summary>
    private static (byte Ink, byte? Outline) FindRoles(IReadOnlyList<Glyph> glyphs)
    {
        var counts = new int[256];
        foreach (var g in glyphs)
        {
            if (!g.IsIndexed) continue;
            foreach (byte v in g.Indices)
                if (v != g.Transparent) counts[v]++;
        }
        static bool Dark(int v) => v is 0 or OutlineValue;
        bool hasLight = Enumerable.Range(0, 256).Any(v => counts[v] > 0 && !Dark(v));
        int ink = -1, outline = -1;
        for (int v = 0; v < 256; v++)
        {
            if (counts[v] == 0) continue;
            if (hasLight && Dark(v)) { if (outline < 0 || counts[v] > counts[outline]) outline = v; }
            else if (ink < 0 || counts[v] > counts[ink]) ink = v;
        }
        return ((byte)Math.Max(ink, 0), outline < 0 ? null : (byte)outline);
    }
}
