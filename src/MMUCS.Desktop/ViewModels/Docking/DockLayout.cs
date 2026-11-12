using System.Collections.Generic;
using System.Linq;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;
using Dock.Model.Mvvm.Core;

namespace MMUCS.Desktop.ViewModels.Docking;

// One dockable type per panel so App.axaml can map each to its view with a DataTemplate.
// Every panel shares the main view model; the panels are just different views of it.

public sealed class BlocksTool(MainWindowViewModel main) : Tool { public MainWindowViewModel Main { get; } = main; }
public sealed class MetadataTool(MainWindowViewModel main) : Tool { public MainWindowViewModel Main { get; } = main; }
public sealed class HexTool(MainWindowViewModel main) : Tool { public MainWindowViewModel Main { get; } = main; }
public sealed class RoomDocument(MainWindowViewModel main) : Document { public MainWindowViewModel Main { get; } = main; }
public sealed class CostumeDocument(MainWindowViewModel main) : Document { public MainWindowViewModel Main { get; } = main; }
public sealed class ScriptDocument(MainWindowViewModel main) : Document { public MainWindowViewModel Main { get; } = main; }

/// <summary>
/// Builds the default layout (Blocks, Metadata and Hex stacked on the left, viewers on the right) and hosts floating
/// panels in real OS windows. Panels can't be closed (yet), so nothing gets lost; Layout ▸ Reset to
/// default rebuilds the default arrangement. Saved layouts are handled by <see cref="DockLayoutStore"/>.
/// </summary>
public sealed class MmucsDockFactory(MainWindowViewModel main) : Factory
{
    public const string BlocksId = "Blocks", MetadataId = "Metadata", HexId = "Hex";
    public const string RoomId = "Room", CostumeId = "Costume", ScriptId = "Script";

    public static readonly string[] PanelIds = { BlocksId, MetadataId, HexId, RoomId, CostumeId, ScriptId };

    /// <summary>Creates the six panels (each layout gets fresh instances).</summary>
    public Dictionary<string, IDockable> CreatePanels()
    {
        T Panel<T>(T d, string id, string title) where T : DockableBase
        {
            d.Id = id;
            d.Title = title;
            d.CanClose = false;
            d.CanFloat = true;
            d.CanPin = false;
            return d;
        }

        return new Dictionary<string, IDockable>
        {
            [BlocksId] = Panel(new BlocksTool(main), BlocksId, "Blocks"),
            [MetadataId] = Panel(new MetadataTool(main), MetadataId, "Metadata"),
            [HexId] = Panel(new HexTool(main), HexId, "Hex"),
            [RoomId] = Panel(new RoomDocument(main), RoomId, "Room"),
            [CostumeId] = Panel(new CostumeDocument(main), CostumeId, "Costume"),
            [ScriptId] = Panel(new ScriptDocument(main), ScriptId, "Script"),
        };
    }

    public ToolDock NewToolDock(string id, IList<IDockable> panels, IDockable active = null) => new()
    {
        Id = id,
        ActiveDockable = active ?? panels.FirstOrDefault(),
        VisibleDockables = panels,
    };

    public DocumentDock NewDocumentDock(string id, IList<IDockable> panels, IDockable active = null) => new()
    {
        Id = id,
        IsCollapsable = false,
        CanCreateDocument = false,
        CloseButtonShowMode = DocumentCloseButtonShowMode.Never,
        ActiveDockable = active ?? panels.FirstOrDefault(),
        VisibleDockables = panels,
    };

    /// <summary>Wraps the main dock tree in the root used by the main window.</summary>
    public IRootDock CreateMainRoot(IDockable content)
    {
        var root = CreateRootDock();
        root.Id = "Root";
        root.ActiveDockable = content;
        root.DefaultDockable = content;
        root.VisibleDockables = CreateList<IDockable>(content);
        // Torn-off panels become real OS windows (movable to other monitors), like the Godot build.
        root.FloatingWindowHostMode = DockFloatingWindowHostMode.Native;
        return root;
    }

    /// <summary>A floating window holding <paramref name="content"/>, shaped like the ones Dock creates.</summary>
    public IDockWindow CreateFloatingWindow(IDockable content, double x, double y, double width, double height)
    {
        var root = CreateRootDock();
        root.Title = nameof(IRootDock);
        root.VisibleDockables = CreateList<IDockable>(content);
        root.ActiveDockable = content;
        root.DefaultDockable = content;

        var window = CreateDockWindow();
        window.Id = nameof(IDockWindow);
        window.Title = "";
        window.X = x;
        window.Y = y;
        window.Width = width;
        window.Height = height;
        window.Layout = root;
        root.Window = window;
        return window;
    }

    public override IRootDock CreateLayout() => CreateDefaultLayout(CreatePanels());

    /// <summary>
    /// Default layout: one column on the left stacking Blocks, Metadata and Hex, with the viewers
    /// (Room/Costume/Script) taking the rest.
    /// </summary>
    public IRootDock CreateDefaultLayout(Dictionary<string, IDockable> p)
    {
        var blocksDock = NewToolDock("LeftDock", CreateList(p[BlocksId]));
        blocksDock.Proportion = 0.60;

        var metadataDock = NewToolDock("MetadataDock", CreateList(p[MetadataId]));
        metadataDock.Proportion = 0.26;

        var hexDock = NewToolDock("HexDock", CreateList(p[HexId]));
        hexDock.Proportion = 0.14;

        var left = new ProportionalDock
        {
            Id = "LeftColumn",
            Proportion = 0.31,
            Orientation = Orientation.Vertical,
            VisibleDockables = CreateList<IDockable>(blocksDock, new ProportionalDockSplitter(), metadataDock, new ProportionalDockSplitter(), hexDock),
        };

        var documents = NewDocumentDock("Documents", CreateList(p[RoomId], p[CostumeId], p[ScriptId]));
        documents.Proportion = 0.69;

        var mainDock = new ProportionalDock
        {
            Id = "MainDock",
            Orientation = Orientation.Horizontal,
            VisibleDockables = CreateList<IDockable>(left, new ProportionalDockSplitter(), documents),
        };

        return CreateMainRoot(mainDock);
    }

    /// <summary>
    /// The original arrangement — Blocks | Room/Costume/Script | Metadata over Hex. Kept for the
    /// upcoming layout templates.
    /// </summary>
    public IRootDock CreateClassicLayout(Dictionary<string, IDockable> p)
    {
        var left = NewToolDock("LeftDock", CreateList(p[BlocksId]));
        left.Proportion = 0.21;
        left.Alignment = Alignment.Left;

        var documents = NewDocumentDock("Documents", CreateList(p[RoomId], p[CostumeId], p[ScriptId]));

        var metadataDock = NewToolDock("MetadataDock", CreateList(p[MetadataId]));
        metadataDock.Proportion = 0.35;
        metadataDock.Alignment = Alignment.Right;

        var hexDock = NewToolDock("HexDock", CreateList(p[HexId]));
        hexDock.Alignment = Alignment.Right;

        var right = new ProportionalDock
        {
            Id = "RightDock",
            Proportion = 0.33,
            Orientation = Orientation.Vertical,
            VisibleDockables = CreateList<IDockable>(metadataDock, new ProportionalDockSplitter(), hexDock),
        };

        var mainDock = new ProportionalDock
        {
            Id = "MainDock",
            Orientation = Orientation.Horizontal,
            VisibleDockables = CreateList<IDockable>(left, new ProportionalDockSplitter(), documents, new ProportionalDockSplitter(), right),
        };

        return CreateMainRoot(mainDock);
    }

    public override void InitLayout(IDockable layout)
    {
        DefaultHostWindowLocator = () => new HostWindow();
        base.InitLayout(layout);
    }

    /// <summary>Applies side-panel sizing to real drops (the preview factory does the same for previews).</summary>
    public override void OnDockableDocked(IDockable dockable, DockOperation operation)
    {
        base.OnDockableDocked(dockable, operation);
        EdgeDockSizing.Apply(dockable, operation);
    }

    /// <summary>
    /// Dock simulates a drop on a cloned layout to draw the preview rectangle. The base Factory only
    /// supplies a preview factory for itself (not subclasses), so without this the preview falls back
    /// to a plain half-split that doesn't match the real result.
    /// </summary>
    public override IFactory CreatePreviewFactory() => new PreviewFactory();

    private sealed class PreviewFactory : Factory
    {
        public override void OnDockableDocked(IDockable dockable, DockOperation operation)
        {
            base.OnDockableDocked(dockable, operation);
            EdgeDockSizing.Apply(dockable, operation);
        }
    }

    /// <summary>Brings a panel to the front, wherever it is docked or floating.</summary>
    public void Activate(string id, IRootDock layout)
    {
        if (layout == null) return;
        var dockable = Find(d => d.Id == id).FirstOrDefault();
        if (dockable == null) return;
        SetActiveDockable(dockable);
        if (dockable.Owner is IDock owner) SetFocusedDockable(owner, dockable);
    }
}

/// <summary>
/// Dock reserves half the space for every split. Docking beside the documents area should give a
/// side-panel-sized share instead (matching the default side panels); other splits get an equal share.
/// </summary>
public static class EdgeDockSizing
{
    public const double SideProportion = 0.22;

    public static void Apply(IDockable dockable, DockOperation operation)
    {
        if (dockable == null || operation is not (DockOperation.Left or DockOperation.Right or DockOperation.Top or DockOperation.Bottom))
            return;

        var orientation = operation is DockOperation.Left or DockOperation.Right ? Orientation.Horizontal : Orientation.Vertical;

        // Walk up to the first proportional dock along the split axis that actually holds siblings.
        IDockable split = dockable;
        while (split.Owner is IDockable parent)
        {
            if (parent is IProportionalDock pd && pd.Orientation == orientation && ResizeSplit(pd, split))
                return;
            split = parent;
        }
    }

    private static bool ResizeSplit(IProportionalDock parent, IDockable split)
    {
        var panes = parent.VisibleDockables?.Where(d => d is not IProportionalDockSplitter).ToList();
        if (panes == null || panes.Count < 2 || !panes.Contains(split)) return false;

        bool besideDocuments = panes.Any(p => !ReferenceEquals(p, split) && ContainsDocuments(p));
        double share = besideDocuments ? SideProportion : 1.0 / panes.Count;

        var others = panes.Where(p => !ReferenceEquals(p, split)).ToList();
        double othersTotal = others.Sum(p => double.IsFinite(p.Proportion) && p.Proportion > 0 ? p.Proportion : 0);

        Set(split, share);
        foreach (var p in others)
        {
            double current = !double.IsFinite(p.Proportion) || p.Proportion <= 0 || othersTotal <= 0
                ? 1.0 / others.Count
                : p.Proportion / othersTotal;
            Set(p, current * (1 - share));
        }
        return true;
    }

    // Mirror Dock's own split sizing, which keeps CollapsedProportion in step with Proportion.
    private static void Set(IDockable d, double value)
    {
        d.Proportion = value;
        d.CollapsedProportion = value;
    }

    private static bool ContainsDocuments(IDockable dockable) => dockable switch
    {
        IDocumentDock => true,
        IDock dock when dock.VisibleDockables != null => dock.VisibleDockables.Any(ContainsDocuments),
        _ => false,
    };
}
