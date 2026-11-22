using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Metadata;
using Avalonia.Interactivity;

namespace MMUCS.Desktop.Controls;

/// <summary>
/// The preview panel the viewers share (Costume, Object, Room): a toolbar on top, with the title, the
/// viewer's own <see cref="Tools"/>, Fit and 1:1, the <see cref="Info"/> line and any
/// <see cref="TrailingTools"/>; the canvas (<see cref="ContentControl.Content"/>) under it, with an
/// optional <see cref="Overlay"/> over the canvas and a <see cref="Footer"/> below. Fit and 1:1 act on
/// <see cref="Viewport"/>, or on the content when it is a <see cref="SkiaViewport"/>. The template is
/// in Styles/ViewerPreview.axaml.
/// </summary>
[TemplatePart(FitPart, typeof(Button))]
[TemplatePart(ActualPart, typeof(Button))]
public class ViewerPreview : ContentControl
{
    private const string FitPart = "PART_Fit", ActualPart = "PART_Actual";

    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<ViewerPreview, string>(nameof(Title));

    public static readonly StyledProperty<string> InfoProperty =
        AvaloniaProperty.Register<ViewerPreview, string>(nameof(Info));

    public static readonly StyledProperty<object> ToolsProperty =
        AvaloniaProperty.Register<ViewerPreview, object>(nameof(Tools));

    public static readonly StyledProperty<object> TrailingToolsProperty =
        AvaloniaProperty.Register<ViewerPreview, object>(nameof(TrailingTools));

    public static readonly StyledProperty<object> OverlayProperty =
        AvaloniaProperty.Register<ViewerPreview, object>(nameof(Overlay));

    public static readonly StyledProperty<object> FooterProperty =
        AvaloniaProperty.Register<ViewerPreview, object>(nameof(Footer));

    public static readonly StyledProperty<SkiaViewport> ViewportProperty =
        AvaloniaProperty.Register<ViewerPreview, SkiaViewport>(nameof(Viewport));

    /// <summary>The first thing on the toolbar.</summary>
    public string Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    /// <summary>A line of detail after Fit and 1:1.</summary>
    public string Info { get => GetValue(InfoProperty); set => SetValue(InfoProperty, value); }

    /// <summary>The viewer's own toolbar controls, between the title and Fit.</summary>
    public object Tools { get => GetValue(ToolsProperty); set => SetValue(ToolsProperty, value); }

    /// <summary>Toolbar controls after the info line.</summary>
    public object TrailingTools { get => GetValue(TrailingToolsProperty); set => SetValue(TrailingToolsProperty, value); }

    /// <summary>Drawn over the canvas (it places itself, for example bottom left).</summary>
    public object Overlay { get => GetValue(OverlayProperty); set => SetValue(OverlayProperty, value); }

    /// <summary>A strip under the canvas, such as a hover readout.</summary>
    public object Footer { get => GetValue(FooterProperty); set => SetValue(FooterProperty, value); }

    /// <summary>What Fit and 1:1 zoom; when unset, the content if it is a viewport.</summary>
    public SkiaViewport Viewport { get => GetValue(ViewportProperty); set => SetValue(ViewportProperty, value); }

    private SkiaViewport Target => Viewport ?? Content as SkiaViewport;

    private Button _fit, _actual;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _fit?.Click -= OnFitClick;

        _actual?.Click -= OnActualClick;

        _fit = e.NameScope.Find<Button>(FitPart);
        _actual = e.NameScope.Find<Button>(ActualPart);
        _fit?.Click += OnFitClick;

        _actual?.Click += OnActualClick;
    }

    private void OnFitClick(object sender, RoutedEventArgs e) => Target?.ZoomToFit();
    private void OnActualClick(object sender, RoutedEventArgs e) => Target?.ZoomToActual();
}
