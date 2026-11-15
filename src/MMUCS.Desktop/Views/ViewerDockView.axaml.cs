using Avalonia.Controls;

namespace MMUCS.Desktop.Views;

/// <summary>A viewer's own docking area; one per viewer, kept alive by <see cref="ViewerViewTemplate"/>.</summary>
public partial class ViewerDockView : UserControl
{
    public ViewerDockView() => InitializeComponent();
}
