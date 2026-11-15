using Avalonia;
using Avalonia.Controls;
using MMUCS.Desktop.ViewModels.Docking;

namespace MMUCS.Desktop.Views;

/// <summary>
/// A viewer's own docking area; one per viewer, kept alive by <see cref="ViewerViewTemplate"/>.
/// Its tool title bars and document tabs follow <see cref="ViewerDock.ShowHeaders"/> (the
/// headers-hidden class, styled in the XAML).
/// </summary>
public partial class ViewerDockView : UserControl
{
    public ViewerDockView()
    {
        InitializeComponent();
        ApplyHeaders();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ViewerDock.ShowHeadersChanged += ApplyHeaders;
        ApplyHeaders();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ViewerDock.ShowHeadersChanged -= ApplyHeaders;
        base.OnDetachedFromVisualTree(e);
    }

    private void ApplyHeaders() => Classes.Set("headers-hidden", !ViewerDock.ShowHeaders);
}
