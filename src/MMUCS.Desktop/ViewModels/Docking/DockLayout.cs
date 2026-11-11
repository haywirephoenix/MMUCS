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
/// Builds the default layout (Blocks | Room/Costume/Script | Metadata over Hex) and hosts floating
/// panels in real OS windows. Panels can't be closed (yet), so nothing gets lost; View ▸ Reset
/// layout rebuilds the default arrangement.
/// </summary>
public sealed class MmucsDockFactory(MainWindowViewModel main) : Factory
{
    public const string BlocksId = "Blocks", MetadataId = "Metadata", HexId = "Hex";
    public const string RoomId = "Room", CostumeId = "Costume", ScriptId = "Script";

    public override IRootDock CreateLayout()
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

        var blocks = Panel(new BlocksTool(main), BlocksId, "Blocks");
        var metadata = Panel(new MetadataTool(main), MetadataId, "Metadata");
        var hex = Panel(new HexTool(main), HexId, "Hex");
        var room = Panel(new RoomDocument(main), RoomId, "Room");
        var costume = Panel(new CostumeDocument(main), CostumeId, "Costume");
        var script = Panel(new ScriptDocument(main), ScriptId, "Script");

        var left = new ToolDock
        {
            Id = "LeftDock",
            Proportion = 0.21,
            Alignment = Alignment.Left,
            ActiveDockable = blocks,
            VisibleDockables = CreateList<IDockable>(blocks),
        };

        var documents = new DocumentDock
        {
            Id = "Documents",
            IsCollapsable = false,
            CanCreateDocument = false,
            CloseButtonShowMode = DocumentCloseButtonShowMode.Never,
            ActiveDockable = room,
            VisibleDockables = CreateList<IDockable>(room, costume, script),
        };

        var metadataDock = new ToolDock
        {
            Id = "MetadataDock",
            Proportion = 0.35,
            Alignment = Alignment.Right,
            ActiveDockable = metadata,
            VisibleDockables = CreateList<IDockable>(metadata),
        };

        var hexDock = new ToolDock
        {
            Id = "HexDock",
            Alignment = Alignment.Right,
            ActiveDockable = hex,
            VisibleDockables = CreateList<IDockable>(hex),
        };

        var right = new ProportionalDock
        {
            Id = "RightDock",
            Proportion = 0.33,
            Orientation = Orientation.Vertical,
            VisibleDockables = CreateList<IDockable>(metadataDock, new ProportionalDockSplitter(), hexDock),
        };

        var main_ = new ProportionalDock
        {
            Id = "MainDock",
            Orientation = Orientation.Horizontal,
            VisibleDockables = CreateList<IDockable>(left, new ProportionalDockSplitter(), documents, new ProportionalDockSplitter(), right),
        };

        var root = CreateRootDock();
        root.Id = "Root";
        root.ActiveDockable = main_;
        root.DefaultDockable = main_;
        root.VisibleDockables = CreateList<IDockable>(main_);
        // Torn-off panels become real OS windows (movable to other monitors), like the Godot build.
        root.FloatingWindowHostMode = DockFloatingWindowHostMode.Native;
        return root;
    }

    public override void InitLayout(IDockable layout)
    {
        DefaultHostWindowLocator = () => new HostWindow();
        base.InitLayout(layout);
    }

    /// <summary>Share given to a panel docked beside the documents area (matches the default side panels).</summary>
    public const double SideProportion = 0.22;

    /// <summary>
    /// Dock reserves half the space for every split. Docking to an edge next to the documents area
    /// should give a side-panel-sized share instead; other splits get an equal share.
    /// </summary>
    public override void OnDockableDocked(IDockable dockable, DockOperation operation)
    {
        base.OnDockableDocked(dockable, operation);
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
        double othersTotal = others.Sum(p => double.IsNaN(p.Proportion) ? 0 : p.Proportion);

        split.Proportion = share;
        foreach (var p in others)
        {
            double current = double.IsNaN(p.Proportion) || othersTotal <= 0 ? 1.0 / others.Count : p.Proportion / othersTotal;
            p.Proportion = current * (1 - share);
        }
        return true;
    }

    private static bool ContainsDocuments(IDockable dockable) => dockable switch
    {
        IDocumentDock => true,
        IDock dock when dock.VisibleDockables != null => dock.VisibleDockables.Any(ContainsDocuments),
        _ => false,
    };

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
