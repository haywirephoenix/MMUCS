using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MMUCS.Desktop.Controls;

namespace MMUCS.Desktop.Views;

public partial class AkosView : UserControl
{
    public AkosView() => InitializeComponent();

    /// <summary>The preview's canvas, wherever its panel is docked inside the viewer.</summary>
    private CelCanvas PreviewCanvas => this.GetVisualDescendants().OfType<CelCanvas>().FirstOrDefault();

    private void OnFitClick(object sender, RoutedEventArgs e) => PreviewCanvas?.ZoomToFit();
    private void OnActualClick(object sender, RoutedEventArgs e) => PreviewCanvas?.ZoomToActual();
}
