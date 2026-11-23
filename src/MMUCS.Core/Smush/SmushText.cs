using System;
using System.Text;

namespace MMUCS.Core.Smush;

/// <summary>How a cutscene line is placed (the flags word of a <c>TEXT</c> chunk).</summary>
[Flags]
public enum SmushTextFlags
{
    None = 0,
    /// <summary>The position is the middle of each line.</summary>
    Center = 1,
    /// <summary>The position is the right end of each line (unused in COMI).</summary>
    Right = 2,
    /// <summary>Lines are wrapped to fit; COMI then keeps them 10 pixels inside the screen, whatever the chunk's clip says.</summary>
    Wrap = 4,
    /// <summary>A subtitle: hidden when subtitles are turned off. Credits and captions don't have it.</summary>
    Subtitle = 8,
}

/// <summary>
/// One line of text in a cutscene frame (a SMUSH <c>TEXT</c> chunk). A line is repeated in every frame
/// that shows it (the opening has 11,250 chunks for 158 lines), so a frame's chunks are exactly what is on
/// screen.
/// <para>The chunk is 8 little-endian int16s (x, y, flags, then a clip rectangle as left, top, width,
/// height, then one unused) and the string. COMI's strings carry the English text with the line's code
/// and its font and colour: <c>/OPGT485/^f00^c031Captain's log…\r\n</c>. The code (also the voice file's
/// name) looks the line up in <c>language.tab</c> for other languages.</para>
/// </summary>
public sealed record SmushText
{
    public const int HeaderSize = 16;
    /// <summary>The colour when the string doesn't give one.</summary>
    public const int DefaultColor = 15;

    private static readonly Encoding Latin1 = Encoding.Latin1;

    public int X { get; init; }
    public int Y { get; init; }
    public SmushTextFlags Flags { get; init; }
    public int ClipLeft { get; init; }
    public int ClipTop { get; init; }
    public int ClipWidth { get; init; }
    public int ClipHeight { get; init; }
    /// <summary>The line's code without slashes (<c>OPGT485</c>), or null.</summary>
    public string Code { get; init; }
    /// <summary>From <c>^fNN</c>: font<i>N</i>.nut.</summary>
    public int Font { get; init; }
    /// <summary>From <c>^cNNN</c>: picks the palette colours the glyphs are drawn in (see <c>TextRasterizer</c>).</summary>
    public int Color { get; init; } = DefaultColor;
    /// <summary>
    /// What is drawn, one char per byte (Latin-1, so a char is the glyph's number): carriage returns
    /// and the end padding removed, line feeds kept. Codes in the middle of the line stay in.
    /// </summary>
    public string Text { get; init; }

    /// <summary>The <c>text/</c> asset ID (docs/bundle-format.md): the code, lowercased.</summary>
    public string TextId => Code == null ? null : "text/" + Code.ToLowerInvariant();
    public bool IsSubtitle => (Flags & SmushTextFlags.Subtitle) != 0;

    public static SmushText Parse(ReadOnlySpan<byte> chunk)
    {
        if (chunk.Length < HeaderSize) throw new ArgumentException($"TEXT chunk is {chunk.Length} bytes, too short");
        static int S16(ReadOnlySpan<byte> d, int o) => (short)(d[o] | (d[o + 1] << 8));

        var str = chunk[HeaderSize..];
        int nul = str.IndexOf((byte)0);
        if (nul >= 0) str = str[..nul];
        string s = Latin1.GetString(str);

        // "/CODE/": the line's code (Full Throttle strings start with several slashes).
        string code = null;
        int i = 0;
        while (i < s.Length && s[i] == '/') i++;
        if (i > 0)
        {
            int end = s.IndexOf('/', i);
            if (end >= 0) { code = s[i..end]; i = end + 1; }
            else i = 0;
        }

        int font = 0, color = DefaultColor;
        while (i + 1 < s.Length && s[i] == '^')
        {
            if (s[i + 1] == 'f' && i + 3 < s.Length) { font = Digit(s[i + 3]); i += 4; }
            else if (s[i + 1] == 'c' && i + 4 < s.Length) { color = Digit(s[i + 3]) * 10 + Digit(s[i + 4]); i += 5; }
            else break;
        }

        return new SmushText
        {
            X = S16(chunk, 0), Y = S16(chunk, 2), Flags = (SmushTextFlags)S16(chunk, 4),
            ClipLeft = S16(chunk, 6), ClipTop = S16(chunk, 8), ClipWidth = S16(chunk, 10), ClipHeight = S16(chunk, 12),
            Code = code, Font = font, Color = color,
            Text = s[i..].Replace("\r", ""),
        };
    }

    /// <summary>
    /// A digit of a code. The colour reads only the last two digits of <c>^cNNN</c> (the game's own quirk:
    /// <c>^c031</c> is 31), and a non-digit counts for what it is past '0', as in the game.
    /// </summary>
    private static int Digit(char c) => c - '0';
}
