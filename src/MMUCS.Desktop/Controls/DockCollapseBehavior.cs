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
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;

namespace MMUCS.Desktop.Controls;

/// <summary>
/// Turns the down-arrow on every tool panel's title bar into an ImGui-style collapse toggle
/// (the panel menu stays available on right-click of the title bar).
///
///  - In a vertical stack the panel shrinks to its title bar and neighbours take the space
///    (via the dock's MaxHeight, which Dock's proportional panel honours). Expanding restores the
///    proportions the panel and its neighbours had before collapsing.
///  - A floating window shrinks to its title bar and restores its previous height; maximizing a
///    collapsed floating window expands it.
///  - Otherwise (e.g. a panel filling a whole column) the content is hidden under the title bar.
///
/// State is kept per dock model, so it survives the chrome being re-templated when a panel is
/// moved, floated or re-docked.
/// </summary>
public static class DockCollapseBehavior
{
    private sealed class State
    {
        public bool Collapsed;
        public double SavedMaxHeight = double.PositiveInfinity;
        public Dictionary<IDockable, double> SavedProportions;
        public double SavedWindowHeight = double.NaN;
        public double SavedWindowMinHeight;
        public HostWindow Window;
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

        // Re-apply visuals if this dock was collapsed before the chrome was re-templated
        // (template parts already exist at this point).
        bool collapsed = chrome.DataContext is IDock dock && States.TryGetValue(dock, out var state) && state.Collapsed;
        if (collapsed) ApplyVisuals(chrome, true);
        ToolTip.SetTip(button, collapsed ? "Expand" : "Collapse");
    }

    private static void OnArrowClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { TemplatedParent: ToolChromeControl chrome }) return;
        if (chrome.DataContext is not IDock dock) return;
        var state = States.GetOrCreateValue(dock);
        if (state.Collapsed) Expand(chrome, dock, state, restoreWindowHeight: true);
        else Collapse(chrome, dock, state);
        e.Handled = true;
    }

    private static void Collapse(ToolChromeControl chrome, IDock dock, State state)
    {
        double header = HeaderHeight(chrome);
        state.Collapsed = true;

        // Remember this panel's and its neighbours' proportions; the layout writes the squashed
        // sizes back into them while collapsed.
        state.SavedProportions = dock.Owner is IDock owner && owner.VisibleDockables != null
            ? owner.VisibleDockables.Where(d => d is not IProportionalDockSplitter).ToDictionary(d => d, d => d.Proportion)
            : null;

        state.SavedMaxHeight = dock.MaxHeight;
        dock.MaxHeight = header;

        if (TopLevel.GetTopLevel(chrome) is HostWindow window)
        {
            // Shrink the floating window by the height the content was using.
            state.Window = window;
            state.SavedWindowHeight = window.Height;
            state.SavedWindowMinHeight = window.MinHeight;
            double contentHeight = Math.Max(0, chrome.Bounds.Height - header);
            window.MinHeight = 0;
            window.Height = Math.Max(header, window.Bounds.Height - contentHeight);

            // Maximize / full screen expands the panel instead of showing a big empty window.
            state.WindowStateHandler = (_, args) =>
            {
                if (args.Property != Window.WindowStateProperty) return;
                if (state.Collapsed && window.WindowState != WindowState.Normal)
                    Expand(chrome, dock, state, restoreWindowHeight: false);
                else if (!state.Collapsed && window.WindowState == WindowState.Normal)
                    RestoreWindowHeight(state);
            };
            window.PropertyChanged += state.WindowStateHandler;
        }

        ApplyVisuals(chrome, true);
    }

    private static void Expand(ToolChromeControl chrome, IDock dock, State state, bool restoreWindowHeight)
    {
        double header = HeaderHeight(chrome);
        state.Collapsed = false;

        dock.MaxHeight = double.IsNaN(state.SavedMaxHeight) || state.SavedMaxHeight <= header
            ? double.PositiveInfinity
            : state.SavedMaxHeight;

        // Put the shared space back the way it was. Neighbours that are themselves collapsed keep
        // their current (title-bar) share.
        if (state.SavedProportions != null && dock.Owner is IDock owner && owner.VisibleDockables != null)
        {
            foreach (var (dockable, proportion) in state.SavedProportions)
            {
                if (!owner.VisibleDockables.Contains(dockable) || double.IsNaN(proportion)) continue;
                if (!ReferenceEquals(dockable, dock) && dockable is IDock other
                    && States.TryGetValue(other, out var otherState) && otherState.Collapsed) continue;
                dockable.Proportion = proportion;
            }
        }
        state.SavedProportions = null;

        if (state.Window != null)
        {
            state.Window.MinHeight = state.SavedWindowMinHeight;
            // When expanded by maximizing, the saved height is applied once the window is restored.
            if (restoreWindowHeight) RestoreWindowHeight(state);
        }

        ApplyVisuals(chrome, false);
    }

    private static void RestoreWindowHeight(State state)
    {
        var window = state.Window;
        if (window == null) return;
        if (!double.IsNaN(state.SavedWindowHeight) && window.WindowState == WindowState.Normal)
            window.Height = state.SavedWindowHeight;
        state.SavedWindowHeight = double.NaN;
        if (state.WindowStateHandler != null) window.PropertyChanged -= state.WindowStateHandler;
        state.WindowStateHandler = null;
        state.Window = null;
    }

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
