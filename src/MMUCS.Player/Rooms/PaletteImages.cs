using System;
using MMUCS.Core.Diagnostics;
using MMUCS.Core.Imaging;
using MMUCS.Core.Scumm;
using SkiaSharp;

namespace MMUCS.Player.Rooms;

/// <summary>
/// Pictures kept in palette indices and coloured as they're drawn, as the engine shows everything through its
/// one palette: an index image (red the index, alpha where it's drawn) is looked up in a 256×1 palette strip by a
/// shader. The index images are made once; when the scripts change the colours (fades, lights, a flickering fire)
/// only the strip is new, so nothing is coloured again on the CPU or uploaded to the GPU again.
/// </summary>
public static class PaletteImages
{
    private const string Sksl = @"
uniform shader indices;
uniform shader palette;
uniform float2 size;
uniform float smooth;

half4 at(float2 ip) {
    ip = clamp(ip, float2(0.0), size - 1.0);
    half4 c = indices.eval(ip + 0.5);
    float i = floor(c.r / max(c.a, 0.0001) * 255.0 + 0.5);
    return half4(palette.eval(float2(i + 0.5, 0.5)).rgb * c.a, c.a);
}

half4 main(float2 p) {
    if (smooth < 0.5) return at(floor(p));
    float2 sp = p - 0.5;
    float2 i = floor(sp);
    half2 f = half2(fract(sp));
    return mix(mix(at(i), at(i + float2(1.0, 0.0)), f.x), mix(at(i + float2(0.0, 1.0)), at(i + float2(1.0, 1.0)), f.x), f.y);
}";

    private static readonly Lazy<SKRuntimeEffect> Effect = new(() =>
    {
        var effect = SKRuntimeEffect.CreateShader(Sksl, out var errors);
        if (effect == null) Log.Error($"Palette SkSL compile failed: {errors}");
        return effect;
    });

    /// <summary>Whether a canvas without a GPU draws coloured copies rather than running the shader (tests turn it off to try the shader).</summary>
    public static bool ColourOnCpu { get; set; } = true;

    private static readonly SKSamplingOptions Nearest = new(SKFilterMode.Nearest, SKMipmapMode.None);

    /// <summary>
    /// The "colours" that make a costume renderer write indices: index i's red is i, so a frame drawn in them
    /// (<see cref="RoomSceneBuilder.CostumeFrame"/>'s screen) is an index image.
    /// </summary>
    public static byte[] IndexScreen { get; } = MakeIndexScreen();

    private static byte[] MakeIndexScreen()
    {
        var screen = new byte[256 * 3];
        for (int i = 0; i < 256; i++) screen[i * 3] = (byte)i;
        return screen;
    }

    /// <summary>A palette (256 RGB triples) as the strip <see cref="Draw"/> looks indices up in.</summary>
    public static SKImage Strip(byte[] palette) => HiResTint.Strip(palette);

    /// <summary>A surface as an index image; <paramref name="transparent"/> (an index, or -1) is left out.</summary>
    public static SKImage Indices(IndexedSurface surface, int transparent = -1)
    {
        int w = surface.Width, h = surface.Height;
        if (w <= 0 || h <= 0) return null;
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0, s = y * surface.Pitch, o = y * w * 4; x < w; x++, o += 4)
            {
                byte i = surface.Pixels[s + x];
                if (i == transparent) continue;
                rgba[o] = i;
                rgba[o + 3] = 255;
            }
        return SKImage.FromPixelCopy(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul), rgba, w * 4);
    }

    /// <summary>
    /// Draws <paramref name="indices"/> over <paramref name="dest"/> in <paramref name="palette"/>'s colours: each
    /// game pixel whole, or blended with its neighbours when <paramref name="sampling"/> filters (a window
    /// drawn smaller than the game). On a canvas without a GPU (software rendering, a save's thumbnail) the
    /// shader would run pixel by pixel, many times slower than an image: there the index image is coloured
    /// once for each palette it's drawn in, and that image drawn. Falls back to the index image itself if the
    /// shader isn't available.
    /// </summary>
    public static void Draw(SKCanvas canvas, SKImage indices, SKImage palette, SKRect dest, SKSamplingOptions sampling, SKPaint paint = null)
    {
        if (indices == null) return;
        if (palette != null && ColourOnCpu && canvas.Context == null && Coloured(indices, palette) is { } coloured)
        {
            canvas.DrawImage(coloured, dest, sampling, paint);
            return;
        }
        if (Effect.Value is not { } effect || palette == null)
        {
            canvas.DrawImage(indices, dest, sampling, paint);
            return;
        }
        // Not disposed: disposing a builder disposes its effect, which is shared. What it made is freed here.
        var builder = new SKRuntimeShaderBuilder(effect);
        using var indexShader = indices.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, Nearest);
        using var paletteShader = palette.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, Nearest);
        builder.Children["indices"] = indexShader;
        builder.Children["palette"] = paletteShader;
        builder.Uniforms["size"] = new[] { (float)indices.Width, indices.Height };
        builder.Uniforms["smooth"] = sampling.Filter == SKFilterMode.Linear ? 1f : 0f;
        using var shader = builder.Build();
        // The shader works in the index image's pixels; the canvas puts them over dest.
        canvas.Save();
        canvas.Translate(dest.Left, dest.Top);
        canvas.Scale(dest.Width / indices.Width, dest.Height / indices.Height);
        using var draw = paint?.Clone() ?? new SKPaint();
        draw.Shader = shader;
        canvas.DrawRect(new SKRect(0, 0, indices.Width, indices.Height), draw);
        canvas.Restore();
    }

    /// <summary>An index image coloured in the palette it was last drawn in on the CPU, kept while the index image lives.</summary>
    private sealed class Colouring
    {
        public SKImage Palette, Image;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SKImage, Colouring> Colourings = [];

    /// <summary>
    /// <paramref name="indices"/> in <paramref name="palette"/>'s colours, made again only when the palette is a new
    /// one (the image replaced is left to the finalizer: another thread may be drawing it), or null if either
    /// isn't in memory.
    /// </summary>
    private static SKImage Coloured(SKImage indices, SKImage palette)
    {
        var colouring = Colourings.GetOrCreateValue(indices);
        lock (colouring)
        {
            if (ReferenceEquals(colouring.Palette, palette)) return colouring.Image;
            using var source = indices.PeekPixels();
            using var strip = palette.PeekPixels();
            if (source == null || strip == null || source.ColorType != SKColorType.Rgba8888 || strip.ColorType != SKColorType.Rgba8888) return null;
            var from = source.GetPixelSpan();
            var colours = strip.GetPixelSpan();
            int w = indices.Width, h = indices.Height;
            var rgba = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                for (int x = 0, s = y * source.RowBytes, o = y * w * 4; x < w; x++, s += 4, o += 4)
                {
                    int a = from[s + 3];
                    if (a == 0) continue;
                    int c = Math.Min(255, from[s] * 255 / a) * 4;
                    rgba[o] = (byte)(colours[c] * a / 255);
                    rgba[o + 1] = (byte)(colours[c + 1] * a / 255);
                    rgba[o + 2] = (byte)(colours[c + 2] * a / 255);
                    rgba[o + 3] = (byte)a;
                }
            colouring.Image = SKImage.FromPixelCopy(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul), rgba, w * 4);
            colouring.Palette = palette;
            return colouring.Image;
        }
    }
}
