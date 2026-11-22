using Avalonia;
using SkiaSharp;

namespace MMUCS.Desktop.Controls;

/// <summary>Shows a video frame on the shared pan/zoom viewport (fits by default, nearest-neighbour when enlarged).</summary>
public sealed class VideoCanvas : SkiaViewport
{
    public static readonly StyledProperty<SKImage> ImageProperty =
        AvaloniaProperty.Register<VideoCanvas, SKImage>(nameof(Image));
    public static readonly StyledProperty<bool> LinearFilterProperty =
        AvaloniaProperty.Register<VideoCanvas, bool>(nameof(LinearFilter));

    public SKImage Image { get => GetValue(ImageProperty); set => SetValue(ImageProperty, value); }
    public bool LinearFilter { get => GetValue(LinearFilterProperty); set => SetValue(LinearFilterProperty, value); }

    static VideoCanvas() => AffectsRender<VideoCanvas>(ImageProperty, LinearFilterProperty);

    public VideoCanvas() => ShowCheckerboard = false;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ImageProperty) ContentChanged();
    }

    protected override Size ContentSize => Image is { } i ? new Size(i.Width, i.Height) : default;

    protected override IViewportScene CreateScene() => new Scene(Image, LinearFilter);

    private sealed class Scene(SKImage image, bool linear) : IViewportScene
    {
        public void Render(SKCanvas canvas, in ViewTransform view, SKRect viewBounds)
        {
            if (image == null) return;
            canvas.Save();
            canvas.Concat(view.Matrix);
            bool smooth = linear || canvas.TotalMatrix.ScaleX < 0.999f;
            canvas.DrawImage(image, 0, 0, new SKSamplingOptions(smooth ? SKFilterMode.Linear : SKFilterMode.Nearest, SKMipmapMode.None));
            canvas.Restore();
        }
    }
}
