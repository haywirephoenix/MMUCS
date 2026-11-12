using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using MMUCS.Core;
using MMUCS.Core.Diagnostics;
using MMUCS.Desktop.Controls;

namespace MMUCS.Desktop.ViewModels.Docking;

/// <summary>Main window position/size, persisted with the layout.</summary>
public sealed class WindowPlacement
{
    public double X { get; set; } = double.NaN;
    public double Y { get; set; } = double.NaN;
    public double Width { get; set; } = double.NaN;
    public double Height { get; set; } = double.NaN;
    public bool Maximized { get; set; }
}

/// <summary>
/// Saves and restores the dock layout (panel arrangement, sizes, active tabs, floating windows) and
/// the main window placement in %LocalAppData%/MMUCS/layout.json.
///
/// Our own small format, rather than Dock's generic serializer: panels are identified by id and
/// rebuilt with fresh instances, so nothing about the panel models needs to be serializable. A file
/// that's unreadable, from another version, or doesn't place every panel exactly once is ignored and
/// the default layout is used.
/// </summary>
public static class DockLayoutStore
{
    private const int CurrentVersion = 1;
    private static string FilePath => Path.Combine(CorePaths.Root, "layout.json");
    private static string _lastWritten;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    // ── File model ───────────────────────────────────────────────────────────

    public sealed class LayoutFile
    {
        public int Version { get; set; }
        public WindowPlacement MainWindow { get; set; }
        public Node Root { get; set; }
        public List<FloatingWindow> Windows { get; set; } = new();
    }

    public sealed class FloatingWindow
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public Node Content { get; set; }
    }

    /// <summary>"split" (proportional), "tools", "documents" or "panel".</summary>
    public sealed class Node
    {
        public string Kind { get; set; }
        public string Id { get; set; }
        public string Orientation { get; set; }
        public double Proportion { get; set; } = double.NaN;
        public string Active { get; set; }
        public List<string> Panels { get; set; }
        /// <summary>Hidden panels that go back into this dock (it is kept, empty, while they're hidden).</summary>
        public List<string> Hidden { get; set; }
        public List<Node> Children { get; set; }
    }

    // ── Save ─────────────────────────────────────────────────────────────────

    public static void Save(IRootDock layout, WindowPlacement mainWindow, Func<IDock, List<string>> hiddenIn = null)
    {
        _hiddenIn = hiddenIn;
        try
        {
            if (layout?.VisibleDockables is not { Count: > 0 }) return;
            var file = new LayoutFile
            {
                Version = CurrentVersion,
                MainWindow = mainWindow,
                Root = Capture(layout.DefaultDockable ?? layout.VisibleDockables[0]),
            };

            foreach (var window in layout.Windows ?? Enumerable.Empty<IDockWindow>())
            {
                window.Save(); // pull the live position/size from the host window into the model
                var content = window.Layout?.VisibleDockables?.FirstOrDefault();
                if (content == null) continue;
                file.Windows.Add(new FloatingWindow
                {
                    X = window.X, Y = window.Y, Width = window.Width, Height = window.Height,
                    Content = Capture(content),
                });
            }

            string json = JsonSerializer.Serialize(file, JsonOptions);
            if (json == _lastWritten && File.Exists(FilePath)) return; // unchanged since the last write
            Directory.CreateDirectory(CorePaths.Root);
            File.WriteAllText(FilePath, json);
            _lastWritten = json;
        }
        catch (Exception ex)
        {
            Log.Warn($"Layout could not be saved: {ex.Message}");
            try { File.AppendAllText(Path.Combine(CorePaths.Root, "layout-errors.log"), $"[{DateTime.Now:O}] save failed{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}"); } catch { }
        }
    }

    private static Func<IDock, List<string>> _hiddenIn;

    /// <summary>Hidden panels that belong in a dock; null when none (keeps the file unchanged for most docks).</summary>
    private static List<string> Hidden(IDock dock) => _hiddenIn?.Invoke(dock) is { Count: > 0 } ids ? ids : null;

    private static Node Capture(IDockable d)
    {
        double proportion = DockCollapseBehavior.GetPersistedProportion(d);
        // An emptied dock (its panels hidden) is collapsed: keep the size it had while open.
        if (d is IDock { IsEmpty: true, IsCollapsable: true } && double.IsFinite(d.CollapsedProportion) && d.CollapsedProportion > 0)
            proportion = d.CollapsedProportion;
        return d switch
        {
            IProportionalDock p => new Node
            {
                Kind = "split",
                Id = p.Id,
                Orientation = p.Orientation.ToString(),
                Proportion = proportion,
                Children = (p.VisibleDockables ?? new List<IDockable>())
                    .Where(c => c is not IProportionalDockSplitter)
                    .Select(Capture).ToList(),
            },
            IToolDock t => new Node
            {
                Kind = "tools",
                Id = t.Id,
                Proportion = proportion,
                Active = t.ActiveDockable?.Id,
                Panels = (t.VisibleDockables ?? new List<IDockable>()).Select(v => v.Id).ToList(),
                Hidden = Hidden(t),
            },
            IDocumentDock dd => new Node
            {
                Kind = "documents",
                Id = dd.Id,
                Proportion = proportion,
                Active = dd.ActiveDockable?.Id,
                Panels = (dd.VisibleDockables ?? new List<IDockable>()).Select(v => v.Id).ToList(),
                Hidden = Hidden(dd),
            },
            _ => new Node { Kind = "panel", Id = d.Id, Proportion = proportion },
        };
    }

    // ── Load ─────────────────────────────────────────────────────────────────

    public static LayoutFile Read()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var file = JsonSerializer.Deserialize<LayoutFile>(File.ReadAllText(FilePath), JsonOptions);
            return file?.Version == CurrentVersion ? file : null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Saved layout ignored: {ex.Message}");
            return null;
        }
    }

    /// <summary>Builds the saved layout, or returns null when it can't be restored faithfully.</summary>
    public static IRootDock TryRestore(MmucsDockFactory factory, LayoutFile file)
    {
        if (file?.Root == null) return null;
        try
        {
            var panels = factory.CreatePanels();
            var used = new HashSet<string>();

            var content = Build(factory, file.Root, panels, used);
            if (content == null) return null;
            var root = factory.CreateMainRoot(content);

            foreach (var w in file.Windows ?? new List<FloatingWindow>())
            {
                var windowContent = Build(factory, w.Content, panels, used);
                if (windowContent == null) continue;
                if (!double.IsFinite(w.Width) || !double.IsFinite(w.Height) || w.Width < 50 || w.Height < 20) continue;
                var window = factory.CreateFloatingWindow(windowContent, w.X, w.Y, w.Width, w.Height);
                root.Windows ??= factory.CreateList<IDockWindow>();
                root.Windows.Add(window);
            }

            // Panels the user hid stay hidden (the Window menu brings them back); a layout with
            // none of this version's panels doesn't fit it.
            if (used.Count == 0) return null;
            return root;
        }
        catch (Exception ex)
        {
            Log.Warn($"Saved layout could not be restored: {ex.Message}");
            return null;
        }
    }

    private static IDockable Build(MmucsDockFactory factory, Node node, Dictionary<string, IDockable> panels, HashSet<string> used)
    {
        if (node == null) return null;

        IList<IDockable> TakePanels() =>
            factory.CreateList((node.Panels ?? new List<string>())
                .Where(id => id != null && panels.ContainsKey(id) && used.Add(id))
                .Select(id => panels[id]).ToArray());

        IDockable result;
        switch (node.Kind)
        {
            case "split":
            {
                var children = (node.Children ?? new List<Node>())
                    .Select(c => Build(factory, c, panels, used))
                    .Where(c => c != null).ToList();
                if (children.Count == 0) return null;
                // Keep single-child splits: Dock can leave them, and their proportion is the pane's size.

                Normalize(children);
                var items = new List<IDockable>();
                for (int i = 0; i < children.Count; i++)
                {
                    if (i > 0) items.Add(new ProportionalDockSplitter());
                    items.Add(children[i]);
                }
                result = new ProportionalDock
                {
                    Id = node.Id,
                    Orientation = Enum.TryParse<Orientation>(node.Orientation, out var o) ? o : Orientation.Horizontal,
                    VisibleDockables = factory.CreateList(items.ToArray()),
                };
                break;
            }
            case "tools":
            {
                var list = TakePanels();
                var hidden = HiddenPanels(node, panels);
                if (list.Count == 0 && hidden.Count == 0) return null;
                var dock = factory.NewToolDock(node.Id, list, list.FirstOrDefault(p => p.Id == node.Active));
                foreach (var id in hidden) factory.RememberHidden(id, dock);
                result = dock;
                break;
            }
            case "documents":
            {
                var list = TakePanels();
                var hidden = HiddenPanels(node, panels);
                if (list.Count == 0 && hidden.Count == 0) return null;
                var dock = factory.NewDocumentDock(node.Id, list, list.FirstOrDefault(p => p.Id == node.Active));
                foreach (var id in hidden) factory.RememberHidden(id, dock);
                result = dock;
                break;
            }
            case "panel":
            {
                if (node.Id == null || !panels.TryGetValue(node.Id, out var panel) || !used.Add(node.Id)) return null;
                result = panel;
                break;
            }
            default:
                return null;
        }

        if (double.IsFinite(node.Proportion) && node.Proportion > 0) result.Proportion = node.Proportion;
        return result;
    }

    /// <summary>A dock's hidden panels that this version knows (an empty dock is kept for them).</summary>
    private static List<string> HiddenPanels(Node node, Dictionary<string, IDockable> panels) =>
        (node.Hidden ?? new List<string>()).Where(id => id != null && panels.ContainsKey(id)).Distinct().ToList();

    /// <summary>Scales sibling proportions to sum to 1 (dropped panes leave gaps otherwise).</summary>
    private static void Normalize(List<IDockable> children)
    {
        double total = children.Sum(c => double.IsFinite(c.Proportion) && c.Proportion > 0 ? c.Proportion : 0);
        int unset = children.Count(c => !double.IsFinite(c.Proportion) || c.Proportion <= 0);
        if (total <= 0) return; // all unset: let the panel share equally
        double fill = unset > 0 ? Math.Max(0.05, (1 - Math.Min(total, 0.95)) / unset) : 0;
        double sum = total + fill * unset;
        foreach (var c in children)
        {
            double v = double.IsFinite(c.Proportion) && c.Proportion > 0 ? c.Proportion : fill;
            c.Proportion = v / sum;
            c.CollapsedProportion = c.Proportion;
        }
    }
}
