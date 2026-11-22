using System.Collections.Generic;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;

namespace MMUCS.Desktop.ViewModels.Docking;

// The Object viewer's own panels, over the object view model.

public sealed class ObjectStatesTool(ObjectImageViewModel obj) : Tool { public ObjectImageViewModel Object { get; } = obj; }
public sealed class ObjectPreviewDocument(ObjectImageViewModel obj) : Document { public ObjectImageViewModel Object { get; } = obj; }

/// <summary>Docking inside the Object viewer: States above the Preview (see <see cref="ViewerDockFactory"/>).</summary>
public sealed class ObjectDockFactory(ObjectImageViewModel obj) : ViewerDockFactory(GroupId, "Object")
{
    public const string GroupId = "object";
    public const string StatesId = "States", PreviewId = "Preview";
    public static readonly string[] PanelIds = { StatesId, PreviewId };

    public override IReadOnlyList<string> Ids => PanelIds;

    protected override IEnumerable<ViewerPanel> DefinePanels() => new ViewerPanel[]
    {
        new(StatesId, "States", new ObjectStatesTool(obj)),
        new(PreviewId, "Preview", new ObjectPreviewDocument(obj)),
    };

    protected override IDockable CreateBuiltInContent(Dictionary<string, IDockable> p)
    {
        var states = NewToolDock("StatesDock", CreateList(p[StatesId]));
        states.Proportion = 0.35;
        var preview = NewDocumentDock("PreviewDock", CreateList(p[PreviewId]));
        preview.Proportion = 0.65;
        return Split("ObjectMain", Orientation.Vertical, states, preview);
    }

    public override DockLayoutStore.Node DefaultNode(DockLayoutStore.LayoutFile file) => file.Object;
}
