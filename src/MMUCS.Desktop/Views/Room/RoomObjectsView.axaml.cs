using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MMUCS.Desktop.ViewModels;

namespace MMUCS.Desktop.Views.Room;

/// <summary>
/// The Room viewer's Objects panel. Its list holds the multi-selection (Ctrl / Shift click), kept in
/// step with the view model: the last one picked is the current one (canvas, Costume viewer).
/// </summary>
public partial class RoomObjectsView : UserControl
{
    private RoomViewModel _vm;
    private bool _syncingSelection;

    public RoomObjectsView()
    {
        InitializeComponent();
        // Space toggles the focused object's visibility. Tunnel, so the ListBox doesn't take it first.
        ObjectList.AddHandler(KeyDownEvent, OnObjectListKeyDown, RoutingStrategies.Tunnel);
        ObjectList.ContextRequested += OnObjectListContextRequested;
        ObjectList.SelectionChanged += OnObjectListSelectionChanged;
    }

    // Re-docking the panel unloads and reloads the view: listen only while it is on screen.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Attach(DataContext as RoomViewModel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Attach(null);
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (this.IsAttachedToVisualTree()) Attach(DataContext as RoomViewModel);
    }

    private void Attach(RoomViewModel vm)
    {
        if (ReferenceEquals(vm, _vm)) return;
        if (_vm != null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.ModifierSelectRequested -= OnModifierSelect;
        }
        _vm = vm;
        if (_vm == null) return;
        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.ModifierSelectRequested += OnModifierSelect;
        // Back on screen: show the selection the view model has.
        _syncingSelection = true;
        ObjectList.SelectedItems?.Clear();
        foreach (var item in _vm.SelectedItems.Where(_vm.Objects.Contains)) ObjectList.SelectedItems?.Add(item);
        _syncingSelection = false;
        SyncListSelection();
    }

    private void OnVmPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RoomViewModel.SelectedObjectItem)) SyncListSelection();
    }

    /// <summary>Ctrl-click in the room adds or removes the object from the selection; Shift-click adds it.</summary>
    private void OnModifierSelect(RoomObjectItem item, KeyModifiers mods)
    {
        if (ObjectList.SelectedItems is not { } selected) return;
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
        _vm.SelectedItems = item == null ? Array.Empty<RoomObjectItem>() : new[] { item };
        _syncingSelection = false;
    }

    private void OnObjectListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || _vm == null) return;
        _vm.ToggleSelectedVisibility();
        e.Handled = true;
    }

    /// <summary>Right-click in the Objects list: the same menu as in the room, for the item under the pointer.</summary>
    private void OnObjectListContextRequested(object sender, ContextRequestedEventArgs e)
    {
        if (_vm == null) return;
        var item = (e.Source as StyledElement)?.DataContext as RoomObjectItem ?? _vm.SelectedObjectItem;
        if (item == null) return;
        // Right-click inside the selection acts on all of it; elsewhere it selects just that entry.
        if (ObjectList.SelectedItems?.Contains(item) != true) _vm.SelectedObjectItem = item;
        RoomObjectMenu.Show(_vm, item.Object, ObjectList);
        e.Handled = true;
    }
}
