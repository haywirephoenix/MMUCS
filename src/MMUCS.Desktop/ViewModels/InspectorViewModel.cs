using CommunityToolkit.Mvvm.ComponentModel;
using MMUCS.Desktop.ViewModels.Docking;

namespace MMUCS.Desktop.ViewModels;

/// <summary>
/// The Inspector panel: shows the viewer for what was last picked (a costume or an object image), with
/// that viewer's own panels, instead of the Costume and Object panels taking turns in front.
/// </summary>
public sealed partial class InspectorViewModel : ObservableObject
{
    /// <summary>The viewer shown (Costume or Object); null before anything is picked.</summary>
    [ObservableProperty] private ViewerDock _content;

    public bool IsEmpty => Content == null;

    partial void OnContentChanged(ViewerDock value) => OnPropertyChanged(nameof(IsEmpty));
}
