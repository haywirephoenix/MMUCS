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
        public double CollapsedWindowHeight = double.NaN;
        public bool RestoreOnNormal;
        public double SavedWindowMinHeight;
        public EventHandler<AvaloniaPropertyChangedEventArgs> WindowStateHandler;
    }

    private static readonly ConditionalWeakTable<IDock, State> States = new();

    /// <summary>
    /// The share to persist for <paramref name="dockable"/>: its remembered expanded share when it is
    /// collapsed in a stack (its live proportion is just the title bar), otherwise its proportion.
    /// </summary>
    public static double GetPersistedProportion(IDockable dockable) =>
        dockable is IDock dock && States.TryGetValue(dock, out var s) && s.Collapsed
            && s.Mode == Mode.VerticalStack && double.IsFinite(s.ExpandedShare)
            ? s.ExpandedShare
            : dockable.Proportion;

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

        // The chrome is re-templated when its panel moves (floated, re-docked). A collapsed panel
        // expands on the move: its saved sizes belonged to the old location.
        if (chrome.DataContext is IDock dock && States.TryGetValue(dock, out var state) && state.Collapsed)
            ResetOnMove(dock, state);
        ToolTip.SetTip(button, "Collapse");
    }

    private static void ResetOnMove(IDock dock, State state)
    {
        state.Collapsed = false;
        if (state.Mode == Mode.VerticalStack)
        {
            dock.MinHeight = state.SavedMinHeight;
            dock.MaxHeight = state.SavedMaxHeight;
        }
        if (state.Window != null)
        {
            state.Window.MinHeight = state.SavedWindowMinHeight;
            if (state.WindowStateHandler != null) state.Window.PropertyChanged -= state.WindowStateHandler;
        }
        state.Window = null;
        state.WindowStateHandler = null;
        state.SavedWindowHeight = double.NaN;
        state.CollapsedWindowHeight = double.NaN;
        state.RestoreOnNormal = false;
        // A fresh chrome's content presenter is visible by default, so no visuals to undo.
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
                state.SavedWindowHeight = window.Bounds.Height;
                state.SavedWindowMinHeight = window.MinHeight;
                double contentHeight = Math.Max(0, chrome.Bounds.Height - header);
                window.MinHeight = 0;
                state.CollapsedWindowHeight = Math.Max(header, window.Bounds.Height - contentHeight);
                window.Height = state.CollapsedWindowHeight;

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
                if (!fromMaximize) RestoreWindowHeight(state);
                break;
            }
        }

        ApplyVisuals(chrome, false);
    }

    private static void OnWindowStateChanged(ToolChromeControl chrome, IDock dock, State state, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.Property != Window.WindowStateProperty || state.Window == null) return;
        var window = state.Window;
        var ws = window.WindowState;
        double height = state.SavedWindowHeight;

        if (state.Collapsed && ws == WindowState.Maximized)
        {
            // Maximizing a collapsed floating window expands it. The OS has already stored the
            // collapsed size as the restore size, which the restore button, dragging the title bar,
            // double-click and Win+Down would all return to.
            double grow = height - state.CollapsedWindowHeight;
            Expand(chrome, dock, state, fromMaximize: true);
            DetachWindow(state);

            // Windows: rewrite the stored restore size in place — no visible change.
            if (double.IsFinite(grow) && WindowRestoreBounds.TryAdjustRestoreHeight(window, grow))
                return;

            // Elsewhere: briefly drop back to normal, set the height, and maximize again. Each step
            // waits for the previous state change to settle so the old bounds aren't re-applied.
            Dispatcher.UIThread.Post(() =>
            {
                window.WindowState = WindowState.Normal;
                DispatcherTimer.RunOnce(() =>
                {
                    if (double.IsFinite(height)) SetWindowHeight(window, height);
                    DispatcherTimer.RunOnce(() => window.WindowState = WindowState.Maximized, TimeSpan.FromMilliseconds(40));
                }, TimeSpan.FromMilliseconds(40));
            }, DispatcherPriority.Background);
        }
        else if (state.Collapsed && ws == WindowState.FullScreen)
        {
            // Full screen restores from Avalonia's own saved bounds and has no drag-to-restore, so
            // simply put the height back when it returns to normal.
            Expand(chrome, dock, state, fromMaximize: true);
            state.RestoreOnNormal = true;
        }
        else if (!state.Collapsed && state.RestoreOnNormal && ws == WindowState.Normal)
        {
            state.RestoreOnNormal = false;
            DetachWindow(state);
            if (!double.IsFinite(height)) return;
            // Avalonia applies its saved (collapsed) bounds just after this event.
            DispatcherTimer.RunOnce(() =>
            {
                if (window.WindowState == WindowState.Normal) SetWindowHeight(window, height);
            }, TimeSpan.FromMilliseconds(50));
        }
    }

    private static void DetachWindow(State state)
    {
        if (state.Window != null && state.WindowStateHandler != null)
            state.Window.PropertyChanged -= state.WindowStateHandler;
        state.WindowStateHandler = null;
        state.SavedWindowHeight = double.NaN;
        state.CollapsedWindowHeight = double.NaN;
        state.Window = null;
    }

    /// <summary>
    /// Sets the window height even when the Height property already holds that value but the real
    /// size differs (as after the OS restores a maximized window to stale bounds).
    /// </summary>
    private static void SetWindowHeight(Window window, double height)
    {
        if (Math.Abs(window.Bounds.Height - height) <= 1) return;
        if (Math.Abs(window.Height - height) <= 0.5) window.Height = height + 1;
        window.Height = height;
    }

    private static void RestoreWindowHeight(State state)
    {
        var window = state.Window;
        if (window == null) return;
        if (double.IsFinite(state.SavedWindowHeight) && window.WindowState == WindowState.Normal)
            SetWindowHeight(window, state.SavedWindowHeight);
        DetachWindow(state);
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
