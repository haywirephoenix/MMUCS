using System.Collections.Generic;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;

namespace MMUCS.Desktop.ViewModels.Docking;

// The Room viewer's own panels, over the room view model.

public sealed class RoomObjectsTool(RoomViewModel room) : Tool { public RoomViewModel Room { get; } = room; }
public sealed class RoomPreviewDocument(RoomViewModel room) : Document { public RoomViewModel Room { get; } = room; }

/// <summary>
/// Docking inside the Room viewer: the Preview with Objects beside it (see <see cref="ViewerDockFactory"/>).
/// The toolbar's Objects toggle hides and shows the Objects panel.
/// </summary>
public sealed class RoomDockFactory(RoomViewModel room) : ViewerDockFactory(GroupId, "Room")
{
    public const string GroupId = "room";
    public const string ObjectsId = "Objects", PreviewId = "Preview";
    public static readonly string[] PanelIds = [ObjectsId, PreviewId];

    public override IReadOnlyList<string> Ids => PanelIds;

    protected override IEnumerable<ViewerPanel> DefinePanels() =>
    [
        new(ObjectsId, "Objects", new RoomObjectsTool(room)),
        new(PreviewId, "Preview", new RoomPreviewDocument(room)),
    ];

    protected override IDockable CreateBuiltInContent(Dictionary<string, IDockable> p)
    {
        var preview = NewDocumentDock("PreviewDock", CreateList(p[PreviewId]));
        preview.Proportion = 1 - EdgeDockSizing.SideProportion;
        var objects = NewToolDock("ObjectsDock", CreateList(p[ObjectsId]));
        objects.Proportion = EdgeDockSizing.SideProportion;
        return Split("RoomMain", Orientation.Horizontal, preview, objects);
    }

    public override DockLayoutStore.Node DefaultNode(DockLayoutStore.LayoutFile file) => file.Room;
}
