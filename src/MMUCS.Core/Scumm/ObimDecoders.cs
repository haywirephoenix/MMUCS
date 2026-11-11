using System;
using System.Threading;
using MMUCS.Core.Diagnostics;
using MMUCS.Core.Imaging;

namespace MMUCS.Core.Scumm;

public static class ObimDecoders
{
    public readonly struct ObimResult(IndexedSurface surface, ScummColor[] resolvedPalette, RgbaImage executedImage = null)
    {
        public IndexedSurface Surface { get; } = surface;
        public ScummColor[] ResolvedPalette { get; } = resolvedPalette;
        public RgbaImage ExecutedImage { get; } = executedImage;
    }
    public readonly struct ObimDecodeResult(RgbaImage image, byte[] indexedPixels, int pitch, ScummColor[] resolvedPalette)
    {
        public readonly RgbaImage RgbaImage = image;
        public readonly byte[] IndexedPixels = indexedPixels;
        public readonly int Pitch = pitch;
        public readonly ScummColor[] ResolvedPalette = resolvedPalette;
    }
    public readonly struct ObimDecodeRawResult(int width, int height, int pitch, byte[] pixels)
    {
        public int Width { get; } = width;
        public int Height { get; } = height;
        public int Pitch { get; } = pitch;
        public byte[] IndexedPixels { get; } = pixels;
    }
    
    public readonly struct ObimInfo(int totalFrames, int totalPalettes, int defaultPaletteIndex)
    {
        public readonly int TotalFrames = totalFrames;
        public readonly int TotalPalettes = totalPalettes;
        public readonly int DefaultPaletteIndex = defaultPaletteIndex;
    }
    
    public static bool TryGetObimInfo(ScummBlock obimBlock, out ObimDecoders.ObimInfo info)
    {
        info = default;

        var imhd = obimBlock.FindChild(ScummTag.IMHD);
        if (imhd == null) return false;

        var palsBlock = obimBlock.Parent?.FindChild(ScummTag.PALS);
        var wrapBlock = palsBlock?.FindChild(ScummTag.WRAP);
        var apals = wrapBlock?.FindChildrenRecursive(ScummTag.APAL);
        int totalPalettes = apals?.Count ?? 0;

        var imag = obimBlock.FindChild(ScummTag.IMAG);
        int totalFrames = 0;
        if (imag != null)
        {
            foreach (var child in imag.FindChildrenRecursive(ScummTag.BOMP))
                if (child.Tag == ScummTag.BOMP) totalFrames++;
        }

        int width = (int)imhd.DataSpan.U32LE(56);
        int defaultPaletteIndex = width == 640 ? 1 : 0;
        defaultPaletteIndex = Math.Clamp(defaultPaletteIndex, 0, Math.Max(0, totalPalettes - 1));

        info = new ObimDecoders.ObimInfo(totalFrames, totalPalettes, defaultPaletteIndex);
        return true;
    }
    
    public static bool TryDecodeObimFrame(
        ScummBlock objectBlock,
        CancellationToken token,
        int frameIndex,
        int paletteIndex,
        out ObimDecodeRawResult result,
        out RgbaImage bakedImage,
        bool generateImage = false)
    {
        result = default;
        bakedImage = null;

        var imhd = objectBlock.FindChild(ScummTag.IMHD);
        if (imhd == null) return false;

        var room = objectBlock.FindParent(ScummTag.ROOM);
        var s = imhd.DataSpan;
        int width  = (int)s.U32LE(56);
        int height = (int)s.U32LE(60);
        if (width <= 0 || height <= 0) return false;

        var imag = objectBlock.FindChild(ScummTag.IMAG);
        if (imag == null) return false;

        ScummBlock smap = imag.FindChildRecursive(ScummTag.SMAP);
        ScummBlock bomp = null;

        if (smap == null)
        {
            foreach (var child in imag.FindChildrenRecursive(ScummTag.BOMP))
            {
                if (child.Tag == ScummTag.BOMP && child.TagSiblingIndex == frameIndex)
                {
                    bomp = child;
                    break;
                }
            }
        }

        byte[] buffer;
        int pitch;

        if (smap != null)
        {
            if (!ScummDecoders.TryDecodeSmapBlock(smap, width, height, token, out buffer, out pitch))
                return false;
        }
        else if (bomp != null)
        {
            buffer = DecodeBomp(bomp.FullSpan, width, height);
            pitch = width.Align8();
        }
        else return false;

        result = new ObimDecodeRawResult(width, height, pitch, buffer);

        if (generateImage)
        {
            var (paletteBytes, _) = ObimCache.ExtractPaletteContext(room, paletteIndex);
            int transparentIndex = ObimCache.GetTransparentIndex(room, objectBlock, frameIndex);
            bakedImage = ScummImageUtils.CreateImage(buffer, width, height, pitch, paletteBytes, (byte)transparentIndex);
        }

        return true;
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