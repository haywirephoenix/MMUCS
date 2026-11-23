using System.Threading;
using MMUCS.Core.Media;
using SkiaSharp;

namespace MMUCS.Desktop.Controls;

/// <summary>
/// One cutscene picture for <see cref="VideoCanvas"/>, freed as soon as nothing uses it. Each picture
/// holds 1.2 MB outside the managed heap (and maybe a GPU texture), which the garbage collector can't
/// see, so leaving them to finalizers lets them pile up during playback.
/// <para>The creator holds the first reference and calls <see cref="Release"/> when it replaces the
/// picture; every draw operation showing it holds one more until the renderer has finished with it.</para>
/// </summary>
public sealed class VideoFrame
{
    private int _refs = 1;

    private VideoFrame(SKImage image) => Image = image;

    public SKImage Image { get; }
    public int Width => Image.Width;
    public int Height => Image.Height;

    /// <summary>
    /// The player's current frame, converted straight into the picture's own pixels (opaque BGRA, Skia's
    /// native order, so drawing it needs no conversion).
    /// </summary>
    public static VideoFrame From(SmushPlayer video)
    {
        var info = new SKImageInfo(video.Video.Width, video.Video.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var bitmap = new SKBitmap(info);
        using (var pixmap = bitmap.PeekPixels())
        {
            video.CopyBgra(pixmap.GetPixelSpan<uint>());
        }
        // An immutable bitmap's pixels are shared by the image, not copied.
        bitmap.SetImmutable();
        return new VideoFrame(SKImage.FromBitmap(bitmap));
    }

    public void Retain() => Interlocked.Increment(ref _refs);

    public void Release()
    {
        if (Interlocked.Decrement(ref _refs) == 0)
        {
            Image.Dispose();
        }
    }
}
