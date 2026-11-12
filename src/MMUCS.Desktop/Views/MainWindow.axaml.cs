using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using MMUCS.Core.Diagnostics;
using MMUCS.Core.Scumm;
using MMUCS.Desktop.ViewModels;
using MMUCS.Desktop.ViewModels.Docking;

namespace MMUCS.Desktop.Views;

public partial class MainWindow : Window
{
    private NativeMenu _recentMenu;
    private readonly System.Collections.Generic.Dictionary<string, NativeMenuItem> _panelItems = new();
    private PixelPoint _normalPosition;
    private Size _normalSize;
    private bool _placementApplied;
    private Avalonia.Threading.DispatcherTimer _autosave;
    private NativeMenuItem _reopenItem, _exportItem, _exportRoomItem, _exportCelsItem;

    public MainWindow()
    {
        InitializeComponent();
        AddHandler(DragDrop.DropEvent, OnDrop);
        // Tunnel so Ctrl+F works even when the tree or a text box has focus.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // Track the normal (un-maximized) bounds so a maximized window still saves a sensible size.
        PositionChanged += (_, _) => { if (WindowState == WindowState.Normal) _normalPosition = Position; };
        Opened += OnOpened;
        Closing += (_, _) =>
        {
            _autosave?.Stop();
            Vm?.SaveLayout(CurrentPlacement());
        };
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (WindowState == WindowState.Normal) _normalSize = e.NewSize;
    }

    // ── Window placement ─────────────────────────────────────────────────────

    private WindowPlacement CurrentPlacement()
    {
        bool normal = WindowState == WindowState.Normal;
        var pos = normal ? Position : _normalPosition;
        var size = normal ? Bounds.Size : _normalSize;
        return new WindowPlacement
        {
            X = pos.X,
            Y = pos.Y,
            Width = size.Width > 0 ? size.Width : Width,
            Height = size.Height > 0 ? size.Height : Height,
            Maximized = WindowState == WindowState.Maximized,
        };
    }

    /// <summary>Applies the saved placement before the window is shown, if it is still on a screen.</summary>
    private void ApplyPlacement(WindowPlacement p)
    {
        if (_placementApplied || p == null) return;
        _placementApplied = true;

        if (double.IsFinite(p.Width) && double.IsFinite(p.Height) && p.Width >= MinWidth && p.Height >= MinHeight)
        {
            Width = p.Width;
            Height = p.Height;
        }
        if (double.IsFinite(p.X) && double.IsFinite(p.Y))
        {
            var pos = new PixelPoint((int)p.X, (int)p.Y);
            if (IsOnScreen(pos))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Position = pos;
            }
        }
        _normalPosition = Position;
        _normalSize = new Size(Width, Height);
        if (p.Maximized) WindowState = WindowState.Maximized;
    }

    /// <summary>True when a point near the window's top-left lands in some screen's working area.</summary>
    private bool IsOnScreen(PixelPoint topLeft)
    {
        var probe = new PixelPoint(topLeft.X + 40, topLeft.Y + 20);
        return Screens?.All.Any(s => s.WorkingArea.Contains(probe)) ?? true;
    }

    private void OnOpened(object sender, EventArgs e)
    {
        // Autosave: the layout is written whenever it changes (checked every few seconds), so it
        // survives the app being killed or stopped from a debugger, not just a normal close.
        _autosave = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _autosave.Tick += (_, _) => Vm?.SaveLayout(CurrentPlacement());
        _autosave.Start();

        // Floating panel windows restored onto a monitor that's no longer attached come back over
        // the main window instead.
        if (Vm?.Layout?.Windows == null) return;
        int offset = 0;
        foreach (var w in Vm.Layout.Windows)
        {
            if (w.Host is not Window host) continue;
            if (IsOnScreen(host.Position)) continue;
            offset += 30;
            host.Position = new PixelPoint(Position.X + 80 + offset, Position.Y + 80 + offset);
        }
    }

    private MainWindowViewModel Vm => (MainWindowViewModel)DataContext;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is not MainWindowViewModel vm) return;

        ApplyPlacement(vm.SavedMainWindow);
        BuildMenu();
        vm.RecentFiles.CollectionChanged += (_, _) => RebuildRecentMenu();
        vm.PropertyChanged += OnVmPropertyChanged;
        vm.Room.PropertyChanged += (_, _) => UpdateMenuState();
        vm.Costume.PropertyChanged += (_, _) => UpdateMenuState();
    }

    // ── Menu ─────────────────────────────────────────────────────────────────

    /// <summary>Cmd on macOS, Ctrl elsewhere.</summary>
    private static KeyGesture Shortcut(Key key, KeyModifiers extra = KeyModifiers.None) =>
        new(key, (OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control) | extra);

    private static NativeMenuItem Item(string header, ICommand command, KeyGesture gesture = null) =>
        new(header) { Command = command, Gesture = gesture };

    /// <summary>
    /// One menu definition for every platform: NativeMenu shows in the macOS system menu bar and
    /// through NativeMenuBar inside the window elsewhere.
    /// </summary>
    private void BuildMenu()
    {
        var open = new AsyncRelayCommand(OpenAsync);
        var export = new AsyncRelayCommand(ShowExportWizardAsync);

        _recentMenu = new NativeMenu();
        _reopenItem = new NativeMenuItem("Reopen last file on startup")
        {
            ToggleType = MenuItemToggleType.CheckBox,
            Command = Vm.ToggleReopenLastFileOnStartupCommand,
        };
        _exportItem = Item("Export…", export, Shortcut(Key.E));
        _exportRoomItem = Item("Export room background…", new AsyncRelayCommand(ExportRoomAsync));
        _exportCelsItem = Item("Export costume cels…", new AsyncRelayCommand(ExportCelsAsync));

        var file = new NativeMenu();
        file.Items.Add(Item("Open…", open, Shortcut(Key.O)));
        file.Items.Add(new NativeMenuItem("Open Recent") { Menu = _recentMenu });
        file.Items.Add(_reopenItem);
        file.Items.Add(new NativeMenuItemSeparator());
        file.Items.Add(_exportItem);
        file.Items.Add(_exportRoomItem);
        file.Items.Add(_exportCelsItem);
        file.Items.Add(new NativeMenuItemSeparator());
        file.Items.Add(Item("Clear decode caches", new RelayCommand(ClearCaches)));
        if (!OperatingSystem.IsMacOS())
        {
            // macOS supplies Quit in the application menu.
            file.Items.Add(new NativeMenuItemSeparator());
            file.Items.Add(Item("Exit", new RelayCommand(Close)));
        }

        // Window: a checkbox per panel, ticked while it is placed (docked or floating). Unticking
        // hides it; ticking brings it back, e.g. after closing a floating panel's window.
        var window = new NativeMenu();
        void PanelItem(string id, string title, Key? key = null)
        {
            var item = new NativeMenuItem(title)
            {
                ToggleType = MenuItemToggleType.CheckBox,
                Gesture = key is { } k ? Shortcut(k) : null,
            };
            item.Command = new RelayCommand(() =>
            {
                Vm.TogglePanel(id);
                item.IsChecked = Vm.IsPanelOpen(id);
            });
            _panelItems[id] = item;
            window.Items.Add(item);
            // The shortcut shows a viewer (it doesn't hide one that's already open).
            if (key is { } gesture && !OperatingSystem.IsMacOS())
                KeyBindings.Add(new KeyBinding { Gesture = Shortcut(gesture), Command = new RelayCommand(() => Vm.ShowPanel(id)) });
        }
        PanelItem(MmucsDockFactory.RoomId, "Room", Key.D1);
        PanelItem(MmucsDockFactory.CostumeId, "Costume", Key.D2);
        PanelItem(MmucsDockFactory.ScriptId, "Script", Key.D3);
        window.Items.Add(new NativeMenuItemSeparator());
        PanelItem(MmucsDockFactory.BlocksId, "Blocks");
        PanelItem(MmucsDockFactory.MetadataId, "Metadata");
        PanelItem(MmucsDockFactory.HexId, "Hex");
        window.Opening += (_, _) => UpdatePanelItems();

        // Layout templates will join this menu later.
        var layout = new NativeMenu();
        layout.Items.Add(Item("Reset to default", new RelayCommand(Vm.ResetLayout)));

        var menu = new NativeMenu();
        menu.Items.Add(new NativeMenuItem("File") { Menu = file });
        menu.Items.Add(new NativeMenuItem("Window") { Menu = window });
        menu.Items.Add(new NativeMenuItem("Layout") { Menu = layout });
        NativeMenu.SetMenu(this, menu);

        // The in-window menu bar shows gestures but doesn't bind them; register them on the window.
        // (On macOS the system menu handles its own key equivalents.)
        if (!OperatingSystem.IsMacOS())
        {
            foreach (var item in file.Items.OfType<NativeMenuItem>())
                if (item.Gesture != null && item.Command != null)
                    KeyBindings.Add(new KeyBinding { Gesture = item.Gesture, Command = item.Command });
        }

        RebuildRecentMenu();
        UpdateMenuState();
        UpdatePanelItems();
    }

    private void RebuildRecentMenu()
    {
        if (_recentMenu == null) return;
        _recentMenu.Items.Clear();
        var recent = Vm.RecentFiles;
        if (recent.Count == 0)
        {
            _recentMenu.Items.Add(new NativeMenuItem("(none)") { IsEnabled = false });
            return;
        }

        foreach (var path in recent)
        {
            string p = path;
            _recentMenu.Items.Add(Item($"{Path.GetFileName(p)}   —   {Path.GetDirectoryName(p)}",
                new AsyncRelayCommand(() => Vm.OpenFileAsync(p))));
        }
        _recentMenu.Items.Add(new NativeMenuItemSeparator());
        _recentMenu.Items.Add(Item("Clear recent files", new RelayCommand(Vm.ClearRecentFiles)));
    }

    private void OnVmPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.HasProject) or nameof(MainWindowViewModel.ReopenLastFileOnStartup))
            UpdateMenuState();
    }

    /// <summary>Ticks the Window menu's panels that are placed (a closed floating window unticks its panel).</summary>
    private void UpdatePanelItems()
    {
        foreach (var (id, item) in _panelItems) item.IsChecked = Vm.IsPanelOpen(id);
    }

    private void UpdateMenuState()
    {
        if (_reopenItem == null) return;
        _reopenItem.IsChecked = Vm.ReopenLastFileOnStartup;
        _exportItem.IsEnabled = Vm.HasProject;
        _exportRoomItem.IsEnabled = Vm.Room.HasRoom;
        _exportCelsItem.IsEnabled = Vm.Costume.HasCostume;
    }

    // ── Keyboard / drag-drop ─────────────────────────────────────────────────

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool cmd = e.KeyModifiers.HasFlag(OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control);
        if (e.Key == Key.F && cmd)
        {
            Vm.ShowPanel(MmucsDockFactory.BlocksId);
            Vm.RequestFilterFocus();
            e.Handled = true;
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        var file = e.DataTransfer.TryGetFiles()?.FirstOrDefault();
        var path = file?.TryGetLocalPath();
        if (path != null) await Vm.OpenFileAsync(path);
    }

    // ── Commands ─────────────────────────────────────────────────────────────

    private async Task OpenAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open SCUMM resource",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("SCUMM resources") { Patterns = new[] { "*.la0", "*.la1", "*.la2", "*.lab", "*.lec", "*.000", "*.001" } },
                FilePickerFileTypes.All,
            },
        });

        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path != null) await Vm.OpenFileAsync(path);
    }

    private async Task<string> PickFolderAsync(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    private async Task ShowExportWizardAsync()
    {
        if (Vm.RootBlock == null) return;
        var window = new ExporterWindow { DataContext = new ExporterViewModel(Vm.RootBlock, Vm.Settings) };
        await window.ShowDialog(this);
    }

    private async Task ExportRoomAsync()
    {
        var folder = await PickFolderAsync("Export room background to…");
        if (folder != null) Vm.Room.ExportBackground(folder);
    }

    private async Task ExportCelsAsync()
    {
        var folder = await PickFolderAsync("Export costume cels to…");
        if (folder != null) await Vm.Costume.ExportCelsAsync(folder);
    }

    private static void ClearCaches()
    {
        ScummBackgroundCache.ClearDiskCache();
        AkosCelCache.ClearDiskCache();
        Log.Info("Decode caches cleared.");
    }
}
