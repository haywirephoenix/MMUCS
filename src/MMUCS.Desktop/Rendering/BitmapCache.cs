using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;
using MMUCS.Core.Imaging;

namespace MMUCS.Desktop.Rendering;

/// <summary>One Avalonia bitmap per Core image, made the first time it's drawn and kept while the image lives.</summary>
public static class BitmapCache
{
    private static readonly ConditionalWeakTable<RgbaImage, Bitmap> Bitmaps = [];

    public static Bitmap Of(RgbaImage image) => image == null ? null : Bitmaps.GetValue(image, i => i.ToBitmap());
}
