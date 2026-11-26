using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MMUCS.Core.Diagnostics;
using MMUCS.Core.Scumm;
using SkiaSharp;

namespace MMUCS.Player.Rooms;

/// <summary>
/// A hi-res mod image (<see cref="HiResMods"/>) as it's drawn: its picture over <see cref="Width"/>×<see cref="Height"/>
/// game pixels from (<see cref="X"/>,<see cref="Y"/>) in the room, and, when the screen's colours aren't the ones the
/// game's image under it was made in, <see cref="Tinted"/>: the picture through <see cref="HiResTint"/>.
/// </summary>
public sealed record HiResImage(SKImage Image, float X, float Y, float Width, float Height, SKShader Tinted = null)
{
    /// <summary>How a hi-res picture is sampled: smoothly, from its mipmaps when it's drawn smaller.</summary>
    public static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    private static readonly SKPaint Plain = new() { IsAntialias = false };

    public void Draw(SKCanvas canvas)
    {
        var bounds = SKRect.Create(X, Y, Width, Height);
        if (Tinted == null)
        {
            canvas.DrawImage(Image, bounds, Sampling);
            return;
        }
        using var paint = new SKPaint { Shader = Tinted, IsAntialias = false };
        canvas.DrawRect(bounds, paint);
    }
}

/// <summary>
/// The hi-res mods' pictures, decoded once and kept while they're used: a room's background can be 8384×1920, so
/// only the last few are kept (<see cref="MaxBytes"/>). A file is read again when it changes. Thread-safe.
/// </summary>
public static class HiResImages
{
    /// <summary>The most the kept pictures take (their pixels): about six 4× wide rooms.</summary>
    public const long MaxBytes = 512L << 20;

    private static readonly LinkedList<(string File, DateTime Written, SKImage Image)> Kept = [];

    /// <summary>The picture in <paramref name="file"/>, or null when it won't decode.</summary>
    public static SKImage Get(string file)
    {
        DateTime written;
        try { written = File.GetLastWriteTimeUtc(file); }
        catch (IOException) { return null; }
        lock (Kept)
        {
            for (var node = Kept.First; node != null; node = node.Next)
                if (node.Value.File == file && node.Value.Written == written)
                {
                    Kept.Remove(node);
                    Kept.AddFirst(node);
                    return node.Value.Image;
                }
        }
        SKImage image = null;
        try
        {
            using var encoded = SKImage.FromEncodedData(file);
            image = encoded?.ToRasterImage(ensurePixelData: true);
        }
        catch (Exception ex) { Log.Warn($"Hi-res mod image {file}: {ex.Message}"); }
        if (image == null)
        {
            Log.Warn($"Hi-res mod image {file} won't decode");
            return null;
        }
        lock (Kept)
        {
            Kept.AddFirst((file, written, image));
            // Images let go aren't disposed: a frame drawn on another thread may still hold one.
            while (Kept.Count > 1 && Kept.Sum(k => (long)k.Image.Width * k.Image.Height * 4) > MaxBytes) Kept.RemoveLast();
        }
        return image;
    }
}

/// <summary>
/// Tints a hi-res picture as the game's colours change: each of its pixels is scaled, per channel, by how the
/// screen's colour of the game's index under it differs from the colour that index had when the picture was made
/// (screen ÷ base), interpolated between the game's pixels so the tint has no blocks. A fade darkens it as it
/// darkens the room; a light that brightens some indices brightens the parts of the picture they cover.
/// </summary>
public static class HiResTint
{
    private const string Sksl = @"
uniform shader image;
uniform shader indices;
uniform shader basePalette;
uniform shader screenPalette;
uniform float2 size;

half3 ratioAt(float2 ip) {
    ip = clamp(ip, float2(0.0), size - 1.0);
    float i = floor(indices.eval(ip + 0.5).a * 255.0 + 0.5);
    float2 q = float2(i + 0.5, 0.5);
    half3 b = basePalette.eval(q).rgb;
    half3 s = screenPalette.eval(q).rgb;
    return (s + 0.02) / (b + 0.02);
}

half4 main(float2 p) {
    half4 c = image.eval(p);
    float2 sp = p - 0.5;
    float2 i = floor(sp);
    half2 f = half2(fract(sp));
    half3 r = mix(mix(ratioAt(i), ratioAt(i + float2(1.0, 0.0)), f.x),
                  mix(ratioAt(i + float2(0.0, 1.0)), ratioAt(i + float2(1.0, 1.0)), f.x), f.y);
    return half4(min(c.rgb * r, half3(c.a)), c.a);
}";

    private static readonly Lazy<SKRuntimeEffect> Effect = new(() =>
    {
        var effect = SKRuntimeEffect.CreateShader(Sksl, out var errors);
        if (effect == null) Log.Error($"Hi-res tint SkSL compile failed: {errors}");
        return effect;
    });

    private static readonly SKSamplingOptions Nearest = new(SKFilterMode.Nearest, SKMipmapMode.None);

    /// <summary>
    /// <paramref name="image"/> over <paramref name="width"/>×<paramref name="height"/> game pixels at
    /// (<paramref name="x"/>,<paramref name="y"/>), tinted by the game's <paramref name="indices"/> under it (an
    /// alpha image of that size) from <paramref name="basePalette"/> to <paramref name="screenPalette"/> (256×1
    /// strips); null when the shader isn't available.
    /// </summary>
    public static SKShader Create(SKImage image, float x, float y, float width, float height, SKImage indices, SKImage basePalette, SKImage screenPalette)
    {
        if (Effect.Value is not { } effect || image == null || indices == null) return null;
        var builder = new SKRuntimeShaderBuilder(effect);
        builder.Children["image"] = image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, HiResImage.Sampling,
            SKMatrix.CreateScale(width / image.Width, height / image.Height));
        builder.Children["indices"] = indices.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, Nearest,
            SKMatrix.CreateScale(width / indices.Width, height / indices.Height));
        builder.Children["basePalette"] = basePalette.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, Nearest);
        builder.Children["screenPalette"] = screenPalette.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, Nearest);
        builder.Uniforms["size"] = new[] { width, height };
        return builder.Build(SKMatrix.CreateTranslation(x, y));
    }

    /// <summary>A palette (256 RGB triples) as a 256×1 strip.</summary>
    public static SKImage Strip(byte[] palette)
    {
        var rgba = new byte[256 * 4];
        for (int i = 0; i < 256 && i * 3 + 2 < palette.Length; i++)
        {
            rgba[i * 4] = palette[i * 3];
            rgba[i * 4 + 1] = palette[i * 3 + 1];
            rgba[i * 4 + 2] = palette[i * 3 + 2];
            rgba[i * 4 + 3] = 255;
        }
        return SKImage.FromPixelCopy(new SKImageInfo(256, 1, SKColorType.Rgba8888, SKAlphaType.Unpremul), rgba, 256 * 4);
    }

    /// <summary>A surface's indices as an alpha image (index ÷ 255).</summary>
    public static SKImage Indices(IndexedSurface surface)
    {
        var alpha = new byte[surface.Width * surface.Height];
        for (int y = 0; y < surface.Height; y++)
            Array.Copy(surface.Pixels, y * surface.Pitch, alpha, y * surface.Width, surface.Width);
        return SKImage.FromPixelCopy(new SKImageInfo(surface.Width, surface.Height, SKColorType.Alpha8, SKAlphaType.Premul), alpha, surface.Width);
    }

    /// <summary>Which indices a surface uses.</summary>
    public static bool[] Used(IndexedSurface surface)
    {
        var used = new bool[256];
        for (int y = 0; y < surface.Height; y++)
            for (int x = 0, o = y * surface.Pitch; x < surface.Width; x++) used[surface.Pixels[o + x]] = true;
        return used;
    }

    /// <summary>Whether the screen shows any index a picture covers in a colour other than its base one.</summary>
    public static bool Differs(bool[] used, byte[] basePalette, byte[] screenPalette)
    {
        for (int i = 0; i < 256; i++)
            if (used[i] && i * 3 + 2 < basePalette.Length && i * 3 + 2 < screenPalette.Length &&
                (basePalette[i * 3] != screenPalette[i * 3] || basePalette[i * 3 + 1] != screenPalette[i * 3 + 1] || basePalette[i * 3 + 2] != screenPalette[i * 3 + 2]))
                return true;
        return false;
    }
}
