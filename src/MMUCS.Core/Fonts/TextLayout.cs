using System;
using System.Collections.Generic;
using MMUCS.Core.Smush;

namespace MMUCS.Core.Fonts;

/// <summary>A character placed on screen: its glyph's top-left corner and the colour code in force there.</summary>
public readonly record struct PlacedChar(char Code, float X, float Y, int Color);

/// <summary>A pixel rectangle (right and bottom exclusive).</summary>
public readonly record struct TextRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

/// <summary>Laid-out text: where each character goes, and the rectangle nothing may be drawn outside.</summary>
public sealed class TextLayout
{
    public List<PlacedChar> Chars { get; } = [];
    public TextRect Clip { get; init; }
    public int Font { get; init; }
}

/// <summary>
/// Places COMI's text the way its v8 interpreter does, for any font: the game's NUT fonts give whole
/// pixels and so the game's exact positions, a replacement font its own sizes.
/// <list type="bullet">
/// <item>Without <see cref="SmushTextFlags.Wrap"/>: each line (split at line feeds) at (x, y), centred on x
/// with <see cref="SmushTextFlags.Center"/>; the clip is the whole screen.</item>
/// <item>With it: words are wrapped to the clip's width and the block is pushed back inside the clip
/// (up from the bottom, then in from the sides). COMI replaces the chunk's clip with the screen less 10
/// pixels each side.</item>
/// <item>A line's height is its tallest character + 1; <c>^cNNN</c> inside the text changes colour from
/// there on, <c>^fNN</c> and <c>^l</c> are skipped (SMUSH text can't change font mid-line).</item>
/// </list>
/// The rules were learned from ScummVM's TextRenderer_v7 (which follows COMI's interpreter) and checked
/// against the game.
/// </summary>
public static class TextLayouter
{
    /// <summary>COMI's margin around wrapped cutscene text.</summary>
    public const int WrapMargin = 10;

    public static TextLayout Layout(SmushText text, IGlyphMetrics font, int screenWidth, int screenHeight)
    {
        bool wrap = (text.Flags & SmushTextFlags.Wrap) != 0;
        var clip = wrap
            ? Rect(WrapMargin, WrapMargin, screenWidth - 2 * WrapMargin, screenHeight - 2 * WrapMargin, screenWidth, screenHeight)
            : new TextRect(0, 0, screenWidth, screenHeight);
        var layout = new TextLayout { Clip = clip, Font = text.Font };
        var align = text.Flags & (SmushTextFlags.Center | SmushTextFlags.Right);
        if (wrap) LayoutWrapped(layout, text.Text, font, text.X, text.Y, align, text.Color);
        else LayoutLines(layout, text.Text, font, text.X, text.Y, align, text.Color);
        return layout;
    }

    private static TextRect Rect(int left, int top, int width, int height, int sw, int sh)
    {
        return new TextRect(Math.Max(0, left), Math.Max(0, top), Math.Min(left + width, sw), Math.Min(top + height, sh));
    }

    private static void LayoutLines(TextLayout layout, string s, IGlyphMetrics font, float x, float y, SmushTextFlags align, int color)
    {
        int start = 0;
        for (int pos = 0; pos <= s.Length; pos++)
        {
            if (pos < s.Length && s[pos] != '\n') continue;
            int len = pos - start;
            if (y < layout.Clip.Bottom)
            {
                color = Place(layout, s, start, len, font, LineX(x, Width(s, start, len, font), align), y, color);
                y += Height(s, start, len, font);
            }
            start = pos + 1;
        }
    }

    private static void LayoutWrapped(TextLayout layout, string s, IGlyphMetrics font, float x, float y, SmushTextFlags align, int color)
    {
        static bool Space(char c) => c == ' ';
        static bool Break(char c) => c is ' ' or '\n';

        // Each line as (start, length, width).
        var lines = new List<(int Start, int Length, float Width)>();
        var clip = layout.Clip;
        int len = s.Length, lineStart = 0, cur = -1;
        float lineWidth = 0;
        while (cur < len)
        {
            int wordStart = cur + 1;
            while (wordStart < len && Space(s[wordStart])) wordStart++;
            float gap = cur > 0 ? Width(s, cur, wordStart - cur, font) : 0;

            int next = wordStart;
            while (next < len && !Break(s[next])) next++;
            float word = Width(s, wordStart, next - wordStart, font);
            float width = lineWidth + gap + word;

            if (lineWidth > 0 && width > clip.Width)
            {
                lines.Add((lineStart, cur - lineStart, lineWidth));
                width = word;
                lineStart = wordStart;
            }
            lineWidth = width;
            cur = next;
            // A line feed ends the line where it is.
            if (cur < len && !Space(s[cur]))
            {
                lines.Add((lineStart, cur - lineStart, lineWidth));
                lineStart = cur + 1;
                lineWidth = 0;
            }
        }
        if (lineWidth > 0) lines.Add((lineStart, Math.Min(cur, len) - lineStart, lineWidth));

        float maxWidth = 0, height = 0, lastHeight = 0;
        foreach (var l in lines)
        {
            maxWidth = Math.Max(maxWidth, l.Width);
            lastHeight = l.Length > 0 ? Height(s, l.Start, l.Length, font) : 0;
            height += lastHeight;
        }

        // Keep the block inside the clip: up from the bottom (half a line to spare), down from the top,
        // then in from the sides.
        float clipHeight = height + (int)lastHeight / 2;
        if (y > clip.Bottom - clipHeight) y = clip.Bottom - clipHeight;
        if (y < clip.Top) y = clip.Top;
        float half = (int)maxWidth >> 1;
        switch (align)
        {
            case SmushTextFlags.Center:
                if (x + half > clip.Right) x = clip.Right - half;
                if (x - half < clip.Left) x = clip.Left + half;
                break;
            case SmushTextFlags.Right:
                if (x > clip.Right) x = clip.Right;
                if (x < clip.Left + maxWidth) x = clip.Left + maxWidth;
                break;
            default:
                if (x > clip.Right - maxWidth) x = clip.Right - maxWidth;
                if (x < clip.Left) x = clip.Left;
                break;
        }

        foreach (var l in lines)
        {
            int n = Math.Max(0, l.Length);
            color = Place(layout, s, l.Start, n, font, LineX(x, l.Width, align), y, color);
            y += Height(s, l.Start, n, font);
        }
    }

    private static float LineX(float x, float width, SmushTextFlags align)
    {
        return align switch
        {
            SmushTextFlags.Center => x - MathF.Floor(width / 2),
            SmushTextFlags.Right => x - width,
            _ => x,
        };
    }

    /// <summary>Puts a line's characters at the pen, which starts at (x, y). Returns the colour in force after it.</summary>
    private static int Place(TextLayout layout, string s, int start, int len, IGlyphMetrics font, float x, float y, int color)
    {
        int end = Math.Min(s.Length, start + len);
        for (int i = start; i < end; i++)
        {
            int skip = CodeLength(s, i, ref color);
            if (skip > 0) { i += skip - 1; continue; }
            char c = s[i];
            if (c == '\n') continue;
            layout.Chars.Add(new PlacedChar(c, x, y, color));
            x += font.CharWidth(c);
        }
        return color;
    }

    /// <summary>The width of the widest line in a piece of text.</summary>
    public static float Width(string s, int start, int len, IGlyphMetrics font)
    {
        float max = 0, width = 0;
        int end = Math.Min(s.Length, start + len), ignored = 0;
        for (int i = start; i < end; i++)
        {
            int skip = CodeLength(s, i, ref ignored);
            if (skip > 0) { i += skip - 1; continue; }
            if (s[i] == '\n') { max = Math.Max(max, width); width = 0; }
            else width += font.CharWidth(s[i]);
        }
        return Math.Max(max, width);
    }

    /// <summary>The height of a piece of text: each line's tallest character (or the font's line height) + 1.</summary>
    public static float Height(string s, int start, int len, IGlyphMetrics font)
    {
        float total = 0, line = 0;
        int end = Math.Min(s.Length, start + len), ignored = 0;
        for (int i = start; i < end; i++)
        {
            int skip = CodeLength(s, i, ref ignored);
            if (skip > 0) { i += skip - 1; continue; }
            if (s[i] == '\n')
            {
                total += (line > 0 ? line : font.LineHeight) + 1;
                line = 0;
            }
            else line = Math.Max(line, font.CharHeight(s[i]));
        }
        return total + (line > 0 ? line : font.LineHeight) + 1;
    }

    /// <summary>The length of a <c>^</c> code at <paramref name="i"/> (0 when there's none); a colour code sets <paramref name="color"/>.</summary>
    private static int CodeLength(string s, int i, ref int color)
    {
        if (s[i] != '^' || i + 1 >= s.Length) return 0;
        switch (s[i + 1])
        {
            case 'f': return i + 3 < s.Length ? 4 : 0;
            case 'c':
                if (i + 4 >= s.Length) return 0;
                color = (s[i + 3] - '0') * 10 + (s[i + 4] - '0');
                return 5;
            case 'l': return 2;
            default: return 0;
        }
    }
}
