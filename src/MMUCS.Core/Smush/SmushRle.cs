using System;

namespace MMUCS.Core.Smush;

/// <summary>
/// The RLE used by SMUSH: the same token stream as BOMP and AKOS CDAT. A control byte gives a count
/// of (b &gt;&gt; 1) + 1; with bit 0 set the next byte is repeated that many times, otherwise that
/// many literal bytes follow.
/// </summary>
public static class SmushRle
{
    /// <summary>Decodes one continuous stream into <paramref name="dst"/> (codec 47's compression 5).</summary>
    public static void DecodeStream(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int pos = 0, o = 0;
        while (o < dst.Length && pos < src.Length)
        {
            byte ctrl = src[pos++];
            int count = Math.Min((ctrl >> 1) + 1, dst.Length - o);
            if ((ctrl & 1) != 0)
            {
                if (pos >= src.Length) break;
                dst.Slice(o, count).Fill(src[pos++]);
            }
            else
            {
                count = Math.Min(count, src.Length - pos);
                src.Slice(pos, count).CopyTo(dst[o..]);
                pos += count;
            }
            o += count;
        }
    }

    /// <summary>
    /// Codec 1 (and 3): one RLE line per row, each prefixed with its byte length (u16 LE), drawn onto
    /// the screen at (left, top). Colour 0 is transparent and leaves the screen as it was.
    /// </summary>
    public static void DrawCodec1(ReadOnlySpan<byte> src, byte[] screen, int screenWidth, int screenHeight,
        int left, int top, int width, int height)
    {
        int pos = 0;
        for (int row = 0; row < height && pos + 2 <= src.Length; row++)
        {
            int lineLength = src[pos] | (src[pos + 1] << 8);
            pos += 2;
            int lineEnd = Math.Min(src.Length, pos + lineLength);
            int y = top + row;
            int x = 0;
            while (x < width && pos < lineEnd)
            {
                byte ctrl = src[pos++];
                int count = (ctrl >> 1) + 1;
                bool run = (ctrl & 1) != 0;
                byte colour = 0;
                if (run)
                {
                    if (pos >= lineEnd) break;
                    colour = src[pos++];
                }
                for (int i = 0; i < count && x < width; i++, x++)
                {
                    byte c = run ? colour : pos < lineEnd ? src[pos++] : (byte)0;
                    int sx = left + x;
                    if (c != 0 && y >= 0 && y < screenHeight && sx >= 0 && sx < screenWidth)
                        screen[y * screenWidth + sx] = c;
                }
            }
            pos = lineEnd;
        }
    }
}
