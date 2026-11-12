using Avalonia.Controls;
using Avalonia.Interactivity;
using MMUCS.Core.Scumm;
using MMUCS.Desktop.ViewModels;

namespace MMUCS.Desktop.Views;

public partial class RoomView : UserControl
{
    public RoomView()
    {
        InitializeComponent();
        Canvas.ObjectContextRequested += OnObjectContextRequested;
        // Space toggles the focused object's visibility. Tunnel, so the ListBox doesn't take it first.
        ObjectList.AddHandler(KeyDownEvent, OnObjectListKeyDown, RoutingStrategies.Tunnel);
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

    private void OnFitClick(object sender, RoutedEventArgs e) => Canvas.ZoomToFit();
    private void OnActualClick(object sender, RoutedEventArgs e) => Canvas.ZoomToActual();

    /// <summary>Right-click on the room: show/hide the object under the pointer, or all objects.</summary>
    private void OnObjectContextRequested(RoomObject obj)
    {
        var vm = Vm;
        if (vm?.Room == null) return;

        MenuItem Item(string header, System.Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => action();
            return item;
        }

        var menu = new ContextMenu();
        if (obj != null)
        {
            // Underscores in names would otherwise be read as access keys.
            string name = obj.ToString().Replace("_", "__");
            bool visible = vm.IsVisible(obj);
            menu.Items.Add(Item(visible ? $"Hide {name}" : $"Show {name}", () => vm.SetVisible(obj, !visible)));
            menu.Items.Add(Item($"Show only {name}", () => vm.ShowOnly(obj)));
            menu.Items.Add(Item("Select", () => vm.SelectedObject = obj));
            menu.Items.Add(new Separator());
        }
        menu.Items.Add(Item("Show all objects", () => vm.SetAllVisible(true), vm.AllObjectsVisible != true));
        menu.Items.Add(Item("Hide all objects", () => vm.SetAllVisible(false), vm.AllObjectsVisible != false));
        menu.Open(Canvas);
    }
}
