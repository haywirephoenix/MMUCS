using System;
using Godot;

public static class IndexedRenderer
{
    public static Image CreateImage(IndexedSurface surface, AkosData akos)
    {
        if (surface == null || akos.ResolvedColors == null) return null;

        int width = surface.Width;
        int height = surface.Height;
        byte[] rgba = new byte[width * height * 4];
    
        ReadOnlySpan<byte> akplMapping = akos.Palette.Span;
        Color[] palette = akos.ResolvedColors;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte localIdx = surface.Pixels[y * surface.Pitch + x];
                int dstIndex = (y * width + x) * 4;

                // Local to Global
                int globalIdx = (localIdx < akplMapping.Length) ? akplMapping[localIdx] : localIdx;

                // Final color from palette
                Color c;
                if (globalIdx < palette.Length)
                    c = palette[globalIdx];
                else
                    c = new Color(1, 0, 1, 1); // Fallback Magenta
                
                // no transparency for index 0 (solid black)
                // no special black coloring for shadows (1-7)
                rgba[dstIndex + 0] = (byte)(c.R * 255);
                rgba[dstIndex + 1] = (byte)(c.G * 255);
                rgba[dstIndex + 2] = (byte)(c.B * 255);
                rgba[dstIndex + 3] = 255; // Force Alpha to 100% for everything
            }
        }

        return Image.CreateFromData(width, height, false, Image.Format.Rgba8, rgba);
    }
    
    public static Image CreateImage_Processed(IndexedSurface surface, AkosData akos)
    {
        if (surface == null || akos.ResolvedColors == null) return null;

        int width = surface.Width;
        int height = surface.Height;
        byte[] rgba = new byte[width * height * 4];
        
        // mapping table (Local Index -> Global Index)
        ReadOnlySpan<byte> akplMapping = akos.Palette.Span;
        // actual colors (Global Index -> RGBA)
        Color[] palette = akos.ResolvedColors;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte localIdx = surface.Pixels[y * surface.Pitch + x];

                // In SCUMM, local index 0 is almost always hard-coded transparency
                if (localIdx == 0) continue; 

                int dstIndex = (y * width + x) * 4;

                // Translate Local to Global using the AKPL table
                // If localIdx is beyond the mapping table, we use it as-is (common in some versions)
                int globalIdx = (localIdx < akplMapping.Length) ? akplMapping[localIdx] : localIdx;

                // Determine final color
                Color c;
                if (globalIdx < palette.Length)
                {
                    c = palette[globalIdx];
                }
                else
                {
                    // Magenta = globalIdx is OOB of ResolvedColors array.
                    c = new Color(1, 0, 1, 1); 
                }

                // Shadow Semantic (Global indices 1-7)
                if (globalIdx > 0 && globalIdx < 8)
                {
                    // shadow
                    rgba[dstIndex + 0] = 0;
                    rgba[dstIndex + 1] = 0;
                    rgba[dstIndex + 2] = 0;
                    rgba[dstIndex + 3] = (byte)(0.4f * 255);
                }
                else
                {
                    rgba[dstIndex + 0] = (byte)(c.R * 255);
                    rgba[dstIndex + 1] = (byte)(c.G * 255);
                    rgba[dstIndex + 2] = (byte)(c.B * 255);
                    rgba[dstIndex + 3] = (byte)(c.A * 255);
                }
            }
        }

        return Image.CreateFromData(width, height, false, Image.Format.Rgba8, rgba);
    }

    public static Image CreateDataImage(IndexedSurface surface, AkosData akos)
    {
        if (surface == null) return null;

        var image = Image.CreateEmpty(surface.Width, surface.Height, false, Image.Format.Rgba8);

        bool isPassthrough  = akos.IsPassthroughPalette;
        byte transparentIdx = akos.TransparentIndex;          // 255 passthrough, 0 local
        byte[] akplMapping  = akos.Palette.Span.ToArray();    // only used in local-index path

        for (int y = 0; y < surface.Height; y++)
        {
            for (int x = 0; x < surface.Width; x++)
            {
                byte localIdx = surface.Pixels[y * surface.Pitch + x];

                if (localIdx == transparentIdx)
                {
                    image.SetPixel(x, y, new Color(0, 0, 0, 0));
                    continue;
                }

                byte globalIdx;

                if (isPassthrough)
                {
                    // AKCD bytes ARE room palette indices — no remapping needed
                    globalIdx = localIdx;
                }
                else
                {
                    // Local index → AKPL → room palette index
                    globalIdx = (localIdx < akplMapping.Length) ? akplMapping[localIdx] : localIdx;
                }

                float globalIdxNormalized = globalIdx / 255.0f;
                float isShadow            = (globalIdx > 0 && globalIdx < 8) ? 1.0f : 0.0f;
                float blueVisualization   = (globalIdxNormalized % 32) / 32.0f;

                image.SetPixel(x, y, new Color(globalIdxNormalized, isShadow, blueVisualization, 1.0f));
            }
        }

        return image;
    }
}