using System;
using System.Collections.Generic;

namespace MMUCS.Core.Fonts;

/// <summary>A block of drawn text: premultiplied BGRA pixels (Skia's native order) placed at (Left, Top) on screen.</summary>
public sealed record TextImage(int Left, int Top, int Width, int Height, uint[] Pixels);

/// <summary>
/// Draws laid-out text with bitmap fonts, in the colours COMI's cutscenes give it.
/// <para>Indexed glyphs are coloured the way COMI's SMUSH player does it (see <see cref="MapValue"/>), then
/// looked up in the palette on screen, so text fades with the picture. Full-colour glyphs (mods) are
/// multiplied by the colour the font's ink would have been.</para>
/// </summary>
public static class TextRasterizer
{
    /// <summary>
    /// The palette index a glyph value is drawn in, or -1 for nothing. Of a glyph's values, the one that
    /// is the colour code negated (as a signed byte) becomes 255, 225 (−31) becomes 0, and the rest are
    /// palette indices as they are: <c>^c031</c> turns font 0's ink (225) into colour 255, and <c>^c001</c>
    /// leaves it 225 and so 0 (the credits are drawn with it).
    /// </summary>
    public static int MapValue(byte value, byte transparent, int color)
    {
        if (value == transparent) return -1;
        sbyte c = (sbyte)(color == -1 ? 1 : color);
        if (value == (byte)-c) return 0xFF;
        if (value == GlyphFont.OutlineValue) return 0;
        return value;
    }

    /// <summary>
    /// Draws <paramref name="layout"/> with <paramref name="font"/> against <paramref name="paletteRgb"/>
    /// (256 RGB triplets), clipped to the layout's clip. Null when nothing shows.
    /// </summary>
    public static TextImage Draw(TextLayout layout, GlyphFont font, ReadOnlySpan<byte> paletteRgb)
    {
        var clip = layout.Clip;
        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        var placed = new List<(Glyph Glyph, int X, int Y, int Color)>(layout.Chars.Count);
        foreach (var ch in layout.Chars)
        {
            var g = font[ch.Code];
            if (g.IsEmpty) continue;
            int x = (int)MathF.Round(ch.X) + g.OffsetX, y = (int)MathF.Round(ch.Y) + g.OffsetY;
            int x0 = Math.Max(x, clip.Left), y0 = Math.Max(y, clip.Top);
            int x1 = Math.Min(x + g.Width, clip.Right), y1 = Math.Min(y + g.Height, clip.Bottom);
            if (x1 <= x0 || y1 <= y0) continue;
            placed.Add((g, x, y, ch.Color));
            left = Math.Min(left, x0); top = Math.Min(top, y0);
            right = Math.Max(right, x1); bottom = Math.Max(bottom, y1);
        }
        if (placed.Count == 0) return null;

        int w = right - left, h = bottom - top;
        var pixels = new uint[w * h];
        Span<uint> lut = stackalloc uint[256];
        for (int i = 0; i < 256; i++)
        {
            int o = i * 3;
            lut[i] = o + 2 < paletteRgb.Length
                ? 0xFF000000u | ((uint)paletteRgb[o] << 16) | ((uint)paletteRgb[o + 1] << 8) | paletteRgb[o + 2]
                : 0xFF000000u;
        }

        foreach (var (g, gx, gy, color) in placed)
        {
            int x0 = Math.Max(gx, left), y0 = Math.Max(gy, top);
            int x1 = Math.Min(gx + g.Width, right), y1 = Math.Min(gy + g.Height, bottom);
            uint tint = g.IsIndexed ? 0 : InkColor(font, color, lut);
            for (int y = y0; y < y1; y++)
            {
                int src = (y - gy) * g.Width - gx, dst = (y - top) * w - left;
                for (int x = x0; x < x1; x++)
                {
                    if (g.IsIndexed)
                    {
                        int index = MapValue(g.Indices[src + x], g.Transparent, color);
                        if (index >= 0) pixels[dst + x] = lut[index];
                    }
                    else
                    {
                        pixels[dst + x] = Over(Premultiply(Multiply(g.Argb[src + x], tint)), pixels[dst + x]);
                    }
                }
            }
        }
        return new TextImage(left, top, w, h, pixels);
    }

    /// <summary>The colour the font's ink is drawn in for a colour code, as opaque 0xAARRGGBB.</summary>
    public static uint InkColor(GlyphFont font, int color, ReadOnlySpan<uint> lut)
    {
        int index = MapValue(font.Ink, NoTransparent(font.Ink), color);
        return lut[index < 0 ? 0xFF : index];
    }

    /// <summary>The colour of the font's outline for a colour code, or null when it has none.</summary>
    public static uint? OutlineColor(GlyphFont font, int color, ReadOnlySpan<uint> lut)
    {
        if (font.Outline is not { } v) return null;
        int index = MapValue(v, NoTransparent(v), color);
        return lut[index < 0 ? 0 : index];
    }

    /// <summary>A transparent value that can't be <paramref name="v"/>.</summary>
    private static byte NoTransparent(byte v) => (byte)(v + 1);

    private static uint Multiply(uint argb, uint tint)
    {
        uint a = argb >> 24;
        uint r = ((argb >> 16) & 0xFF) * ((tint >> 16) & 0xFF) / 255;
        uint g = ((argb >> 8) & 0xFF) * ((tint >> 8) & 0xFF) / 255;
        uint b = (argb & 0xFF) * (tint & 0xFF) / 255;
        return (a << 24) | (r << 16) | (g << 8) | b;
    }

    private static uint Premultiply(uint argb)
    {
        uint a = argb >> 24;
        if (a == 255) return argb;
        uint r = ((argb >> 16) & 0xFF) * a / 255, g = ((argb >> 8) & 0xFF) * a / 255, b = (argb & 0xFF) * a / 255;
        return (a << 24) | (r << 16) | (g << 8) | b;
    }

    /// <summary>Premultiplied source over destination.</summary>
    private static uint Over(uint src, uint dst)
    {
        uint a = src >> 24;
        if (a == 255 || dst == 0) return src;
        if (a == 0) return dst;
        uint inv = 255 - a;
        uint ra = a + (dst >> 24) * inv / 255;
        uint rr = ((src >> 16) & 0xFF) + ((dst >> 16) & 0xFF) * inv / 255;
        uint rg = ((src >> 8) & 0xFF) + ((dst >> 8) & 0xFF) * inv / 255;
        uint rb = (src & 0xFF) + (dst & 0xFF) * inv / 255;
        return (ra << 24) | (rr << 16) | (rg << 8) | rb;
    }
}
