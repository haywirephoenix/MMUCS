using System;
using Godot;

public static class ScummImageUtils
{
    public static Image CreateGodotImage(
        byte[] pixels,
        int width,
        int height,
        int pitch,
        ReadOnlySpan<byte> palette,
        byte? transparentIndex = null)
    {
        try
        {
            if (pitch <= 0) 
            {
                pitch = width;
            }
            
            byte[] sanitizedPixels;
            if (pitch == width)
            {
                sanitizedPixels = pixels;
            }
            else
            {
                sanitizedPixels = new byte[width * height];
                for (int y = 0; y < height; y++)
                {
                    Buffer.BlockCopy(
                        pixels,
                        y * pitch,
                        sanitizedPixels,
                        y * width,
                        width);
                }
            }
            
            byte[] rgbaData = new byte[width * height * 4];
            
            int dst = 0;
            int totalPixels = width * height;

            for (int i = 0; i < totalPixels; i++)
            {
                byte paletteIndex = sanitizedPixels[i];

                if (transparentIndex.HasValue && paletteIndex == transparentIndex.Value)
                {
                    rgbaData[dst++] = 0; // R
                    rgbaData[dst++] = 0; // G
                    rgbaData[dst++] = 0; // B
                    rgbaData[dst++] = 0; // A (Fully Transparent)
                    continue;
                }

                int palOffset = paletteIndex * 3;

                if (palOffset + 2 >= palette.Length)
                {
                    rgbaData[dst++] = 0; 
                    rgbaData[dst++] = 0; 
                    rgbaData[dst++] = 0; 
                    rgbaData[dst++] = 0; // Fallback to transparent black on overflow
                    continue;
                }

                rgbaData[dst++] = palette[palOffset + 0]; // R
                rgbaData[dst++] = palette[palOffset + 1]; // G
                rgbaData[dst++] = palette[palOffset + 2]; // B
                rgbaData[dst++] = 255;                    // A (Opaque)
            }

            return Image.CreateFromData(
                width,
                height,
                false,
                Image.Format.Rgba8,
                rgbaData);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ImageLoader] Failed to process indexed image data: {ex.Message}");
            return null;
        }
    }
    
    /*
    public static Image CreateGodotImageIndexed(
        byte[] indexed,
        int width,
        int height,
        ReadOnlySpan<byte> palette,
        byte transparentIndex = 255)
    {
        byte[] rgba = new byte[width * height * 4];

        int src = 0;
        int dst = 0;

        for (int i = 0; i < width * height; i++)
        {
            byte idx = indexed[src++];

            if (idx == transparentIndex)
            {
                rgba[dst++] = 0;
                rgba[dst++] = 0;
                rgba[dst++] = 0;
                rgba[dst++] = 0;
                continue;
            }

            int pal = idx * 3;

            rgba[dst++] = palette[pal + 0];
            rgba[dst++] = palette[pal + 1];
            rgba[dst++] = palette[pal + 2];
            rgba[dst++] = 255;
        }

        return Image.CreateFromData(
            width,
            height,
            false,
            Image.Format.Rgba8,
            rgba);
    }

    public static Image CreateGodotImage(byte[] pixels, int width, int height, int pitch, ReadOnlySpan<byte> apal)
    {
        // 1. Create the base L8 image (Indexed/Grayscale)
        // We use pitch to ensure we only capture the actual pixel data if the buffer is padded
        byte[] sanitizedPixels = pixels;

        if (pitch != width)
        {
            sanitizedPixels = new byte[width * height];
            for (int y = 0; y < height; y++)
            {
                Array.Copy(pixels, y * pitch, sanitizedPixels, y * width, width);
            }
        }

        // Map the 8-bit indices to actual RGBA colors using the palette
        // SCUMM palettes are usually 768 bytes (256 * RGB)
        byte[] rgbaData = new byte[width * height * 4];

        for (int i = 0; i < width * height; i++)
        {
            int paletteIndex = sanitizedPixels[i] * 3;
            int rgbaIndex = i * 4;

            if (paletteIndex + 2 < apal.Length)
            {
                rgbaData[rgbaIndex + 0] = apal[paletteIndex + 0]; // R
                rgbaData[rgbaIndex + 1] = apal[paletteIndex + 1]; // G
                rgbaData[rgbaIndex + 2] = apal[paletteIndex + 2]; // B
                rgbaData[rgbaIndex + 3] = 255; // A (Opaque)
            }
        }

        var image = Image.CreateFromData(width, height, false, Image.Format.Rgba8, rgbaData);
        return image;
    }
    
    
    public static Image CreateGodotImage2(
        byte[] pixels,
        int width,
        int height,
        int pitch,
        ReadOnlySpan<byte> apal,
        byte transparentIndex = 0)
    {
        byte[] sanitizedPixels = pixels;

        if (pitch != width)
        {
            sanitizedPixels = new byte[width * height];

            for (int y = 0; y < height; y++)
            {
                Array.Copy(
                    pixels,
                    y * pitch,
                    sanitizedPixels,
                    y * width,
                    width);
            }
        }

        byte[] rgbaData = new byte[width * height * 4];

        for (int i = 0; i < width * height; i++)
        {
            byte paletteIndex = sanitizedPixels[i];

            int rgbaIndex = i * 4;

            // COMI convention:
            // palette index transparentIndex = transparent
            if (paletteIndex == transparentIndex)
            {
                rgbaData[rgbaIndex + 3] = 0;
                continue;
            }

            int pal = paletteIndex * 3;

            if (pal + 2 >= apal.Length)
            {
                rgbaData[rgbaIndex + 3] = 0;
                continue;
            }

            rgbaData[rgbaIndex + 0] = apal[pal + 0];
            rgbaData[rgbaIndex + 1] = apal[pal + 1];
            rgbaData[rgbaIndex + 2] = apal[pal + 2];
            rgbaData[rgbaIndex + 3] = 255;
        }

        return Image.CreateFromData(
            width,
            height,
            false,
            Image.Format.Rgba8,
            rgbaData);
    }
    
     public static Image CreateBGImageFromIndexed(
        byte[] indexedPixels,
        int width,
        int height,
        int pitch,
        ReadOnlySpan<byte> palette)
    {
        try
        {
            // Remove pitch padding if needed
            byte[] sanitizedPixels;

            if (pitch == width)
            {
                sanitizedPixels = indexedPixels;
            }
            else
            {
                sanitizedPixels = new byte[width * height];

                for (int y = 0; y < height; y++)
                {
                    Buffer.BlockCopy(
                        indexedPixels,
                        y * pitch,
                        sanitizedPixels,
                        y * width,
                        width);
                }
            }

            byte[] rgba = new byte[width * height * 4];

            for (int i = 0; i < sanitizedPixels.Length; i++)
            {
                int palIndex = sanitizedPixels[i] * 3;
                int outIndex = i * 4;

                if (palIndex + 2 >= palette.Length)
                    continue;

                rgba[outIndex + 0] = palette[palIndex + 0];
                rgba[outIndex + 1] = palette[palIndex + 1];
                rgba[outIndex + 2] = palette[palIndex + 2];
                rgba[outIndex + 3] = 255;
            }

            return Image.CreateFromData(
                width,
                height,
                false,
                Image.Format.Rgba8,
                rgba);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"Failed to create image from indexed data: {ex}");
            return null;
        }
    }
    
    
    */
}