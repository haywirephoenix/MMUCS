using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MMUCS.Desktop.ViewModels.Docking;

namespace MMUCS.Desktop.ViewModels;

/// <summary>
/// The Inspector panel: foldout sections for the selected block's metadata and for the viewer of it (a
/// costume or an object image, with that viewer's own panels), instead of the Costume and Object panels
/// taking turns in front. Both follow every selection, so a block with nothing to view clears the viewer
/// rather than leaving the last costume or object up.
/// </summary>
public sealed partial class InspectorViewModel : ObservableObject
{
    public InspectorViewModel(ObservableCollection<MetadataRow> metadata)
    {
        Metadata = metadata;
        Metadata.CollectionChanged += (_, _) => OnPropertyChanged(nameof(Hint));
    }

    /// <summary>The selected block's metadata (shared with the Metadata panel).</summary>
    public ObservableCollection<MetadataRow> Metadata { get; }

    /// <summary>Metadata section open (saved).</summary>
    [ObservableProperty] public partial bool IsMetadataExpanded { get; set; } = true;

    /// <summary>Viewer section open (saved).</summary>
    [ObservableProperty] public partial bool IsViewerExpanded { get; set; } = true;

    /// <summary>The viewer shown (Costume or Object); null when the selection has nothing to view.</summary>
    [ObservableProperty] public partial ViewerDock Content { get; set; }

    public bool IsEmpty => Content == null;

    /// <summary>The viewer section's title: "Costume" or "Object".</summary>
    public string ContentTitle => Content?.Factory.Name;

    /// <summary>What the empty viewer area says.</summary>
    public string Hint => Metadata.Count == 0
        ? "Pick a costume or an object to inspect it here."
        : "Nothing to preview for this block.";

    partial void OnContentChanged(ViewerDock value)
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(ContentTitle));
    }
}
