using Dock.Model;
using Dock.Model.Controls;
using Dock.Model.Core;
using MMUCS.Desktop.ViewModels;
using MMUCS.Desktop.ViewModels.Docking;
using Xunit;
using Xunit.Abstractions;

namespace MMUCS.Desktop.Tests;

/// <summary>
/// Drags inside the Costume viewer, run through Dock's own DockManager / DockService as a real drop
/// is. Between steps a stand-in layout pass gives every pane the on-screen size its proportion
/// implies, since the sizing code reads those sizes like it does in the app.
/// </summary>
public class CostumeDockTests(ITestOutputHelper output)
{
    private const double Width = 1000, Height = 600;

    [Fact]
    public void Cels_tabbed_into_chores_then_docked_back_left_restores_the_layout()
    {
        var factory = new CostumeDockFactory(new AkosViewModel());
        var root = factory.CreateLayout();
        factory.InitLayout(root);
        LayoutPass(root);
        var before = Widths(root);
        output.WriteLine("start:       " + Describe(root.DefaultDockable));

        var manager = new DockManager(new DockService());
        var cels = Find(root, CostumeDockFactory.CelsId);
        var chores = Find(root, CostumeDockFactory.ChoresId);
        var preview = Find(root, CostumeDockFactory.PreviewId);

        // Drag the Cels tab onto the middle of Chores: it joins that dock as a tab.
        Assert.True(manager.ValidateDockable(cels, chores.Owner, DragAction.Move, DockOperation.Fill, bExecute: true));
        LayoutPass(root);
        output.WriteLine("tabbed:      " + Describe(root.DefaultDockable));
        Assert.Same(chores.Owner, cels.Owner);

        // Drag it back to the left edge of the preview.
        Assert.True(manager.ValidateDockable(cels, preview.Owner, DragAction.Move, DockOperation.Left, bExecute: true));
        LayoutPass(root);
        output.WriteLine("docked back: " + Describe(root.DefaultDockable));

        var after = Widths(root);
        foreach (var id in new[] { CostumeDockFactory.CelsId, CostumeDockFactory.PreviewId, CostumeDockFactory.ChoresId })
            Assert.True(System.Math.Abs(before[id] - after[id]) < Width * 0.01,
                $"{id}: {before[id]:0} px before, {after[id]:0} px after");
    }

    [Fact]
    public void Cels_docked_right_of_chores_then_back_left_restores_the_layout()
    {
        var factory = new CostumeDockFactory(new AkosViewModel());
        var root = factory.CreateLayout();
        factory.InitLayout(root);
        LayoutPass(root);
        var before = Widths(root);
        output.WriteLine("start:       " + Describe(root.DefaultDockable));

        var manager = new DockManager(new DockService());
        var cels = Find(root, CostumeDockFactory.CelsId);
        var chores = Find(root, CostumeDockFactory.ChoresId);
        var preview = Find(root, CostumeDockFactory.PreviewId);

        // Split to the right of the Chores dock: Cels gets its own pane there, at its own width.
        Assert.True(manager.ValidateDockable(cels, chores.Owner, DragAction.Move, DockOperation.Right, bExecute: true));
        LayoutPass(root);
        output.WriteLine("right:       " + Describe(root.DefaultDockable));
        var moved = Widths(root);
        Assert.True(System.Math.Abs(moved[CostumeDockFactory.CelsId] - before[CostumeDockFactory.CelsId]) < Width * 0.01,
            $"Cels should keep its width on the right: {moved[CostumeDockFactory.CelsId]:0} px");

        Assert.True(manager.ValidateDockable(cels, preview.Owner, DragAction.Move, DockOperation.Left, bExecute: true));
        LayoutPass(root);
        output.WriteLine("docked back: " + Describe(root.DefaultDockable));

        var after = Widths(root);
        foreach (var id in new[] { CostumeDockFactory.CelsId, CostumeDockFactory.PreviewId, CostumeDockFactory.ChoresId })
            Assert.True(System.Math.Abs(before[id] - after[id]) < Width * 0.01,
                $"{id}: {before[id]:0} px before, {after[id]:0} px after");
    }

    // ── Helpers ──

    private static IDockable Find(IDockable node, string id)
    {
        if (node.Id == id && node is not IDock) return node;
        if (node is IDock dock && dock.VisibleDockables != null)
            foreach (var child in dock.VisibleDockables)
                if (Find(child, id) is { } found) return found;
        return null;
    }

    /// <summary>Width of the pane holding each panel.</summary>
    private static Dictionary<string, double> Widths(IRootDock root)
    {
        var widths = new Dictionary<string, double>();
        foreach (var id in CostumeDockFactory.PanelIds)
        {
            var panel = Find(root, id);
            panel.Owner.GetVisibleBounds(out _, out _, out var w, out _);
            widths[id] = w;
        }
        return widths;
    }

    /// <summary>Gives every dockable bounds from the proportions, like the ProportionalStackPanel does.</summary>
    private static void LayoutPass(IRootDock root)
    {
        root.SetVisibleBounds(0, 0, Width, Height);
        var content = root.DefaultDockable ?? root.ActiveDockable;
        Place(content, 0, 0, Width, Height);
    }

    private static void Place(IDockable d, double x, double y, double w, double h)
    {
        d.SetVisibleBounds(x, y, w, h);
        if (d is IProportionalDock split && split.VisibleDockables != null)
        {
            var panes = split.VisibleDockables.Where(c => c is not IProportionalDockSplitter).ToList();
            double total = panes.Sum(p => double.IsFinite(p.Proportion) && p.Proportion > 0 ? p.Proportion : 0);
            int unset = panes.Count(p => !double.IsFinite(p.Proportion) || p.Proportion <= 0);
            double fill = unset > 0 ? System.Math.Max(0, 1 - total) / unset : 0;
            double sum = total + fill * unset;
            bool horizontal = split.Orientation == Orientation.Horizontal;
            double at = horizontal ? x : y;
            foreach (var p in panes)
            {
                double share = (double.IsFinite(p.Proportion) && p.Proportion > 0 ? p.Proportion : fill) / (sum > 0 ? sum : 1);
                double size = (horizontal ? w : h) * share;
                if (horizontal) Place(p, at, y, size, h); else Place(p, x, at, w, size);
                at += size;
            }
        }
        else if (d is IDock dock && dock.VisibleDockables != null)
        {
            foreach (var child in dock.VisibleDockables) Place(child, x, y, w, h);
        }
    }

    private static string Describe(IDockable d) => d switch
    {
        IProportionalDock p => $"[{string.Join(" | ", p.VisibleDockables.Where(c => c is not IProportionalDockSplitter).Select(Describe))}]",
        IDock dock => $"{string.Join("+", dock.VisibleDockables.Select(v => v.Id))} {Size(d)}",
        _ => d.Id,
    };

    private static string Size(IDockable d)
    {
        d.GetVisibleBounds(out _, out _, out var w, out _);
        return $"{w:0}px (p={d.Proportion:0.###})";
    }
}
