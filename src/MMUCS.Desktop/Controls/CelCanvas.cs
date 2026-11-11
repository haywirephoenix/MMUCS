using Avalonia;
using MMUCS.Desktop.Rendering;
using SkiaSharp;

namespace MMUCS.Desktop.Controls;

public enum CelViewMode { Shader, Index, Raw }

/// <summary>
/// AKOS cel viewer. In Shader mode the indexed "data" image is resolved through the palette by an
/// SkSL runtime effect (port of akos.gdshader), so palette/shadow/alpha changes are instant and need
/// no re-decode. Index mode shows the raw data encoding; Raw mode shows the CPU-resolved image.
/// </summary>
public sealed class CelCanvas : SkiaViewport
{
    public static readonly StyledProperty<SKImage> DataImageProperty =
        AvaloniaProperty.Register<CelCanvas, SKImage>(nameof(DataImage));
    public static readonly StyledProperty<SKImage> RawImageProperty =
        AvaloniaProperty.Register<CelCanvas, SKImage>(nameof(RawImage));
    public static readonly StyledProperty<SKImage> PaletteImageProperty =
        AvaloniaProperty.Register<CelCanvas, SKImage>(nameof(PaletteImage));
    public static readonly StyledProperty<CelViewMode> ModeProperty =
        AvaloniaProperty.Register<CelCanvas, CelViewMode>(nameof(Mode));
    public static readonly StyledProperty<bool> AlphaEnabledProperty =
        AvaloniaProperty.Register<CelCanvas, bool>(nameof(AlphaEnabled), true);
    public static readonly StyledProperty<bool> ShadowColorEnabledProperty =
        AvaloniaProperty.Register<CelCanvas, bool>(nameof(ShadowColorEnabled), true);
    public static readonly StyledProperty<double> ShadowAmountProperty =
        AvaloniaProperty.Register<CelCanvas, double>(nameof(ShadowAmount), 0.4);
    public static readonly StyledProperty<bool> LinearFilterProperty =
        AvaloniaProperty.Register<CelCanvas, bool>(nameof(LinearFilter));

    public SKImage DataImage { get => GetValue(DataImageProperty); set => SetValue(DataImageProperty, value); }
    public SKImage RawImage { get => GetValue(RawImageProperty); set => SetValue(RawImageProperty, value); }
    public SKImage PaletteImage { get => GetValue(PaletteImageProperty); set => SetValue(PaletteImageProperty, value); }
    public CelViewMode Mode { get => GetValue(ModeProperty); set => SetValue(ModeProperty, value); }
    public bool AlphaEnabled { get => GetValue(AlphaEnabledProperty); set => SetValue(AlphaEnabledProperty, value); }
    public bool ShadowColorEnabled { get => GetValue(ShadowColorEnabledProperty); set => SetValue(ShadowColorEnabledProperty, value); }
    public double ShadowAmount { get => GetValue(ShadowAmountProperty); set => SetValue(ShadowAmountProperty, value); }
    public bool LinearFilter { get => GetValue(LinearFilterProperty); set => SetValue(LinearFilterProperty, value); }

    static CelCanvas()
    {
        AffectsRender<CelCanvas>(DataImageProperty, RawImageProperty, PaletteImageProperty, ModeProperty,
            AlphaEnabledProperty, ShadowColorEnabledProperty, ShadowAmountProperty, LinearFilterProperty);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DataImageProperty || change.Property == RawImageProperty)
            ContentChanged();
    }

    private SKImage Current => Mode == CelViewMode.Raw ? RawImage : DataImage;

    protected override Size ContentSize
    {
        get
        {
            var img = Current ?? DataImage;
            return img != null ? new Size(img.Width, img.Height) : default;
        }
    }

    protected override IViewportScene CreateScene()
    {
        var mode = Mode;
        if (mode == CelViewMode.Shader && !AkosPaletteShader.IsAvailable) mode = CelViewMode.Raw;

        return new Scene(mode, DataImage, RawImage, PaletteImage,
            new AkosPaletteShader.Options(AlphaEnabled, ShadowColorEnabled, (float)ShadowAmount, LinearFilter));
    }

    private sealed class Scene : IViewportScene
    {
        private readonly CelViewMode _mode;
        private readonly SKImage _data, _raw, _palette;
        private readonly AkosPaletteShader.Options _options;

        public Scene(CelViewMode mode, SKImage data, SKImage raw, SKImage palette, AkosPaletteShader.Options options)
        {
            _mode = mode; _data = data; _raw = raw; _palette = palette; _options = options;
        }

        public void Render(SKCanvas canvas, in ViewTransform view, SKRect viewBounds)
        {
            canvas.Save();
            canvas.Concat(view.Matrix);

            float scale = canvas.TotalMatrix.ScaleX;
            var sampling = (_options.LinearFilter || scale < 0.999f)
                ? new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None)
                : new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None);

            switch (_mode)
            {
                case CelViewMode.Shader when _data != null && _palette != null:
                    using (var shader = AkosPaletteShader.Create(_data, _palette, _options))
                    using (var paint = new SKPaint { Shader = shader })
                        canvas.DrawRect(0, 0, _data.Width, _data.Height, paint);
                    break;
                case CelViewMode.Index when _data != null:
                    canvas.DrawImage(_data, 0, 0, sampling);
                    break;
                default:
                    if (_raw != null) canvas.DrawImage(_raw, 0, 0, sampling);
                    break;
            }

            canvas.Restore();
        }
    }
}
