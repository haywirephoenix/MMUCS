using System;
using System.Collections.Generic;
using System.IO;
using MMUCS.Core.Diagnostics;
using MMUCS.Core.Smush;

namespace MMUCS.Core.Fonts;

/// <summary>
/// Reads the game's NUT fonts (<c>resource/font0.nut</c> … <c>font4.nut</c>). A NUT file is a SMUSH
/// animation with one frame per character: <c>ANIM</c> &gt; <c>AHDR</c> (its frame count is the glyph
/// count, 256 in COMI) &gt; <c>FRME</c> &gt; <c>FOBJ</c>, whose header gives the codec, the offset and the
/// glyph's size.
/// <list type="bullet">
/// <item>Codecs 21 and 44 (all of COMI's fonts): a line-skip RLE, see <see cref="DecodeLines"/>.</item>
/// <item>Codec 1: SMUSH's row RLE (older games' fonts).</item>
/// </list>
/// Codec 44 glyphs leave unset pixels as value 2, codec 21 and 1 as 0: that value is the glyph's
/// <see cref="Glyph.Transparent"/>. font3.nut's frames say codec 44 but are laid out the codec 21 way
/// (see <see cref="DecodeLines"/>), which decodes the same.
/// </summary>
public static class NutFont
{
    public const byte Codec44Transparent = 2;
    public const byte DefaultTransparent = 0;

    public static GlyphFont Load(string path)
    {
        return Parse(File.ReadAllBytes(path), Path.GetFileNameWithoutExtension(path).ToLowerInvariant());
    }

    /// <summary>The font number in a file name (<c>font3.nut</c> → 3), or -1.</summary>
    public static int NumberOf(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        int i = name.Length;
        while (i > 0 && char.IsAsciiDigit(name[i - 1])) i--;
        return i < name.Length && int.TryParse(name[i..], out int n) ? n : -1;
    }

    /// <summary>The game's fonts by number (font0.nut is [0]); a missing number leaves a null.</summary>
    public static GlyphFont[] LoadAll(string gameFile)
    {
        var found = new List<(int, string)>();
        foreach (var path in GameFiles.Find(gameFile, ".nut"))
            if (NumberOf(path) is >= 0 and < 100 and var n) found.Add((n, path));
        int count = 0;
        foreach (var (n, _) in found) count = Math.Max(count, n + 1);
        var fonts = new GlyphFont[count];
        foreach (var (n, path) in found)
        {
            try { fonts[n] = Load(path); }
            catch (Exception ex) { Log.Warn($"{Path.GetFileName(path)}: {ex.Message}"); }
        }
        return fonts;
    }

    public static GlyphFont Parse(byte[] data, string name)
    {
        if (!SmushChunk.TryRead(data, 0, data.Length, out var anim) || anim.Tag != SmushTag.ANIM)
            throw new InvalidDataException($"{name} is not a NUT font (no ANIM)");

        int declared = -1;
        var glyphs = new List<Glyph>();
        foreach (var chunk in anim.Children(data))
        {
            if (chunk.Tag == SmushTag.AHDR)
            {
                var h = chunk.Data(data);
                if (h.Length >= 4) declared = h[2] | (h[3] << 8);
            }
            else if (chunk.Tag == SmushTag.FRME)
            {
                glyphs.Add(ReadGlyph(data, chunk, name, glyphs.Count));
            }
        }
        if (declared >= 0 && declared != glyphs.Count)
            Log.Warn($"{name}: the header says {declared} glyphs, the file has {glyphs.Count}");
        return new GlyphFont(name, glyphs);
    }

    private static Glyph ReadGlyph(byte[] data, SmushChunk frame, string name, int index)
    {
        foreach (var obj in frame.Children(data))
        {
            if (obj.Tag != SmushTag.FOBJ || obj.Length < FrameObject.HeaderSize) continue;
            var fobj = obj.Data(data);
            var h = FrameObject.ReadHeader(fobj);
            var payload = fobj[FrameObject.HeaderSize..];
            byte transparent = h.Codec == 44 ? Codec44Transparent : DefaultTransparent;
            var pixels = new byte[h.Width * h.Height];
            pixels.AsSpan().Fill(transparent);
            switch (h.Codec)
            {
                case 21:
                case 44:
                    DecodeLines(payload, pixels, h.Width, h.Height);
                    break;
                case 1:
                    SmushRle.DrawCodec1(payload, pixels, h.Width, h.Height, 0, 0, h.Width, h.Height);
                    break;
                default:
                    Log.Warn($"{name} glyph {index}: codec {h.Codec} isn't supported");
                    break;
            }
            return new Glyph
            {
                Width = h.Width, Height = h.Height, OffsetX = h.Left, OffsetY = h.Top,
                Indices = pixels, Transparent = transparent,
            };
        }
        return Glyph.Empty;
    }

    /// <summary>
    /// Codecs 21 and 44: each row starts with its byte length (u16 LE), then pairs of a skip (u16 LE,
    /// pixels left as they were) and a run of literal bytes (u16 LE count − 1, then the bytes), until the
    /// row is full. A row that ends on a skip has no run after it.
    /// <para>The encoders write height + 1 rows (the last one empty) and let each row run one pixel past
    /// the width: codec 44 adds a 0 byte to the last run, codec 21 a skip of one. Both are dropped here.</para>
    /// </summary>
    public static void DecodeLines(ReadOnlySpan<byte> src, Span<byte> dst, int width, int height)
    {
        int pos = 0;
        for (int row = 0; row < height && pos + 2 <= src.Length; row++)
        {
            int rowEnd = Math.Min(src.Length, pos + 2 + (src[pos] | (src[pos + 1] << 8)));
            pos += 2;
            var line = dst.Slice(row * width, width);
            int x = 0;
            while (x < width && pos + 2 <= rowEnd)
            {
                x += src[pos] | (src[pos + 1] << 8);
                pos += 2;
                if (x >= width || pos + 2 > rowEnd) break;
                int count = (src[pos] | (src[pos + 1] << 8)) + 1;
                pos += 2;
                count = Math.Min(count, rowEnd - pos);
                int keep = Math.Min(count, width - x);
                if (keep > 0) src.Slice(pos, keep).CopyTo(line[x..]);
                x += count;
                pos += count;
            }
            pos = rowEnd;
        }
    }
}
