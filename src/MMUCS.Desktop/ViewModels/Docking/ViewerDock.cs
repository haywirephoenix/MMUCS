using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;
using Dock.Model.Mvvm.Core;

namespace MMUCS.Desktop.ViewModels.Docking;

/// <summary>One panel of a viewer's own docking area.</summary>
public sealed record ViewerPanel(string Id, string Title, DockableBase Dockable);

/// <summary>
/// Docking inside a viewer (Costume, Object, Room): the viewer's own panels, usually a list or two
/// around a Preview document. Everything carries the viewer's <see cref="Group"/>, so its panels
/// rearrange among themselves but can't be dropped into the main layout (or another viewer), and main
/// panels can't be dropped in here. They don't float or close; a viewer can hide one
/// (<see cref="SetPanelShown"/>), which keeps its dock so it goes back where it was.
/// <para>
/// A viewer says which panels it has (<see cref="DefinePanels"/>), its built-in arrangement
/// (<see cref="CreateBuiltInContent"/>) and where its section of layout.default.json is
/// (<see cref="DefaultNode"/>); the rest is shared.
/// </para>
/// </summary>
public abstract class ViewerDockFactory : Factory, ILayoutBuilder
{
    protected ViewerDockFactory(string group, string name)
    {
        Group = group;
        Name = name;
    }

    /// <summary>The dock group every dockable of this viewer carries.</summary>
    public string Group { get; }

    /// <summary>Prefix of the root and main split ids ("Costume" → CostumeRoot, CostumeMain).</summary>
    public string Name { get; }

    /// <summary>Every panel's id; a restored layout must place (or hide) each one.</summary>
    public abstract IReadOnlyList<string> Ids { get; }

    /// <summary>Fresh instances of the viewer's panels.</summary>
    protected abstract IEnumerable<ViewerPanel> DefinePanels();

    /// <summary>The built-in arrangement of <paramref name="p"/> (by id), when the default file has none.</summary>
    protected abstract IDockable CreateBuiltInContent(Dictionary<string, IDockable> p);

    /// <summary>This viewer's section of a layout file.</summary>
    public abstract DockLayoutStore.Node DefaultNode(DockLayoutStore.LayoutFile file);

    /// <summary>The panels of the current layout, placed or hidden (set whenever panels are created).</summary>
    public Dictionary<string, IDockable> Panels { get; private set; } = new();

    public Dictionary<string, IDockable> CreatePanels()
    {
        _hiddenAt.Clear();
        Panels = DefinePanels().ToDictionary(p => p.Id, p =>
        {
            var d = p.Dockable;
            d.Id = p.Id;
            d.Title = p.Title;
            d.CanClose = false;
            d.CanFloat = false;
            d.CanPin = false;
            d.DockGroup = Group;
            return (IDockable)d;
        });
        return Panels;
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
        IsCollapsable = true, // emptied, it collapses and its neighbours take the space
        CanCreateDocument = false,
        CloseButtonShowMode = DocumentCloseButtonShowMode.Never,
        ActiveDockable = active ?? panels.FirstOrDefault(),
        VisibleDockables = panels,
    };

    /// <summary>A split of panes (with splitters between them), in this viewer's group.</summary>
    protected ProportionalDock Split(string id, Orientation orientation, params IDockable[] panes)
    {
        var items = new List<IDockable>();
        for (int i = 0; i < panes.Length; i++)
        {
            if (i > 0) items.Add(new ProportionalDockSplitter());
            items.Add(panes[i]);
        }
        return new ProportionalDock
        {
            Id = id,
            DockGroup = Group,
            CanFloat = false,
            Orientation = orientation,
            VisibleDockables = CreateList(items.ToArray()),
        };
    }

    public IRootDock WrapRoot(IDockable content)
    {
        var root = CreateRootDock();
        root.Id = Name + "Root";
        root.DockGroup = Group;
        root.CanFloat = false;
        root.ActiveDockable = content;
        root.DefaultDockable = content;
        root.VisibleDockables = CreateList<IDockable>(content);
        return root;
    }

    /// <summary>The default arrangement: this viewer's section of layout.default.json, or the built-in one.</summary>
    public override IRootDock CreateLayout()
    {
        var file = DockLayoutStore.ReadDefault();
        return (file == null ? null : TryRestore(DefaultNode(file))) ?? CreateBuiltInLayout();
    }

    public IRootDock CreateBuiltInLayout() => WrapRoot(CreateBuiltInContent(CreatePanels()));

    /// <summary>Rebuilds a saved arrangement; null when it doesn't hold exactly this viewer's panels.</summary>
    public IRootDock TryRestore(DockLayoutStore.Node node)
    {
        if (node == null) return null;
        var panels = CreatePanels();
        var used = new HashSet<string>();
        var content = DockLayoutStore.Build(this, node, panels, used);
        if (content == null || !Ids.All(id => used.Contains(id) || _hiddenAt.ContainsKey(id)))
        {
            _hiddenAt.Clear();
            return null;
        }
        SetGroup(content);
        return WrapRoot(content);
    }

    /// <summary>Restored splits are created without a group; the whole tree needs it.</summary>
    private void SetGroup(IDockable d)
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

    // ── Hiding a panel ───────────────────────────────────────────────────────

    /// <summary>Where a hidden panel was: the dock it left (kept, empty and collapsed, while it's hidden).</summary>
    private sealed record Placement(IDock Dock, int Index);

    private readonly Dictionary<string, Placement> _hiddenAt = new();

    /// <summary>The hidden panels that go back into <paramref name="dock"/> (saved with the layout).</summary>
    public List<string> HiddenIn(IDock dock) =>
        _hiddenAt.Where(kv => ReferenceEquals(kv.Value.Dock, dock)).OrderBy(kv => kv.Value.Index).Select(kv => kv.Key).ToList();

    /// <summary>Records a hidden panel's dock when a saved layout is restored.</summary>
    public void RememberHidden(string id, IDock dock) => _hiddenAt[id] = new Placement(dock, int.MaxValue);

    public bool IsPanelShown(string id, IRootDock layout) => layout != null && Find(layout, d => d.Id == id && d is not IDock) != null;

    /// <summary>
    /// Hides a panel (its dock stays, collapsed, so it can go back) or shows it again: in the dock it was
    /// hidden from, else with the other tool panels, else beside the preview.
    /// </summary>
    public void SetPanelShown(string id, bool shown, IRootDock layout)
    {
        if (layout == null) return;
        var placed = Find(layout, d => d.Id == id && d is not IDock);
        if (!shown)
        {
            if (placed == null) return;
            if (placed.Owner is IDock owner) _hiddenAt[id] = new Placement(owner, owner.VisibleDockables?.IndexOf(placed) ?? 0);
            RemoveDockable(placed, false);
            return;
        }
        if (placed != null || !Panels.TryGetValue(id, out var panel)) return;
        if (_hiddenAt.Remove(id, out var at) && Find(layout, d => ReferenceEquals(d, at.Dock)) != null)
        {
            InsertDockable(at.Dock, panel, Math.Clamp(at.Index, 0, at.Dock.VisibleDockables?.Count ?? 0));
            at.Dock.ActiveDockable = panel;
            return;
        }
        if (Find(layout, d => d is IToolDock) is IDock tools)
        {
            AddDockable(tools, panel);
            tools.ActiveDockable = panel;
            return;
        }
        if (Find(layout, d => d is IDocumentDock) is IDock documents)
        {
            var dock = NewToolDock(id + "Dock", CreateList(panel));
            SplitToDock(documents, dock, DockOperation.Right);
            EdgeDockSizing.Apply(dock, DockOperation.Right);
        }
    }

    private static IDockable Find(IDockable node, Func<IDockable, bool> match)
    {
        if (match(node)) return node;
        if (node is IDock dock && dock.VisibleDockables != null)
            foreach (var child in dock.VisibleDockables)
                if (Find(child, match) is { } found) return found;
        return null;
    }

    // ── Shared drag behaviour ────────────────────────────────────────────────

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
        // A dock kept for a hidden panel stays in the tree so the panel can go back into it.
        if (_hiddenAt.Values.Any(p => ReferenceEquals(p.Dock, dock))) return;
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

/// <summary>
/// A viewer's docking area as its view shows it: the factory and the current layout, which Layout ▸
/// Reset and a restored layout replace. The view (Views/ViewerDockView) binds to <see cref="Layout"/>.
/// </summary>
public sealed partial class ViewerDock : ObservableObject
{
    /// <summary>
    /// Whether the viewers' docks show their tool title bars and document tabs (all viewers at once).
    /// Hidden saves space; shown, every panel has a handle to drag it by.
    /// </summary>
    public static bool ShowHeaders
    {
        get => _showHeaders;
        set
        {
            if (_showHeaders == value) return;
            _showHeaders = value;
            ShowHeadersChanged?.Invoke();
        }
    }
    private static bool _showHeaders;

    public static event Action ShowHeadersChanged;

    public ViewerDock(ViewerDockFactory factory)
    {
        Factory = factory;
        UseLayout(factory.CreateLayout());
    }

    public ViewerDockFactory Factory { get; }

    [ObservableProperty] private IRootDock _layout;

    /// <summary>Raised after a new layout is in place (restored or reset), so the viewer can re-apply hidden panels.</summary>
    public event Action LayoutReplaced;

    /// <summary>Applies a saved arrangement of the viewer's panels (the default if it doesn't fit).</summary>
    public void Restore(DockLayoutStore.Node saved) => UseLayout(Factory.TryRestore(saved) ?? Factory.CreateLayout());

    public void Reset() => UseLayout(Factory.CreateLayout());

    public bool IsPanelShown(string id) => Factory.IsPanelShown(id, Layout);

    public void SetPanelShown(string id, bool shown) => Factory.SetPanelShown(id, shown, Layout);

    private void UseLayout(IRootDock layout)
    {
        Factory.InitLayout(layout);
        Layout = layout;
        LayoutReplaced?.Invoke();
    }
}
