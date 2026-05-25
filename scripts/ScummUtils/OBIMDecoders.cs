using System;
using Godot;
public static class OBIMDecoders
{
    public readonly struct ObimDecodeResult(Image image, byte[] indexedPixels, int pitch, Color[] resolvedPalette)
    {
        public readonly Image Image = image;
        public readonly byte[] IndexedPixels = indexedPixels;
        public readonly int Pitch = pitch;
        public readonly Color[] ResolvedPalette = resolvedPalette;
    }
    
    public readonly struct ObimInfo(int totalFrames, int totalPalettes, int defaultPaletteIndex)
    {
        public readonly int TotalFrames = totalFrames;
        public readonly int TotalPalettes = totalPalettes;
        public readonly int DefaultPaletteIndex = defaultPaletteIndex;

    }
    
    public static byte[] DecodeBomp(
        ReadOnlySpan<byte> data,
        int width,
        int height)
    {
        byte[] dst = new byte[width * height];

        // V8 header skip
        int srcPos = 16;

        for (int y = 0; y < height; y++)
        {
            if (srcPos + 2 > data.Length)
                break;

            int rowSize = data.U16LE(srcPos);
            srcPos += 2;

            if (rowSize <= 0 || srcPos + rowSize > data.Length)
                break;

            DecodeBompRow(
                data.Slice(srcPos, rowSize),
                dst,
                y * width,
                width);

            srcPos += rowSize;
        }

        return dst;
    }
    
    public static void DecodeBompRow(
        ReadOnlySpan<byte> src,
        byte[] dst,
        int dstPos,
        int width)
    {
        int x = 0;
        int pos = 0;

        while (x < width && pos < src.Length)
        {
            byte code = src[pos++];

            int count = (code >> 1) + 1;

            if (count > width - x)
                count = width - x;

            if ((code & 1) != 0)
            {
                // solid run
                byte color = src[pos++];

                for (int i = 0; i < count; i++)
                    dst[dstPos + x++] = color;
            }
            else
            {
                // literal run
                for (int i = 0; i < count; i++)
                    dst[dstPos + x++] = src[pos++];
            }
        }
    }
    
    
}