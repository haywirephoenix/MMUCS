using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MMUCS.Core;
using MMUCS.Core.Scumm;
using MMUCS.Desktop.ViewModels;

namespace MMUCS.Desktop.Views;

public partial class RoomView : UserControl
{
    public RoomView()
    {
        InitializeComponent();
        Canvas.ObjectContextRequested += OnObjectContextRequested;
        Canvas.ObjectModifierClicked += OnCanvasModifierClicked;
        // Space toggles the focused object's visibility. Tunnel, so the ListBox doesn't take it first.
        ObjectList.AddHandler(KeyDownEvent, OnObjectListKeyDown, RoutingStrategies.Tunnel);
        ObjectList.ContextRequested += OnObjectListContextRequested;
        ObjectList.SelectionChanged += OnObjectListSelectionChanged;
    }

    private bool _syncingSelection;

    /// <summary>Ctrl-click in the room adds or removes the object from the selection; Shift-click adds it.</summary>
    private void OnCanvasModifierClicked(RoomObject obj, KeyModifiers mods)
    {
        if (_vm?.ItemFor(obj) is not { } item || ObjectList.SelectedItems is not { } selected) return;
        if (selected.Contains(item))
        {
            if ((mods & (KeyModifiers.Control | KeyModifiers.Meta)) != 0) selected.Remove(item);
        }
        else selected.Add(item);
        ObjectList.ScrollIntoView(item);
    }

    /// <summary>The list's selection → the view model: all selected, and the last one picked as the current one.</summary>
    private void OnObjectListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection || _vm == null) return;
        var selected = ObjectList.SelectedItems?.OfType<RoomObjectItem>().ToList() ?? new();
        _vm.SelectedItems = selected;
        var current = e.AddedItems.OfType<RoomObjectItem>().LastOrDefault()
                      ?? (selected.Contains(_vm.SelectedObjectItem) ? _vm.SelectedObjectItem : selected.LastOrDefault());
        _syncingSelection = true;
        _vm.SelectedObjectItem = current;
        _syncingSelection = false;
    }

    /// <summary>The view model's current entry (a click in the room) → the list, as the only one selected.</summary>
    private void SyncListSelection()
    {
        if (_syncingSelection || _vm == null) return;
        var item = _vm.SelectedObjectItem;
        if (item != null && ObjectList.SelectedItems?.Contains(item) == true) return;
        _syncingSelection = true;
        ObjectList.SelectedItems?.Clear();
        if (item != null) ObjectList.SelectedItem = item;
        _vm.SelectedItems = item == null ? System.Array.Empty<RoomObjectItem>() : new[] { item };
        _syncingSelection = false;
    }

    private RoomViewModel _vm;
    private GridLength _objectsWidth = new(220);

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as RoomViewModel;
        if (_vm != null) _vm.PropertyChanged += OnVmPropertyChanged;
        ApplyObjectsPanel();
    }

    private void OnVmPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RoomViewModel.ShowObjectsPanel)) ApplyObjectsPanel();
        else if (e.PropertyName == nameof(RoomViewModel.SelectedObjectItem)) SyncListSelection();
    }

    /// <summary>Collapses or restores the Objects panel column (keeping the width it had).</summary>
    private void ApplyObjectsPanel()
    {
        bool show = _vm?.ShowObjectsPanel ?? true;
        var column = ContentGrid.ColumnDefinitions[2];
        if (!show && column.Width.Value > 0) _objectsWidth = column.Width;
        column.Width = show ? _objectsWidth : new GridLength(0);
        ContentGrid.ColumnDefinitions[1].Width = new GridLength(show ? 4 : 0);
        ObjectsSplitter.IsVisible = show;
        ObjectsPanel.IsVisible = show;
    }

    private void OnObjectListKeyDown(object sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key != Avalonia.Input.Key.Space || _vm == null) return;
        _vm.ToggleSelectedVisibility();
        e.Handled = true;
    }

    private RoomViewModel Vm => DataContext as RoomViewModel;

    /// <summary>Right-click in the Objects list: the same menu as in the room, for the item under the pointer.</summary>
    private void OnObjectListContextRequested(object sender, ContextRequestedEventArgs e)
    {
        var item = (e.Source as Avalonia.StyledElement)?.DataContext as RoomObjectItem ?? Vm?.SelectedObjectItem;
        if (item == null) return;
        // Right-click inside the selection acts on all of it; elsewhere it selects just that entry.
        if (ObjectList.SelectedItems?.Contains(item) != true) Vm.SelectedObjectItem = item;
        ShowObjectMenu(item.Object, ObjectList);
        e.Handled = true;
    }

    private void OnFitClick(object sender, RoutedEventArgs e) => Canvas.ZoomToFit();
    private void OnActualClick(object sender, RoutedEventArgs e) => Canvas.ZoomToActual();

    /// <summary>Right-click on the room: show/hide the object under the pointer, or all objects.</summary>
    private void OnObjectContextRequested(RoomObject obj) => ShowObjectMenu(obj, Canvas);

    private void ShowObjectMenu(RoomObject obj, Control target)
    {
        var vm = Vm;
        if (vm?.Room == null) return;

        static MenuItem Item(string header, System.Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => action();
            return item;
        }

        var menu = new ContextMenu();
        var items = obj == null ? null : vm.ActionItems(vm.ItemFor(obj));
        if (items is { Count: > 1 })
        {
            int n = items.Count;
            menu.Items.Add(Item($"Show {n} selected", () => vm.SetVisible(items, true)));
            menu.Items.Add(Item($"Hide {n} selected", () => vm.SetVisible(items, false)));
            menu.Items.Add(Item($"Show only the {n} selected", () => vm.ShowOnly(items)));
            AddReviewItems(menu, vm, items, Item);
            menu.Items.Add(new Separator());
        }
        else if (obj != null)
        {
            // Underscores in names would otherwise be read as access keys.
            string name = obj.ToString().Replace("_", "__");
            bool visible = vm.IsVisible(obj);
            menu.Items.Add(Item(visible ? $"Hide {name}" : $"Show {name}", () => vm.SetVisible(obj, !visible)));
            menu.Items.Add(Item($"Show only {name}", () => vm.ShowOnly(obj)));
            menu.Items.Add(Item("Select", () => vm.SelectedObject = obj));
            if (CostumeMenu(vm, obj) is { } costumeMenu) menu.Items.Add(costumeMenu);
            if (vm.ItemFor(obj) is { } entry) AddReviewItems(menu, vm, new[] { entry }, Item);
            menu.Items.Add(new Separator());
        }
        menu.Items.Add(Item("Show all objects", () => vm.SetAllVisible(true), vm.AllObjectsVisible != true));
        menu.Items.Add(Item("Hide all objects", () => vm.SetAllVisible(false), vm.AllObjectsVisible != false));
        menu.Open(target);
    }

    /// <summary>Mark for review… (one mark for all of <paramref name="items"/>), and clearing their marks.</summary>
    private void AddReviewItems(ContextMenu menu, RoomViewModel vm, IReadOnlyList<RoomObjectItem> items,
        System.Func<string, System.Action, bool, MenuItem> item)
    {
        var markable = items.Where(i => vm.TargetOf(i) != null).ToList();
        if (markable.Count == 0) return;
        var existing = vm.MarkFor(markable);
        string what = markable.Count > 1 ? $" {markable.Count} together" : "";
        menu.Items.Add(new Separator());
        menu.Items.Add(item(existing != null ? $"Edit review mark{what}…" : $"Mark{what} for review…", () => _ = MarkAsync(vm, markable, existing), true));
        int marks = markable.SelectMany(i => i.ReviewMarks).Select(m => m.Id).Distinct().Count();
        if (marks > 0) menu.Items.Add(item(marks == 1 ? "Clear review mark" : $"Clear {marks} review marks", () => vm.ClearReviewMarks(markable), true));
    }

    /// <summary>Asks what looks wrong (tags and a note) and saves one mark for the entries, for mroom's review command.</summary>
    private async System.Threading.Tasks.Task MarkAsync(RoomViewModel vm, IReadOnlyList<RoomObjectItem> items, ReviewMark existing)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        string name = items.Count == 1 ? items[0].Name : $"{items.Count} together: " + string.Join(", ", items.Select(i => i.Name));
        var result = await ReviewMarkDialog.Ask(owner, name, existing);
        if (result is { } r) vm.MarkForReview(items, r.Tags, r.Note);
    }

    /// <summary>
    /// "Link costume" for an object (or "Link to object" for an unclaimed costume): for costumes the
    /// matching can't place, such as an inventory item's placeholder hotspot. Links are saved.
    /// </summary>
    private static MenuItem CostumeMenu(RoomViewModel vm, RoomObject obj)
    {
        static string Esc(string s) => s.Replace("_", "__");
        MenuItem Radio(string header, bool isChecked, System.Action action)
        {
            var item = new MenuItem { Header = Esc(header), ToggleType = MenuItemToggleType.Radio, IsChecked = isChecked };
            item.Click += (_, _) => action();
            return item;
        }

        var costumes = vm.RoomCostumes;
        if (costumes.Count == 0 || vm.Room == null) return null;

        if (obj.Index < 0)
        {
            // An unclaimed costume: pick the object it belongs to.
            var actor = vm.ItemFor(obj)?.Costume;
            if (actor == null) return null;
            var targets = vm.Room.Objects.Where(o => o.ImageCount == 0).OrderBy(o => o.Name).ToList();
            if (targets.Count == 0) return null;
            var toObject = new MenuItem { Header = "Link to object" };
            foreach (var o in targets)
                toObject.Items.Add(Radio(o.ToString(), false, () => _ = vm.SetCostumeLinkAsync(o, actor.Costume)));
            return toObject;
        }

        int? link = vm.GetCostumeLink(obj);
        var current = vm.ItemFor(obj)?.Costume;
        var menu = new MenuItem { Header = "Link costume" };
        menu.Items.Add(Radio(current != null && link == null ? $"Automatic (costume {current.Costume})" : "Automatic", link == null, () => _ = vm.SetCostumeLinkAsync(obj, null)));
        menu.Items.Add(Radio("None", link == 0, () => _ = vm.SetCostumeLinkAsync(obj, 0)));
        menu.Items.Add(new Separator());
        foreach (var c in costumes)
        {
            string owner = c.Object != null && !ReferenceEquals(c.Object.Block, obj.Block) ? $"   — {c.Object}" : "";
            menu.Items.Add(Radio(c.Label + owner, link == c.Costume, () => _ = vm.SetCostumeLinkAsync(obj, c.Costume)));
        }
        return menu;
    }
}
