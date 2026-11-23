using Avalonia;
using SkiaSharp;

namespace MMUCS.Desktop.Controls;

/// <summary>Shows a <see cref="VideoFrame"/> and its text on the shared pan/zoom viewport (fits by default, nearest-neighbour when enlarged).</summary>
public sealed class VideoCanvas : SkiaViewport
{
    public static readonly StyledProperty<VideoFrame> ImageProperty =
        AvaloniaProperty.Register<VideoCanvas, VideoFrame>(nameof(Image));
    public static readonly StyledProperty<bool> LinearFilterProperty =
        AvaloniaProperty.Register<VideoCanvas, bool>(nameof(LinearFilter));

    public VideoFrame Image { get => GetValue(ImageProperty); set => SetValue(ImageProperty, value); }
    public bool LinearFilter { get => GetValue(LinearFilterProperty); set => SetValue(LinearFilterProperty, value); }

    static VideoCanvas() => AffectsRender<VideoCanvas>(ImageProperty, LinearFilterProperty);

    public VideoCanvas() => ShowCheckerboard = false;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ImageProperty)
        {
            ContentChanged();
        }
    }

    protected override Size ContentSize => Image is { } i ? new Size(i.Width, i.Height) : default;

    protected override IViewportScene CreateScene() => new Scene(Image, LinearFilter);

    /// <summary>Keeps its frame alive while a draw operation may still show it.</summary>
    private sealed class Scene(VideoFrame frame, bool linear) : IRetainedScene
    {
        public void Retain() => frame?.Retain();
        public void Release() => frame?.Release();

        public void Render(SKCanvas canvas, in ViewTransform view, SKRect viewBounds)
        {
            if (frame == null)
            {
                return;
            }

            canvas.Save();
            canvas.Concat(view.Matrix);
            bool smooth = linear || canvas.TotalMatrix.ScaleX < 0.999f;
            canvas.DrawImage(frame.Image, 0, 0, new SKSamplingOptions(smooth ? SKFilterMode.Linear : SKFilterMode.Nearest, SKMipmapMode.None));
            // The text belongs to the frame (the game draws it into the picture), so it scales with it.
            frame.Subtitles?.Draw(canvas, smooth);
            canvas.Restore();
        }
    }
}
