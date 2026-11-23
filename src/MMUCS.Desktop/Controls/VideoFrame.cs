using System.Threading;
using MMUCS.Player.Text;
using MMUCS.Core.Media;
using MMUCS.Desktop.Rendering;
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

    private VideoFrame(SKImage image, SubtitleOverlay subtitles)
    {
        Image = image;
        Subtitles = subtitles;
    }

    public SKImage Image { get; }
    /// <summary>The frame's text, drawn over the picture, or null.</summary>
    public SubtitleOverlay Subtitles { get; }
    public int Width => Image.Width;
    public int Height => Image.Height;

    /// <summary>
    /// The player's current frame, converted straight into the picture's own pixels (opaque BGRA, Skia's
    /// native order, so drawing it needs no conversion), with its text drawn by <paramref name="subtitles"/>
    /// (none when null).
    /// </summary>
    public static VideoFrame From(SmushPlayer video, SubtitleRenderer subtitles = null)
    {
        var info = new SKImageInfo(video.Video.Width, video.Video.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var bitmap = new SKBitmap(info);
        using (var pixmap = bitmap.PeekPixels())
        {
            video.CopyBgra(pixmap.GetPixelSpan<uint>());
        }
        // An immutable bitmap's pixels are shared by the image, not copied.
        bitmap.SetImmutable();
        var text = subtitles?.Build(video.Current, video.Video.Width, video.Video.Height);
        return new VideoFrame(SKImage.FromBitmap(bitmap), text);
    }

    public void Retain() => Interlocked.Increment(ref _refs);

    public void Release()
    {
        if (Interlocked.Decrement(ref _refs) == 0)
        {
            Image.Dispose();
            Subtitles?.Dispose();
        }
    }
}
