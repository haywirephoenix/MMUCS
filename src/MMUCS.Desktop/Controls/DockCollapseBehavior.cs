using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;

namespace MMUCS.Desktop.Controls;

/// <summary>
/// Turns the down-arrow on every tool panel's title bar into an ImGui-style collapse toggle
/// (the panel menu stays available on right-click of the title bar).
///
///  - In a vertical stack the panel is pinned to its title-bar height and expanded neighbours share
///    the rest. Each panel remembers only its own expanded share (with a minimum), and expanding
///    divides the space among whatever is expanded at that moment — so any collapse/expand order
///    recovers sensibly.
///  - A floating window that holds just this panel shrinks to the title bar and restores its height;
///    maximizing it expands the panel.
///  - Otherwise (e.g. a panel filling a whole column) the content is hidden under the title bar,
///    which stays at the top.
/// </summary>
public static class DockCollapseBehavior
{
    /// <summary>Smallest share an expanded panel gets back, so it never re-expands to a sliver.</summary>
    private const double MinExpandedShare = 0.2;
    /// <summary>Nominal share for a collapsed panel; its real size is pinned by Min/MaxHeight.</summary>
    private const double CollapsedShare = 0.02;

    private enum Mode { HideContent, VerticalStack, FloatingWindow }

    private sealed class State
    {
        public bool Collapsed;
        public Mode Mode;
        public double ExpandedShare = double.NaN;
        public double SavedMinHeight, SavedMaxHeight = double.PositiveInfinity;
        public HostWindow Window;
        public double SavedWindowHeight = double.NaN;
        public double SavedWindowMinHeight;
        public bool RestoreHeightOnNormal;
        public EventHandler<AvaloniaPropertyChangedEventArgs> WindowStateHandler;
    }

    private static readonly ConditionalWeakTable<IDock, State> States = new();

    public static void Install()
    {
        TemplatedControl.TemplateAppliedEvent.AddClassHandler<ToolChromeControl>(OnTemplateApplied);
    }

    private static void OnTemplateApplied(ToolChromeControl chrome, TemplateAppliedEventArgs e)
    {
        var button = e.NameScope.Find<Button>("PART_MenuButton");
        if (button == null) return;

        // Local value beats the template binding, so the arrow no longer opens the menu.
        button.Flyout = null;
        button.Click -= OnArrowClick;
        button.Click += OnArrowClick;

        // Re-apply visuals if this dock was collapsed before the chrome was re-templated.
        bool collapsed = chrome.DataContext is IDock dock && States.TryGetValue(dock, out var state) && state.Collapsed;
        if (collapsed) ApplyVisuals(chrome, true);
        ToolTip.SetTip(button, collapsed ? "Expand" : "Collapse");
    }

    private static void OnArrowClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { TemplatedParent: ToolChromeControl chrome }) return;
        if (chrome.DataContext is not IDock dock) return;
        var state = States.GetOrCreateValue(dock);
        if (state.Collapsed) Expand(chrome, dock, state);
        else Collapse(chrome, dock, state);
        e.Handled = true;
    }

    // ── Collapse / expand ────────────────────────────────────────────────────

    private static void Collapse(ToolChromeControl chrome, IDock dock, State state)
    {
        double header = HeaderHeight(chrome);
        state.Collapsed = true;
        state.Mode = VerticalPanes(dock) != null ? Mode.VerticalStack
            : TopLevel.GetTopLevel(chrome) is HostWindow ? Mode.FloatingWindow
            : Mode.HideContent;

        switch (state.Mode)
        {
            case Mode.VerticalStack:
            {
                // Remember this panel's share only while it was actually sharing space; if it was the
                // last one expanded it had the whole stack, which isn't a size worth restoring.
                var panes = VerticalPanes(dock);
                bool sharing = panes.Any(p => !ReferenceEquals(p, dock) && !IsCollapsed(p));
                if (sharing && double.IsFinite(dock.Proportion) && dock.Proportion > CollapsedShare * 2)
                    state.ExpandedShare = dock.Proportion;
                else if (!double.IsFinite(state.ExpandedShare))
                    state.ExpandedShare = 1.0 / panes.Count;
                state.SavedMinHeight = dock.MinHeight;
                state.SavedMaxHeight = dock.MaxHeight;
                dock.MinHeight = header;
                dock.MaxHeight = header;
                Redistribute(dock, CollapsedShare);
                break;
            }
            case Mode.FloatingWindow:
            {
                var window = (HostWindow)TopLevel.GetTopLevel(chrome);
                state.Window = window;
                state.SavedWindowHeight = window.Height;
                state.SavedWindowMinHeight = window.MinHeight;
                double contentHeight = Math.Max(0, chrome.Bounds.Height - header);
                window.MinHeight = 0;
                window.Height = Math.Max(header, window.Bounds.Height - contentHeight);

                // Maximize / full screen expands the panel instead of showing a big empty window.
                state.WindowStateHandler = (_, args) => OnWindowStateChanged(chrome, dock, state, args);
                window.PropertyChanged += state.WindowStateHandler;
                break;
            }
        }

        ApplyVisuals(chrome, true);
    }

    private static void Expand(ToolChromeControl chrome, IDock dock, State state, bool fromMaximize = false)
    {
        state.Collapsed = false;

        switch (state.Mode)
        {
            case Mode.VerticalStack:
            {
                dock.MinHeight = state.SavedMinHeight;
                dock.MaxHeight = state.SavedMaxHeight;
                double share = double.IsFinite(state.ExpandedShare) ? state.ExpandedShare : 0.5;
                Redistribute(dock, Math.Clamp(share, MinExpandedShare, 1.0));
                break;
            }
            case Mode.FloatingWindow when state.Window != null:
            {
                state.Window.MinHeight = state.SavedWindowMinHeight;
                if (fromMaximize)
                    state.RestoreHeightOnNormal = true; // apply when the window comes back to normal
                else
                    RestoreWindowHeight(state);
                break;
            }
        }

        ApplyVisuals(chrome, false);
    }

    private static void OnWindowStateChanged(ToolChromeControl chrome, IDock dock, State state, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.Property != Window.WindowStateProperty || state.Window == null) return;
        var ws = state.Window.WindowState;

        if (state.Collapsed && ws is WindowState.Maximized or WindowState.FullScreen)
        {
            Expand(chrome, dock, state, fromMaximize: true);
        }
        else if (!state.Collapsed && state.RestoreHeightOnNormal && ws == WindowState.Normal)
        {
            // The OS re-applies the window's restore bounds (the collapsed size) after this event,
            // so set the height once that has happened.
            Dispatcher.UIThread.Post(() => RestoreWindowHeight(state), DispatcherPriority.Background);
        }
    }

    private static void RestoreWindowHeight(State state)
    {
        var window = state.Window;
        if (window == null) return;
        if (double.IsFinite(state.SavedWindowHeight) && window.WindowState == WindowState.Normal)
            window.Height = state.SavedWindowHeight;
        if (state.WindowStateHandler != null) window.PropertyChanged -= state.WindowStateHandler;
        state.WindowStateHandler = null;
        state.SavedWindowHeight = double.NaN;
        state.RestoreHeightOnNormal = false;
        state.Window = null;
    }

    // ── Vertical-stack sizing ────────────────────────────────────────────────

    /// <summary>Panes of the vertical proportional dock that owns <paramref name="dock"/>, or null.</summary>
    private static List<IDockable> VerticalPanes(IDock dock)
    {
        if (dock.Owner is not IProportionalDock { Orientation: Orientation.Vertical, VisibleDockables: { } children })
            return null;
        var panes = children.Where(d => d is not IProportionalDockSplitter).ToList();
        return panes.Count >= 2 ? panes : null;
    }

    private static bool IsCollapsed(IDockable d) =>
        d is IDock dock && States.TryGetValue(dock, out var s) && s.Collapsed && s.Mode == Mode.VerticalStack;

    /// <summary>
    /// Gives <paramref name="dock"/> the share <paramref name="target"/>; collapsed neighbours keep a
    /// nominal share and the expanded ones split the remainder in their current ratio.
    /// </summary>
    private static void Redistribute(IDock dock, double target)
    {
        var panes = VerticalPanes(dock);
        if (panes == null) return;

        var collapsed = panes.Where(p => !ReferenceEquals(p, dock) && IsCollapsed(p)).ToList();
        var expanded = panes.Where(p => !ReferenceEquals(p, dock) && !IsCollapsed(p)).ToList();
        double available = 1.0 - collapsed.Count * CollapsedShare;

        foreach (var c in collapsed) Set(c, CollapsedShare);

        if (expanded.Count == 0)
        {
            // Nothing else to share with: take everything that isn't a collapsed title bar.
            Set(dock, Math.Max(CollapsedShare, IsCollapsed(dock) ? CollapsedShare : available));
            return;
        }

        // Expanded neighbours always keep at least the minimum share, so nothing gets swallowed.
        double mine = Math.Min(target, available - expanded.Count * MinExpandedShare);
        mine = Math.Max(mine, Math.Min(target, MinExpandedShare));
        double rest = available - mine;
        double total = expanded.Sum(p => double.IsFinite(p.Proportion) && p.Proportion > 0 ? p.Proportion : 0);
        Set(dock, mine);
        foreach (var p in expanded)
        {
            double ratio = total > 0 && double.IsFinite(p.Proportion) && p.Proportion > 0 ? p.Proportion / total : 1.0 / expanded.Count;
            Set(p, rest * ratio);
        }
    }

    private static void Set(IDockable d, double value)
    {
        d.Proportion = value;
        d.CollapsedProportion = value;
    }

    // ── Visuals ──────────────────────────────────────────────────────────────

    private static void ApplyVisuals(ToolChromeControl chrome, bool collapsed)
    {
        foreach (var child in TemplateParts(chrome))
        {
            switch (child)
            {
                case { Name: "PART_ContentPresenter" } presenter:
                    // Stay in the layout (so the title bar keeps its place at the top) but disappear.
                    presenter.Opacity = collapsed ? 0 : 1;
                    presenter.IsHitTestVisible = !collapsed;
                    presenter.IsEnabled = !collapsed;
                    break;
                case Path { Name: "PART_MenuPath" } path:
                    // ▾ expanded, ▸ collapsed (ImGui-style).
                    path.RenderTransformOrigin = RelativePoint.Center;
                    path.RenderTransform = collapsed ? new RotateTransform(-90) : null;
                    break;
                case Button { Name: "PART_MenuButton" } button:
                    ToolTip.SetTip(button, collapsed ? "Expand" : "Collapse");
                    break;
            }
        }
    }

    /// <summary>Named parts of the chrome's own template (not of nested panels' templates).</summary>
    private static IEnumerable<Control> TemplateParts(ToolChromeControl chrome) =>
        chrome.GetVisualDescendants().OfType<Control>().Where(c => ReferenceEquals(c.TemplatedParent, chrome));

    private static double HeaderHeight(ToolChromeControl chrome)
    {
        foreach (var child in TemplateParts(chrome))
            if (child is Border { Name: "PART_Border" } border && border.Bounds.Height > 0)
                return Math.Ceiling(border.Bounds.Height) + 1;
        return 26;
    }
}
