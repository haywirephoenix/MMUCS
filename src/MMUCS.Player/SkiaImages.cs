using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MMUCS.Core.Imaging;
using SkiaSharp;

namespace MMUCS.Player;

/// <summary>One immutable (so thread-safe) SKImage per Core image, made the first time it's drawn and kept while the image lives.</summary>
public static class SkiaImages
{
    private static readonly ConditionalWeakTable<RgbaImage, SKImage> Images = [];

    public static SKImage Of(RgbaImage image) => image == null ? null : Images.GetValue(image, Copy);

    private static SKImage Copy(RgbaImage image)
    {
        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var handle = GCHandle.Alloc(image.Data, GCHandleType.Pinned);
        try
        {
            using var pixmap = new SKPixmap(info, handle.AddrOfPinnedObject(), image.Stride);
            return SKImage.FromPixelCopy(pixmap);
        }
        finally { handle.Free(); }
    }
}
