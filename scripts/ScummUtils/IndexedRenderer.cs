using System;
using Godot;

public static class IndexedRenderer
{
    private enum RenderMode
    {
        Raw,
        Processed,
        DataVisualization
    }

    public static Image CreateImage(IndexedSurface surface, AkosData akos) =>
        RenderInternal(surface, akos, RenderMode.Raw);

    public static Image CreateImage_Processed(IndexedSurface surface, AkosData akos) =>
        RenderInternal(surface, akos, RenderMode.Processed);

    public static Image CreateDataImage(IndexedSurface surface, AkosData akos) =>
        RenderInternal(surface, akos, RenderMode.DataVisualization);

    private static Image RenderInternal(IndexedSurface surface, AkosData akos, RenderMode mode)
    {
        if (surface == null || (mode != RenderMode.DataVisualization && akos?.ResolvedColors == null)) 
            return null;

        int width = surface.Width;
        int height = surface.Height;
        byte[] rgba = new byte[width * height * 4];
    
        ReadOnlySpan<byte> akplMapping = akos.Palette.Span;
        Color[] palette = akos.ResolvedColors;
        
        bool isPassthrough = akos.IsPassthroughPalette;
        byte transparentIdx = akos.TransparentIndex; // Default to 0 for SCUMM processed

        for (int y = 0; y < height; y++)
        {
            int rowOffset = y * surface.Pitch;
            int dstRowOffset = y * width * 4;

            for (int x = 0; x < width; x++)
            {
                byte localIdx = surface.Pixels[rowOffset + x];
                int dstIndex = dstRowOffset + (x * 4);
                
                if (mode == RenderMode.Processed && localIdx == 0) continue; // Leaves alpha at 0
                if (mode == RenderMode.DataVisualization && localIdx == transparentIdx) continue;

                int globalIdx;
                if (mode == RenderMode.DataVisualization && isPassthrough)
                {
                    globalIdx = localIdx;
                }
                else
                {
                    globalIdx = (localIdx < akplMapping.Length) ? akplMapping[localIdx] : localIdx;
                }

                if (mode == RenderMode.DataVisualization)
                {
                    float globalIdxNormalized = globalIdx / 255.0f;
                    float isShadow = (globalIdx > 0 && globalIdx < 8) ? 1.0f : 0.0f;
                    float blueVisualization = (globalIdxNormalized % 32) / 32.0f;

                    rgba[dstIndex + 0] = (byte)(globalIdxNormalized * 255);
                    rgba[dstIndex + 1] = (byte)(isShadow * 255);
                    rgba[dstIndex + 2] = (byte)(blueVisualization * 255);
                    rgba[dstIndex + 3] = 255;
                }
                else // Raw or Processed
                {
                    Color c = (globalIdx < palette.Length) ? palette[globalIdx] : new Color(1, 0, 1, 1);

                    if (mode == RenderMode.Processed && globalIdx > 0 && globalIdx < 8)
                    {
                        // Processed Shadow Semantic
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
                        rgba[dstIndex + 3] = (mode == RenderMode.Raw) ? (byte)255 : (byte)(c.A * 255);
                    }
                }
            }
        }

        return Image.CreateFromData(width, height, false, Image.Format.Rgba8, rgba);
    }
}