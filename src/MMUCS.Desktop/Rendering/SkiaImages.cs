using System;
using System.Runtime.InteropServices;
using MMUCS.Core.Imaging;
using SkiaSharp;

namespace MMUCS.Desktop.Rendering;

public static class SkiaImages
{
    /// <summary>Copies a straight-alpha RGBA8 Core image into an immutable (thread-safe) SKImage.</summary>
    public static SKImage ToSkImage(this RgbaImage image)
    {
        if (image == null)
        {
            return null;
        }

        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var handle = GCHandle.Alloc(image.Data, GCHandleType.Pinned);
        try
        {
            using var pixmap = new SKPixmap(info, handle.AddrOfPinnedObject(), image.Stride);
            return SKImage.FromPixelCopy(pixmap);
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>Copies a straight-alpha RGBA8 Core image into an Avalonia bitmap (for list thumbnails).</summary>
    public static Avalonia.Media.Imaging.Bitmap ToBitmap(this RgbaImage image)
    {
        if (image == null)
        {
            return null;
        }

        var bitmap = new Avalonia.Media.Imaging.WriteableBitmap(new Avalonia.PixelSize(image.Width, image.Height),
            new Avalonia.Vector(96, 96), Avalonia.Platform.PixelFormat.Rgba8888, Avalonia.Platform.AlphaFormat.Unpremul);
        using var buffer = bitmap.Lock();
        for (int y = 0; y < image.Height; y++)
        {
            Marshal.Copy(image.Data, y * image.Stride, buffer.Address + y * buffer.RowBytes, image.Stride);
        }

        return bitmap;
    }

    /// <summary>Builds a 256x1 opaque palette strip (RGB only; alpha is decided by the shader).</summary>
    public static SKImage CreatePaletteStrip(ScummColor[] colors)
    {
        var rgba = new byte[256 * 4];
        if (colors != null)
        {
            int n = Math.Min(256, colors.Length);
            for (int i = 0; i < n; i++)
            {
                rgba[i * 4 + 0] = colors[i].R8;
                rgba[i * 4 + 1] = colors[i].G8;
                rgba[i * 4 + 2] = colors[i].B8;
                rgba[i * 4 + 3] = 255;
            }
        }
        return new RgbaImage(256, 1, rgba).ToSkImage();
    }
}
