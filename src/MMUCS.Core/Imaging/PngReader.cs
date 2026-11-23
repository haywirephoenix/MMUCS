using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace MMUCS.Core.Imaging;

/// <summary>
/// A decoded PNG: either indexed (<see cref="Indices"/> with <see cref="Palette"/> and its alpha) or
/// full colour (<see cref="Argb"/>, straight alpha).
/// </summary>
public sealed class PngImage
{
    public int Width { get; init; }
    public int Height { get; init; }
    public byte[] Indices { get; init; }
    /// <summary>The palette as 0xAARRGGBB (alpha from tRNS), for indexed images.</summary>
    public uint[] Palette { get; init; }
    public uint[] Argb { get; init; }
    public bool IsIndexed => Indices != null;

    /// <summary>Every pixel as 0xAARRGGBB, whatever the image type.</summary>
    public uint[] ToArgb()
    {
        if (!IsIndexed) return Argb;
        var result = new uint[Indices.Length];
        for (int i = 0; i < result.Length; i++)
            result[i] = Indices[i] < Palette.Length ? Palette[Indices[i]] : 0;
        return result;
    }
}

/// <summary>
/// A small dependency-free PNG decoder, enough for what painters' tools write: greyscale, RGB, indexed,
/// greyscale + alpha and RGBA at 1–16 bits, any filter, not interlaced. Indexed images stay indexed.
/// </summary>
public static class PngReader
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static PngImage Load(string path) => Read(File.ReadAllBytes(path));

    public static PngImage Read(byte[] file)
    {
        if (file.Length < 8 || !file.AsSpan(0, 8).SequenceEqual(Signature)) throw new InvalidDataException("not a PNG file");
        int width = 0, height = 0, depth = 0, type = 0, interlace = 0;
        byte[] plte = null, trns = null;
        using var idat = new MemoryStream();
        int pos = 8;
        while (pos + 8 <= file.Length)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(pos));
            string tag = Encoding.ASCII.GetString(file, pos + 4, 4);
            if (len < 0 || pos + 12 + len > file.Length) throw new InvalidDataException($"PNG chunk {tag} runs past the end");
            var data = file.AsSpan(pos + 8, len);
            switch (tag)
            {
                case "IHDR":
                    width = BinaryPrimitives.ReadInt32BigEndian(data);
                    height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                    depth = data[8]; type = data[9]; interlace = data[12];
                    break;
                case "PLTE": plte = data.ToArray(); break;
                case "tRNS": trns = data.ToArray(); break;
                case "IDAT": idat.Write(data); break;
            }
            pos += 12 + len;
            if (tag == "IEND") break;
        }
        if (width <= 0 || height <= 0) throw new InvalidDataException("PNG has no IHDR");
        if (interlace != 0) throw new InvalidDataException("interlaced PNGs aren't supported; save it without interlacing");

        int channels = type switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new InvalidDataException($"PNG colour type {type}") };
        int bitsPerPixel = channels * depth;
        int stride = (width * bitsPerPixel + 7) / 8;
        int bpp = Math.Max(1, bitsPerPixel / 8);
        var raw = new byte[stride * height];
        idat.Position = 0;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
        {
            var prev = new byte[stride];
            var line = new byte[stride];
            for (int y = 0; y < height; y++)
            {
                int filter = z.ReadByte();
                z.ReadExactly(line);
                Unfilter(filter, line, prev, bpp);
                line.CopyTo(raw, y * stride);
                (prev, line) = (line, prev);
            }
        }

        int Sample(int y, int index)
        {
            // The index-th sample of row y, scaled to 8 bits unless it's a palette index.
            int bit = index * depth;
            int o = y * stride + bit / 8;
            int v = depth switch
            {
                16 => raw[o],
                8 => raw[o],
                _ => (raw[o] >> (8 - depth - bit % 8)) & ((1 << depth) - 1),
            };
            if (type == 3 || depth >= 8) return v;
            return v * 255 / ((1 << depth) - 1);
        }

        if (type == 3)
        {
            var indices = new byte[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    indices[y * width + x] = (byte)Sample(y, x);
            var palette = new uint[256];
            for (int i = 0; plte != null && i * 3 + 2 < plte.Length && i < 256; i++)
            {
                uint a = trns != null && i < trns.Length ? trns[i] : 255u;
                palette[i] = (a << 24) | ((uint)plte[i * 3] << 16) | ((uint)plte[i * 3 + 1] << 8) | plte[i * 3 + 2];
            }
            return new PngImage { Width = width, Height = height, Indices = indices, Palette = palette };
        }

        // A tRNS chunk on greyscale/RGB names one colour (as 16-bit samples) that is transparent.
        int keyR = -1, keyG = -1, keyB = -1;
        if (trns != null && type == 0 && trns.Length >= 2) keyR = keyG = keyB = Key(trns, 0, depth);
        if (trns != null && type == 2 && trns.Length >= 6) { keyR = Key(trns, 0, depth); keyG = Key(trns, 2, depth); keyB = Key(trns, 4, depth); }

        var argb = new uint[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = x * channels;
                int r, g, b, a = 255;
                if (channels <= 2) { r = g = b = Sample(y, i); if (channels == 2) a = Sample(y, i + 1); }
                else { r = Sample(y, i); g = Sample(y, i + 1); b = Sample(y, i + 2); if (channels == 4) a = Sample(y, i + 3); }
                if (r == keyR && g == keyG && b == keyB) a = 0;
                argb[y * width + x] = ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
            }
        }
        return new PngImage { Width = width, Height = height, Argb = argb };
    }

    private static int Key(byte[] trns, int o, int depth)
    {
        int v = (trns[o] << 8) | trns[o + 1];
        return depth == 16 ? v >> 8 : depth == 8 ? v : v * 255 / ((1 << depth) - 1);
    }

    private static void Unfilter(int filter, byte[] line, byte[] prev, int bpp)
    {
        for (int i = 0; i < line.Length; i++)
        {
            int a = i >= bpp ? line[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
            line[i] = filter switch
            {
                0 => line[i],
                1 => (byte)(line[i] + a),
                2 => (byte)(line[i] + b),
                3 => (byte)(line[i] + ((a + b) >> 1)),
                4 => (byte)(line[i] + Paeth(a, b, c)),
                _ => throw new InvalidDataException($"PNG filter {filter}"),
            };
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
