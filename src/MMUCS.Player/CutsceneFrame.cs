using System;
using System.Threading;
using MMUCS.Core.Media;
using MMUCS.Player.Text;
using SkiaSharp;

namespace MMUCS.Player;

/// <summary>
/// One cutscene picture with its text, as the player shows it: drawn with <see cref="Draw"/> in picture
/// pixels (a host's pan/zoom canvas, such as the media player's) or with <see cref="Render"/> fitted into a
/// viewport (a standalone player). Freed as soon as nothing uses it. Each picture
/// holds 1.2 MB outside the managed heap (and maybe a GPU texture), which the garbage collector can't
/// see, so leaving them to finalizers lets them pile up during playback.
/// <para>The creator holds the first reference and calls <see cref="Release"/> when it replaces the
/// picture; every draw operation showing it holds one more until the renderer has finished with it.</para>
/// </summary>
public sealed class CutsceneFrame
{
    private int _refs = 1;

    private CutsceneFrame(SKImage image, SubtitleOverlay subtitles)
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
    public static CutsceneFrame From(SmushPlayer video, SubtitleRenderer subtitles = null)
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
        return new CutsceneFrame(SKImage.FromBitmap(bitmap), text);
    }

    /// <summary>
    /// Draws the picture and its text at (0, 0) in picture pixels, through whatever matrix the canvas has.
    /// The text belongs to the frame (the game draws it into the picture), so it scales with it. Smooth
    /// filtering when asked, or when the picture is shrunk; nearest-neighbour otherwise, so pixels stay square.
    /// </summary>
    public void Draw(SKCanvas canvas, bool linear = false)
    {
        bool smooth = linear || canvas.TotalMatrix.ScaleX < 0.999f;
        canvas.DrawImage(Image, 0, 0, new SKSamplingOptions(smooth ? SKFilterMode.Linear : SKFilterMode.Nearest, SKMipmapMode.None));
        Subtitles?.Draw(canvas, smooth);
    }

    /// <summary>Draws the frame into <paramref name="viewport"/>: as large as it fits, centred, black around it.</summary>
    public void Render(SKCanvas canvas, SKRect viewport, bool linear = false)
    {
        using (var black = new SKPaint { Color = SKColors.Black }) canvas.DrawRect(viewport, black);
        float scale = Math.Min(viewport.Width / Width, viewport.Height / Height);
        if (scale <= 0) return;
        canvas.Save();
        canvas.ClipRect(viewport);
        canvas.Translate(viewport.Left + (viewport.Width - Width * scale) / 2, viewport.Top + (viewport.Height - Height * scale) / 2);
        canvas.Scale(scale);
        Draw(canvas, linear);
        canvas.Restore();
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
