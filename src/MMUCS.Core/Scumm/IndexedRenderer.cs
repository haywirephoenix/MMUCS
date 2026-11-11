using System;
using MMUCS.Core.Diagnostics;
using MMUCS.Core.Imaging;

namespace MMUCS.Core.Scumm;


public static class IndexedRenderer
{
    private enum RenderMode
    {
        Raw,
        Processed,
        DataVisualization
    }

    public static RgbaImage CreateImage(IndexedSurface surface, AkosData akos) =>
        RenderInternal(surface, akos, RenderMode.Raw);

    public static RgbaImage CreateImage_Processed(IndexedSurface surface, AkosData akos) =>
        RenderInternal(surface, akos, RenderMode.Processed);

    public static RgbaImage CreateDataImage(IndexedSurface surface, AkosData akos) =>
        RenderInternal(surface, akos, RenderMode.DataVisualization);

    private static RgbaImage RenderInternal(IndexedSurface surface, AkosData akos, RenderMode mode)
    {
        if (surface == null || (mode != RenderMode.DataVisualization && akos?.ResolvedColors == null)) 
            return null;

        int width = surface.Width;
        int height = surface.Height;
        byte[] rgba = new byte[width * height * 4];
    
        ReadOnlySpan<byte> akplMapping = akos.Palette.Span;
        ScummColor[] palette = akos.ResolvedColors;
        
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
                    ScummColor c = (globalIdx < palette.Length) ? palette[globalIdx] : new ScummColor(1, 0, 1, 1);

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

        return new RgbaImage(width, height, rgba);
    }
    
    public static RgbaImage RenderWithConfig(RgbaImage shaderDataImage, ScummColor[] roomPalette, CelExportConfig config)
    {
        if (shaderDataImage == null || roomPalette == null) return null;

        int width = shaderDataImage.GetWidth();
        int height = shaderDataImage.GetHeight();
        
        byte[] srcRgba = shaderDataImage.GetData();
        byte[] dstRgba = new byte[width * height * 4];

        for (int y = 0; y < height; y++)
        {
            int dstRowOffset = y * width * 4;

            for (int x = 0; x < width; x++)
            {
                ScummColor finalPixelColor;

                if (config.UseLinearFilter)
                {
                    float sampleX = x - 0.5f;
                    float sampleY = y - 0.5f;

                    int ix = (int)MathF.Floor(sampleX);
                    int iy = (int)MathF.Floor(sampleY);

                    float fx = sampleX - ix;
                    float fy = sampleY - iy;

                    ScummColor c00 = GetDecodedColorAt(ix,     iy);
                    ScummColor c10 = GetDecodedColorAt(ix + 1, iy);
                    ScummColor c01 = GetDecodedColorAt(ix,     iy + 1);
                    ScummColor c11 = GetDecodedColorAt(ix + 1, iy + 1);

                    ScummColor top = c00.Lerp(c10, fx);
                    ScummColor bottom = c01.Lerp(c11, fx);
                    finalPixelColor = top.Lerp(bottom, fy);
                }
                else
                {
                    finalPixelColor = GetDecodedColorAt(x, y);
                }

                int dstIdx = dstRowOffset + (x * 4);
                dstRgba[dstIdx + 0] = (byte)(finalPixelColor.R * 255);
                dstRgba[dstIdx + 1] = (byte)(finalPixelColor.G * 255);
                dstRgba[dstIdx + 2] = (byte)(finalPixelColor.B * 255);
                dstRgba[dstIdx + 3] = (byte)(finalPixelColor.A * 255);
            }
        }

        return new RgbaImage(width, height, dstRgba);

        ScummColor GetDecodedColorAt(int x, int y)
        {
            x = Math.Clamp(x, 0, width - 1);
            y = Math.Clamp(y, 0, height - 1);
            int idx = (y * width + x) * 4;

            byte r = srcRgba[idx + 0];
            byte g = srcRgba[idx + 1];
            byte a = srcRgba[idx + 3];

            if (a < 127)
                return config.AlphaEnabled ? new ScummColor(0, 0, 0, 0) : new ScummColor(0, 0, 0, 1);

            int globalIdx = r;
            bool isShadow = g > 127;

            ScummColor baseColor = (globalIdx < roomPalette.Length) ? roomPalette[globalIdx] : new ScummColor(1, 0, 1, 1);
            ScummColor finalColor = (config.ShadowColorEnabled && isShadow) ? new ScummColor(0, 0, 0) : baseColor;

            float finalAlpha = 1.0f;
            if (config.AlphaEnabled && config.ShadowColorEnabled && isShadow)
                finalAlpha = config.ShadowAmount;
            else if (config.AlphaEnabled)
                finalAlpha = baseColor.A;

            finalColor.A = finalAlpha;
            return finalColor;
        }
    }
    
}