using System.Collections.Generic;
using System.Linq;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;
using Dock.Model.Mvvm.Core;

namespace MMUCS.Desktop.ViewModels.Docking;

// The Costume viewer's own panels. They share the costume view model, like the main panels share
// the main one; App.axaml maps each type to its view.

public sealed class CostumeCelsTool(AkosViewModel akos) : Tool { public AkosViewModel Akos { get; } = akos; }
public sealed class CostumeChoresTool(AkosViewModel akos) : Tool { public AkosViewModel Akos { get; } = akos; }
public sealed class CostumePaletteTool(AkosViewModel akos) : Tool { public AkosViewModel Akos { get; } = akos; }
public sealed class CostumePreviewDocument(AkosViewModel akos) : Document { public AkosViewModel Akos { get; } = akos; }

/// <summary>
/// Docking inside the Costume viewer: Cels on the left, the preview in the middle, Chores and the
/// resolved palette as tabs on the right. Everything carries the <see cref="Group"/> dock group, so
/// these panels rearrange among themselves but can't be dropped into the main layout (and main
/// panels can't be dropped in here). They don't float.
/// </summary>
public sealed class CostumeDockFactory(AkosViewModel akos) : Factory, ILayoutBuilder
{
    public const string Group = "costume";
    public const string CelsId = "Cels", ChoresId = "Chores", PaletteId = "Palette", PreviewId = "Preview";
    public static readonly string[] PanelIds = { CelsId, ChoresId, PaletteId, PreviewId };

    public Dictionary<string, IDockable> CreatePanels()
    {
        T Panel<T>(T d, string id, string title) where T : DockableBase
        {
            d.Id = id;
            d.Title = title;
            d.CanClose = false;
            d.CanFloat = false;
            d.CanPin = false;
            d.DockGroup = Group;
            return d;
        }

        return new Dictionary<string, IDockable>
        {
            [CelsId] = Panel(new CostumeCelsTool(akos), CelsId, "Cels"),
            [ChoresId] = Panel(new CostumeChoresTool(akos), ChoresId, "Chores"),
            [PaletteId] = Panel(new CostumePaletteTool(akos), PaletteId, "Resolved palette"),
            [PreviewId] = Panel(new CostumePreviewDocument(akos), PreviewId, "Preview"),
        };
    }

    public ToolDock NewToolDock(string id, IList<IDockable> panels, IDockable active = null) => new()
    {
        Id = id,
        DockGroup = Group,
        CanFloat = false,
        ActiveDockable = active ?? panels.FirstOrDefault(),
        VisibleDockables = panels,
    };

    public DocumentDock NewDocumentDock(string id, IList<IDockable> panels, IDockable active = null) => new()
    {
        Id = id,
        DockGroup = Group,
        CanFloat = false,
        IsCollapsable = false,
        CanCreateDocument = false,
        CloseButtonShowMode = DocumentCloseButtonShowMode.Never,
        ActiveDockable = active ?? panels.FirstOrDefault(),
        VisibleDockables = panels,
    };

    // The inner panels can't be hidden, so there are no hidden slots to keep.
    public void RememberHidden(string id, IDock dock) { }

    public IRootDock WrapRoot(IDockable content)
    {
        var root = CreateRootDock();
        root.Id = "CostumeRoot";
        root.DockGroup = Group;
        root.CanFloat = false;
        root.ActiveDockable = content;
        root.DefaultDockable = content;
        root.VisibleDockables = CreateList<IDockable>(content);
        return root;
    }

    /// <summary>The default arrangement: the Costume section of layout.default.json, or the built-in one.</summary>
    public override IRootDock CreateLayout() => TryRestore(DockLayoutStore.ReadDefault()?.Costume) ?? CreateBuiltInLayout();

    /// <summary>Cels | Preview | Chores and Resolved palette as tabs.</summary>
    public IRootDock CreateBuiltInLayout()
    {
        var p = CreatePanels();
        var cels = NewToolDock("CelsDock", CreateList(p[CelsId]));
        cels.Proportion = 0.18;
        var preview = NewDocumentDock("PreviewDock", CreateList(p[PreviewId]));
        preview.Proportion = 0.60;
        var side = NewToolDock("SideDock", CreateList(p[ChoresId], p[PaletteId]));
        side.Proportion = 0.22;

        var content = new ProportionalDock
        {
            Id = "CostumeMain",
            DockGroup = Group,
            CanFloat = false,
            Orientation = Orientation.Horizontal,
            VisibleDockables = CreateList<IDockable>(cels, new ProportionalDockSplitter(), preview, new ProportionalDockSplitter(), side),
        };
        return WrapRoot(content);
    }

    /// <summary>Rebuilds a saved arrangement; null when it doesn't hold exactly this viewer's panels.</summary>
    public IRootDock TryRestore(DockLayoutStore.Node node)
    {
        if (node == null) return null;
        var panels = CreatePanels();
        var used = new HashSet<string>();
        var content = DockLayoutStore.Build(this, node, panels, used);
        if (content == null || !PanelIds.All(used.Contains)) return null;
        SetGroup(content);
        return WrapRoot(content);
    }

    /// <summary>Restored splits are created without a group; the whole tree needs it.</summary>
    private static void SetGroup(IDockable d)
    {
        d.DockGroup = Group;
        d.CanFloat = false;
        if (d is IDock dock && dock.VisibleDockables != null)
            foreach (var child in dock.VisibleDockables) SetGroup(child);
    }

    public override void InitLayout(IDockable layout)
    {
        DefaultHostWindowLocator = () => new HostWindow();
        base.InitLayout(layout);
    }

    // The panels stay inside the viewer: dropping one outside it leaves it where it was.
    public override void FloatDockable(IDockable dockable) { }
    public override void FloatDockable(IDockable dockable, DockWindowOptions options) { }
    public override void SplitToWindow(IDock dock, IDockable dockable, double x, double y, double width, double height) { }
    public override void SplitToWindow(IDock dock, IDockable dockable, double x, double y, double width, double height, DockWindowOptions options) { }

    /// <summary>Panels dropped beside another take the size they had.</summary>
    public override void OnDockableDocked(IDockable dockable, DockOperation operation)
    {
        base.OnDockableDocked(dockable, operation);
        EdgeDockSizing.Apply(dockable, operation);
    }

    public override IFactory CreatePreviewFactory() => new SizedPreviewFactory(this);

    /// <summary>A dock left empty hands its space to one neighbour (see <see cref="EdgeDockSizing.GiveSpaceToNeighbour"/>).</summary>
    public override void CollapseDock(IDock dock)
    {
        EdgeDockSizing.GiveSpaceToNeighbour(dock, emptied: true);
        base.CollapseDock(dock);
    }

    /// <summary>Notes the proportions around the target first, so the split can be sized from them.</summary>
    public override void SplitToDock(IDock dock, IDockable dockable, DockOperation operation)
    {
        EdgeDockSizing.BeforeSplit(dock);
        base.SplitToDock(dock, dockable, operation);
    }

    /// <summary>A panel leaving a dock it had to itself: remember that pane's size for when it gets one again.</summary>
    public override void MoveDockable(IDock sourceDock, IDock targetDock, IDockable sourceDockable, IDockable targetDockable)
    {
        EdgeDockSizing.RememberPaneSize(sourceDockable, sourceDock);
        base.MoveDockable(sourceDock, targetDock, sourceDockable, targetDockable);
    }

    /// <summary>A whole dock dragged away (by its title bar) hands its space to one neighbour too.</summary>
    public override void RemoveDockable(IDockable dockable, bool collapse)
    {
        // Only panel docks: Dock also removes split containers when it flattens the tree, and those keep their share.
        if (dockable is IToolDock or IDocumentDock) EdgeDockSizing.GiveSpaceToNeighbour((IDock)dockable, emptied: false);
        base.RemoveDockable(dockable, collapse);
    }

}
