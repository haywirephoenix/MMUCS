using Avalonia;
using MMUCS.Player;
using SkiaSharp;

namespace MMUCS.Desktop.Controls;

/// <summary>Shows a <see cref="CutsceneFrame"/> and its text on the shared pan/zoom viewport (fits by default, nearest-neighbour when enlarged).</summary>
public sealed class VideoCanvas : SkiaViewport
{
    public static readonly StyledProperty<CutsceneFrame> ImageProperty =
        AvaloniaProperty.Register<VideoCanvas, CutsceneFrame>(nameof(Image));
    public static readonly StyledProperty<bool> LinearFilterProperty =
        AvaloniaProperty.Register<VideoCanvas, bool>(nameof(LinearFilter));

    public CutsceneFrame Image { get => GetValue(ImageProperty); set => SetValue(ImageProperty, value); }
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
    private sealed class Scene(CutsceneFrame frame, bool linear) : IRetainedScene
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
            frame.Draw(canvas, linear);
            canvas.Restore();
        }
    }
}
