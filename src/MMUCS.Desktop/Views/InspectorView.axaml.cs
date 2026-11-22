using System.ComponentModel;
using Avalonia.Controls;
using MMUCS.Desktop.ViewModels;

namespace MMUCS.Desktop.Views;

/// <summary>The Inspector panel (see <see cref="InspectorViewModel"/>).</summary>
public partial class InspectorView : UserControl
{
    /// <summary>The Metadata rows' height while the viewer is open below them.</summary>
    private const double MetadataHeightOverViewer = 240;

    private InspectorViewModel _vm;

    public InspectorView() => InitializeComponent();

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        _vm?.PropertyChanged -= OnVmChanged;

        _vm = DataContext as InspectorViewModel;
        MetadataRows.DataContext = _vm?.Metadata;
        _vm?.PropertyChanged += OnVmChanged;

        SizeSections();
    }

    private void OnVmChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(InspectorViewModel.IsMetadataExpanded) or nameof(InspectorViewModel.IsViewerExpanded) or nameof(InspectorViewModel.Content))
        {
            SizeSections();
        }
    }

    /// <summary>The viewer takes the height when it's open; otherwise the Metadata rows do.</summary>
    private void SizeSections()
    {
        if (_vm == null)
        {
            return;
        }

        bool viewerOpen = !_vm.IsEmpty && _vm.IsViewerExpanded;
        bool metadataFills = _vm.IsMetadataExpanded && !viewerOpen;
        Sections.RowDefinitions[1].Height = metadataFills ? GridLength.Star : GridLength.Auto;
        Sections.RowDefinitions[3].Height = metadataFills ? GridLength.Auto : GridLength.Star;
        MetadataSection.MaxHeight = viewerOpen ? MetadataHeightOverViewer : double.PositiveInfinity;
    }
}
