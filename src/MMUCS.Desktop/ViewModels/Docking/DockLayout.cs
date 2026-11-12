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
public sealed class MmucsDockFactory(MainWindowViewModel main) : Factory, ILayoutBuilder
{
    public const string BlocksId = "Blocks", MetadataId = "Metadata", HexId = "Hex";
    public const string RoomId = "Room", CostumeId = "Costume", ScriptId = "Script";

    public static readonly string[] PanelIds = { BlocksId, MetadataId, HexId, RoomId, CostumeId, ScriptId };
    public static readonly string[] ViewerIds = { RoomId, CostumeId, ScriptId };

    /// <summary>The panels of the current layout, placed or hidden (set whenever panels are created).</summary>
    public Dictionary<string, IDockable> Panels { get; private set; } = new();

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

        _hiddenAt.Clear();
        return Panels = new Dictionary<string, IDockable>
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


    /// <summary>Brings a panel to the front, wherever it is docked or floating (no-op when hidden).</summary>
    public void Activate(string id, IRootDock layout)
    {
        if (FindPanel(id, layout) is not { } dockable) return;
        SetActiveDockable(dockable);
        if (dockable.Owner is IDock owner) SetFocusedDockable(owner, dockable);
    }

    /// <summary>The panel where it is placed: in the main window or a floating one; null when hidden.</summary>
    public IDockable FindPanel(string id, IRootDock layout)
    {
        if (layout == null) return null;
        if (FindIn(layout, d => d.Id == id && d is not IDock) is { } found) return found;
        foreach (var w in layout.Windows ?? Enumerable.Empty<IDockWindow>())
            if (w.Layout != null && FindIn(w.Layout, d => d.Id == id && d is not IDock) is { } inWindow) return inWindow;
        return null;
    }

    private static IDockable FindIn(IDockable node, System.Func<IDockable, bool> match)
    {
        if (match(node)) return node;
        if (node is IDock dock && dock.VisibleDockables != null)
            foreach (var child in dock.VisibleDockables)
                if (FindIn(child, match) is { } found) return found;
        return null;
    }

    public bool IsPanelOpen(string id, IRootDock layout) => FindPanel(id, layout) != null;

    /// <summary>Removes a panel from the layout; a floating window left empty is closed.</summary>
    public void HidePanel(string id, IRootDock layout)
    {
        if (FindPanel(id, layout) is not { } panel) return;
        // The dock stays (empty docks collapse to nothing), so the panel can go back into it.
        if (panel.Owner is IDock dock) _hiddenAt[id] = new Placement(dock, dock.VisibleDockables?.IndexOf(panel) ?? 0);
        RemoveDockable(panel, false);
        foreach (var w in (layout.Windows ?? Enumerable.Empty<IDockWindow>()).ToList())
            if (w.Layout == null || FindIn(w.Layout, d => d is not IDock && d is not IProportionalDockSplitter) == null)
                w.Exit();
    }

    /// <summary>Where a hidden panel was: the dock it left (kept, empty and collapsed, while it's hidden).</summary>
    private sealed record Placement(IDock Dock, int Index);

    private readonly Dictionary<string, Placement> _hiddenAt = new();

    /// <summary>The hidden panels that go back into <paramref name="dock"/> (saved with the layout).</summary>
    public List<string> HiddenIn(IDock dock) =>
        _hiddenAt.Where(kv => ReferenceEquals(kv.Value.Dock, dock)).OrderBy(kv => kv.Value.Index).Select(kv => kv.Key).ToList();

    /// <summary>Records a hidden panel's dock when a saved layout is restored.</summary>
    public void RememberHidden(string id, IDock dock) => _hiddenAt[id] = new Placement(dock, int.MaxValue);

    private static bool Contains(IRootDock layout, IDockable target)
    {
        if (FindIn(layout, d => ReferenceEquals(d, target)) != null) return true;
        foreach (var w in layout.Windows ?? Enumerable.Empty<IDockWindow>())
            if (w.Layout != null && FindIn(w.Layout, d => ReferenceEquals(d, target)) != null) return true;
        return false;
    }

    /// <summary>Puts a hidden panel back into the dock it was hidden from, if that dock is still in the layout.</summary>
    private bool TryRestorePlacement(string id, IDockable panel, IRootDock layout)
    {
        if (!_hiddenAt.Remove(id, out var at) || !Contains(layout, at.Dock)) return false;
        InsertDockable(at.Dock, panel, System.Math.Clamp(at.Index, 0, at.Dock.VisibleDockables?.Count ?? 0));
        return true;
    }

    /// <summary>
    /// Shows a panel: brings it to the front if placed, otherwise puts it back where it was hidden
    /// from, or (e.g. after its floating window was closed) with panels of its kind (viewers with
    /// the documents, the others with the tool panels), or beside them when none are left.
    /// </summary>
    public void ShowPanel(string id, IRootDock layout)
    {
        if (layout == null) return;
        if (FindPanel(id, layout) == null && Panels.TryGetValue(id, out var panel) && !TryRestorePlacement(id, panel, layout))
        {
            bool viewer = ViewerIds.Contains(id);
            var main = layout.DefaultDockable ?? layout.VisibleDockables?.FirstOrDefault();
            IDock sameKind = main == null ? null : (IDock)(viewer
                ? FindIn(main, d => d is IDocumentDock)
                : FindIn(main, d => d is IToolDock));
            if (sameKind != null)
            {
                AddDockable(sameKind, panel);
            }
            else
            {
                IDock newDock = viewer
                    ? NewDocumentDock($"{id}Dock", CreateList(panel))
                    : NewToolDock($"{id}Dock", CreateList(panel));
                var anchor = main == null ? null : (IDock)FindIn(main, d => d is IToolDock or IDocumentDock);
                if (anchor != null)
                {
                    var op = viewer ? DockOperation.Right : DockOperation.Left;
                    SplitToDock(anchor, newDock, op);
                    EdgeDockSizing.Apply(newDock, op);
                }
                else
                {
                    // Nothing left in the main window: the panel becomes its content.
                    newDock.Proportion = double.NaN;
                    var content = new ProportionalDock { Id = "MainDock", Orientation = Orientation.Horizontal, VisibleDockables = CreateList<IDockable>(newDock) };
                    layout.VisibleDockables = CreateList<IDockable>(content);
                    layout.DefaultDockable = content;
                    layout.ActiveDockable = content;
                    InitDockable(content, layout);
                }
            }
        }
        Activate(id, layout);
    }
}

/// <summary>
/// The factory Dock uses to simulate a drop for its preview rectangle, sizing the result the same
/// way as a real drop. The simulation runs on copies that keep their ids but not their on-screen
/// sizes, so sizes are looked up on the live panels with the same ids.
/// </summary>
public sealed class SizedPreviewFactory(IFactory live) : Factory
{
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

    public override void OnDockableDocked(IDockable dockable, DockOperation operation)
    {
        base.OnDockableDocked(dockable, operation);
        EdgeDockSizing.Apply(dockable, operation, copy => Live(live, copy));
    }

    private static IDockable Live(IFactory factory, IDockable copy)
    {
        if (copy?.Id == null || factory?.DockControls == null) return null;
        int kind = Kind(copy);
        foreach (var control in factory.DockControls)
            if (control.Layout is { } layout && Find(layout, d => d.Id == copy.Id && Kind(d) == kind) is { } found)
                return found;
        return null;
    }

    private static int Kind(IDockable d) => d switch
    {
        IProportionalDock => 1, IToolDock => 2, IDocumentDock => 3, IRootDock => 4, IDock => 5, _ => 0,
    };

    private static IDockable Find(IDockable node, System.Func<IDockable, bool> match)
    {
        if (match(node)) return node;
        if (node is IDock dock && dock.VisibleDockables != null)
            foreach (var child in dock.VisibleDockables)
                if (Find(child, match) is { } found) return found;
        return null;
    }
}

/// <summary>
/// Dock reserves half the space for every split. Instead the new pane takes the size the dragged
/// panel had (its width when docked beside, its height when above or below), so a panel keeps its
/// size when it's moved or re-docked. When that size isn't known: a side-panel share beside the
/// documents area, an equal share elsewhere.
/// </summary>
public static class EdgeDockSizing
{
    public const double SideProportion = 0.22;
    private const double MinShare = 0.05;

    /// <summary>
    /// The share the preview last showed for a drop. By the time the real drop is reported the
    /// dragged panel has been moved into a new dock and its on-screen size is gone, so the drop
    /// reuses what the preview worked out for the same panel and side.
    /// </summary>
    private static (string Id, DockOperation Operation, double Share, long Ticks)? _previewed;

    /// <param name="live">Set for the preview: maps its copies to the live panels that have on-screen sizes.</param>
    public static void Apply(IDockable dockable, DockOperation operation, System.Func<IDockable, IDockable> live = null)
    {
        if (dockable == null || operation is not (DockOperation.Left or DockOperation.Right or DockOperation.Top or DockOperation.Bottom))
            return;

        var orientation = operation is DockOperation.Left or DockOperation.Right ? Orientation.Horizontal : Orientation.Vertical;

        // Walk up to the first proportional dock along the split axis that actually holds siblings.
        IDockable split = dockable;
        while (split.Owner is IDockable parent)
        {
            if (parent is IProportionalDock pd && pd.Orientation == orientation && ResizeSplit(pd, split, dockable, operation, live))
                return;
            split = parent;
        }
    }

    private static bool ResizeSplit(IProportionalDock parent, IDockable split, IDockable dragged, DockOperation operation, System.Func<IDockable, IDockable> live)
    {
        var panes = parent.VisibleDockables?.Where(d => d is not IProportionalDockSplitter).ToList();
        if (panes == null || panes.Count < 2 || !panes.Contains(split)) return false;
        var others = panes.Where(p => !ReferenceEquals(p, split)).ToList();

        // The documents area (the viewers) gives up the space, so side panels keep their size; in a
        // split without one, the pane the new one was dropped beside does.
        int at = panes.IndexOf(split);
        bool before = operation is DockOperation.Left or DockOperation.Top; // new pane sits before its neighbour
        var neighbour = others.FirstOrDefault(ContainsDocuments)
            ?? (before
                ? (at + 1 < panes.Count ? panes[at + 1] : panes[at - 1])
                : (at > 0 ? panes[at - 1] : panes[at + 1]));

        bool horizontal = parent.Orientation == Orientation.Horizontal;
        double? share;
        if (live != null)
        {
            share = DraggedShare(panes, split, dragged, horizontal, live);
            _previewed = share is double v ? (PanelOf(dragged).Id, operation, v, System.DateTime.UtcNow.Ticks) : null;
        }
        else
        {
            share = _previewed is { } last && last.Id == PanelOf(dragged).Id && last.Operation == operation
                    && System.TimeSpan.FromTicks(System.DateTime.UtcNow.Ticks - last.Ticks).TotalSeconds < 30
                ? last.Share
                : DraggedShare(panes, split, dragged, horizontal, d => d);
            _previewed = null;
        }
        bool besideDocuments = others.Any(ContainsDocuments);
        double wanted = share ?? (besideDocuments ? SideProportion : 1.0 / panes.Count);

        // The existing panes' shares from before the split (Dock has already halved the target, or
        // scaled everything, to make room), summing to 1.
        var prior = others.ToDictionary(p => p, p => _beforeSplit != null && _beforeSplit.TryGetValue(p, out var v) ? v : p.Proportion);
        _beforeSplit = null;
        double total = prior.Values.Sum(v => double.IsFinite(v) && v > 0 ? v : 0);
        bool known = prior.Values.All(v => double.IsFinite(v) && v > 0) && total > 0;
        var current = others.ToDictionary(p => p, p => known ? prior[p] / total : 1.0 / others.Count);

        // Take it from the neighbour only, leaving it at least a sliver.
        double available = System.Math.Max(0, current[neighbour] - MinShare);
        double taken = System.Math.Clamp(wanted, MinShare, System.Math.Max(MinShare, available));
        Set(split, taken);
        foreach (var p in others)
        {
            if (!ReferenceEquals(p, neighbour)) { Set(p, current[p]); continue; }
            // The neighbour's side facing the new pane: its end if it comes before it.
            bool facesAtEnd = panes.IndexOf(p) < at;
            ResizeKeepingInner(p, current[p], System.Math.Max(MinShare, current[p] - taken), facesAtEnd, parent.Orientation);
        }
        return true;
    }

    /// <summary>
    /// Changes a pane's share. When the pane is itself a split along the same axis (e.g. the whole
    /// layout after docking at a window edge), its children keep their on-screen size except the one
    /// on the edge facing the change, which grows or shrinks by the difference.
    /// </summary>
    private static void ResizeKeepingInner(IDockable pane, double oldShare, double newShare, bool edgeAtEnd, Orientation orientation)
    {
        Set(pane, newShare);
        if (pane is not IProportionalDock inner || inner.Orientation != orientation || oldShare <= 0 || newShare <= 0) return;
        var kids = inner.VisibleDockables?.Where(d => d is not IProportionalDockSplitter).ToList();
        if (kids == null || kids.Count < 2) return;
        double total = kids.Sum(k => double.IsFinite(k.Proportion) && k.Proportion > 0 ? k.Proportion : 0);
        if (total <= 0 || !kids.All(k => double.IsFinite(k.Proportion) && k.Proportion > 0)) return;

        var edge = edgeAtEnd ? kids[^1] : kids[0];
        double scale = oldShare / newShare, sum = 0;
        foreach (var k in kids)
        {
            if (ReferenceEquals(k, edge)) continue;
            double v = k.Proportion / total * scale;
            Set(k, v);
            sum += v;
        }
        Set(edge, System.Math.Max(MinShare, 1 - sum));
    }

    /// <summary>
    /// The dragged panel's size along the split axis as a share of the space the new pane is cut
    /// from (the other panes' sizes), or null when a size isn't known.
    /// </summary>
    private static double? DraggedShare(List<IDockable> panes, IDockable split, IDockable dragged, bool horizontal, System.Func<IDockable, IDockable> live)
    {
        double? SizeOf(IDockable d)
        {
            if (live(d) is not { } model) return null;
            model.GetVisibleBounds(out _, out _, out double w, out double h);
            double v = horizontal ? w : h;
            return double.IsFinite(v) && v > 1 ? v : null;
        }

        if (DraggedSize(dragged, horizontal, live) is not double size) return null;
        // The split's own size: the other panes' last sizes can be stale (one of them may just have
        // left), the container's isn't. A split Dock has just created has none yet; use its panes then.
        double total = split.Owner is { } container && SizeOf(container) is double whole ? whole : 0;
        if (total <= 0)
            foreach (var p in panes)
            {
                if (ReferenceEquals(p, split)) continue;
                if (SizeOf(p) is not double s) return null;
                total += s;
            }
        if (total <= 0) return null;
        return System.Math.Clamp(size / total, 0.08, 0.85);
    }

    /// <summary>
    /// The size a dragged panel should get: its own pane's size while it has one (it's alone in its
    /// dock), otherwise the size its pane had when it last left one (e.g. before it was tabbed in with
    /// other panels), otherwise its current size.
    /// </summary>
    private static double? DraggedSize(IDockable dragged, bool horizontal, System.Func<IDockable, IDockable> live)
    {
        var panel = PanelOf(dragged);
        static double? Axis(double w, double h, bool horizontal)
        {
            double v = horizontal ? w : h;
            return double.IsFinite(v) && v > 1 ? v : null;
        }

        if (live(panel) is { } model)
        {
            if (model.Owner is IDock own && own.VisibleDockables is { Count: 1 })
            {
                own.GetVisibleBounds(out _, out _, out var ow, out var oh);
                if (Axis(ow, oh, horizontal) is double alone) return alone;
            }
            if (panel.Id != null && PaneSizes.TryGetValue(panel.Id, out var remembered) && Axis(remembered.W, remembered.H, horizontal) is double kept)
                return kept;
            model.GetVisibleBounds(out _, out _, out var w, out var h);
            if (Axis(w, h, horizontal) is double now) return now;
        }
        else if (panel.Id != null && PaneSizes.TryGetValue(panel.Id, out var remembered) && Axis(remembered.W, remembered.H, horizontal) is double kept)
        {
            return kept;
        }
        return null;
    }

    /// <summary>Proportions of the panes around a split target, taken just before Dock splits it.</summary>
    private static Dictionary<IDockable, double> _beforeSplit;

    /// <summary>Called before a split: keeps the target's and its siblings' proportions as they were.</summary>
    public static void BeforeSplit(IDock target)
    {
        _beforeSplit = new Dictionary<IDockable, double>(ReferenceEqualityComparer.Instance);
        if (target == null) return;
        _beforeSplit[target] = target.Proportion;
        if (target.Owner is IDock owner && owner.VisibleDockables != null)
            foreach (var d in owner.VisibleDockables)
                if (d is not IProportionalDockSplitter) _beforeSplit[d] = d.Proportion;
    }

    /// <summary>Last size of each panel's own pane, recorded as it leaves it.</summary>
    private static readonly Dictionary<string, (double W, double H)> PaneSizes = new();

    /// <summary>Called before a panel moves out of <paramref name="dock"/>: if it was alone there, keep that pane's size.</summary>
    public static void RememberPaneSize(IDockable panel, IDock dock)
    {
        if (panel is IDock || panel?.Id == null || dock?.VisibleDockables is not { Count: 1 } || !dock.VisibleDockables.Contains(panel)) return;
        dock.GetVisibleBounds(out _, out _, out var w, out var h);
        if (double.IsFinite(w) && double.IsFinite(h) && w > 1 && h > 1) PaneSizes[panel.Id] = (w, h);
    }

    /// <summary>The panel a drag carries: the dragged panel, or the only panel of a dragged dock.</summary>
    private static IDockable PanelOf(IDockable dragged) =>
        dragged is IDock dock && dock.VisibleDockables is { Count: 1 } && dock.VisibleDockables[0] is not IDock
            ? dock.VisibleDockables[0]
            : dragged;

    /// <summary>
    /// Before an empty dock is removed from its split, its share goes to the documents area (the
    /// viewers) if the split has one, else to the next pane, else the previous, so side panels keep
    /// their size. Dock would otherwise leave a gap that the layout spreads over every pane.
    /// </summary>
    /// <param name="emptied">True for a dock collapsing because it's empty; false for one being moved away.</param>
    public static void GiveSpaceToNeighbour(IDock dock, bool emptied)
    {
        if (dock == null) return;
        if (emptied && (dock is not { IsCollapsable: true } || dock.VisibleDockables is not { Count: 0 })) return;
        if (dock.Owner is not IProportionalDock parent || parent.VisibleDockables is not { } items) return;
        var panes = items.Where(d => d is not IProportionalDockSplitter).ToList();
        int at = panes.IndexOf(dock);
        if (at < 0 || panes.Count < 2) return;

        // The documents area takes the space (side panels keep their size); otherwise the next pane, else the previous.
        var previous = at > 0 ? panes[at - 1] : null;
        var next = at + 1 < panes.Count ? panes[at + 1] : null;
        var neighbour = panes.FirstOrDefault(p => !ReferenceEquals(p, dock) && ContainsDocuments(p)) ?? next ?? previous;

        double total = panes.Sum(p => double.IsFinite(p.Proportion) && p.Proportion > 0 ? p.Proportion : 0);
        bool known = panes.All(p => double.IsFinite(p.Proportion) && p.Proportion > 0) && total > 0;
        if (!known) return; // nothing reliable to redistribute
        foreach (var p in panes)
        {
            if (ReferenceEquals(p, dock)) continue;
            double share = p.Proportion / total;
            if (!ReferenceEquals(p, neighbour)) { Set(p, share); continue; }
            // The neighbour's side facing the leaving dock: its end if it comes before it.
            bool facesAtEnd = panes.IndexOf(p) < at;
            ResizeKeepingInner(p, share, share + dock.Proportion / total, facesAtEnd, parent.Orientation);
        }
        // Handed over: the removal that follows (Dock removes the dock right after) mustn't hand it again.
        // A moved dock gets a fresh share where it lands.
        dock.Proportion = double.NaN;
        dock.CollapsedProportion = double.NaN;
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
