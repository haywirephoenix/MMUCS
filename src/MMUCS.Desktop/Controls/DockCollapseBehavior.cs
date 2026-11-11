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
using Dock.Avalonia.Controls;
using Avalonia.VisualTree;
using Dock.Model.Core;

namespace MMUCS.Desktop.Controls;

/// <summary>
/// Turns the down-arrow on every tool panel's title bar into an ImGui-style collapse toggle
/// (the panel menu stays available on right-click of the title bar).
///
///  - In a vertical stack the panel shrinks to its title bar and neighbours take the space
///    (via the dock's MaxHeight, which Dock's proportional panel honours).
///  - A floating window shrinks to its title bar and restores its previous height.
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
        public double SavedWindowHeight = double.NaN;
        public double SavedWindowMinHeight;
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
        if (chrome.DataContext is IDock dock && States.TryGetValue(dock, out var state) && state.Collapsed)
            ApplyVisuals(chrome, collapsed: true);
        else
            ToolTip.SetTip(button, "Collapse");
    }

    private static void OnArrowClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { TemplatedParent: ToolChromeControl chrome }) return;
        if (chrome.DataContext is not IDock dock) return;
        Toggle(chrome, dock);
        e.Handled = true;
    }

    private static void Toggle(ToolChromeControl chrome, IDock dock)
    {
        var state = States.GetOrCreateValue(dock);
        double header = HeaderHeight(chrome);
        var window = TopLevel.GetTopLevel(chrome) as HostWindow;

        if (!state.Collapsed)
        {
            state.Collapsed = true;
            state.SavedMaxHeight = dock.MaxHeight;
            dock.MaxHeight = header;

            if (window != null)
            {
                // Shrink the floating window by the height the content was using.
                state.SavedWindowHeight = window.Height;
                state.SavedWindowMinHeight = window.MinHeight;
                double contentHeight = Math.Max(0, chrome.Bounds.Height - header);
                window.MinHeight = 0;
                window.Height = Math.Max(header, window.Bounds.Height - contentHeight);
            }
        }
        else
        {
            state.Collapsed = false;
            dock.MaxHeight = double.IsNaN(state.SavedMaxHeight) || state.SavedMaxHeight <= header
                ? double.PositiveInfinity
                : state.SavedMaxHeight;

            if (window != null && !double.IsNaN(state.SavedWindowHeight))
            {
                window.Height = state.SavedWindowHeight;
                window.MinHeight = state.SavedWindowMinHeight;
                state.SavedWindowHeight = double.NaN;
            }
        }

        ApplyVisuals(chrome, state.Collapsed);
    }

    private static void ApplyVisuals(ToolChromeControl chrome, bool collapsed)
    {
        foreach (var child in TemplateParts(chrome))
        {
            switch (child)
            {
                case { Name: "PART_ContentPresenter" } presenter:
                    presenter.IsVisible = !collapsed;
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
