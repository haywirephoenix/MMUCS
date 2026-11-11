using Avalonia.Controls;
using Avalonia.Interactivity;

namespace MMUCS.Desktop.Views;

public partial class AkosView : UserControl
{
    public AkosView() => InitializeComponent();

    private void OnFitClick(object sender, RoutedEventArgs e) => Canvas.ZoomToFit();
    private void OnActualClick(object sender, RoutedEventArgs e) => Canvas.ZoomToActual();
}
