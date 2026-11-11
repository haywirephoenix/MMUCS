using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MMUCS.Core.Diagnostics;
using MMUCS.Core.Scumm;
using MMUCS.Desktop.ViewModels;

namespace MMUCS.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        AddHandler(DragDrop.DropEvent, OnDrop);
        // Tunnel so Ctrl+F works even when the tree or a text box has focus.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainWindowViewModel vm)
        {
            vm.RecentFiles.CollectionChanged += (_, _) => RebuildRecentMenu();
            RebuildRecentMenu();
        }
    }

    /// <summary>Recent list is rebuilt in code: simpler and more predictable than templated MenuItems.</summary>
    private void RebuildRecentMenu()
    {
        RecentMenu.Items.Clear();
        var recent = Vm.RecentFiles;
        if (recent.Count == 0)
        {
            RecentMenu.Items.Add(new MenuItem { Header = "(none)", IsEnabled = false });
            return;
        }

        for (int i = 0; i < recent.Count; i++)
        {
            string path = recent[i];
            var item = new MenuItem
            {
                // "_1" gives an access key; the folder helps tell same-named files apart.
                // Underscores in paths are doubled so they aren't treated as access-key markers.
                Header = $"_{(i + 1) % 10}  {System.IO.Path.GetFileName(path).Replace("_", "__")}   —   {System.IO.Path.GetDirectoryName(path)?.Replace("_", "__")}",
            };
            ToolTip.SetTip(item, path);
            item.Click += async (_, _) => await Vm.OpenFileAsync(path);
            RecentMenu.Items.Add(item);
        }
        RecentMenu.Items.Add(new Separator());
        var clear = new MenuItem { Header = "Clear recent files" };
        clear.Click += (_, _) => Vm.ClearRecentFiles();
        RecentMenu.Items.Add(clear);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            FilterBox.Focus();
            FilterBox.SelectAll();
            e.Handled = true;
        }
    }

    private void OnFilterKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Vm.ClearFilter();
                e.Handled = true;
                break;
            case Key.Enter:
                Vm.ApplyFilter(); // skip the debounce
                BlockTree.Focus();
                e.Handled = true;
                break;
        }
    }

    private MainWindowViewModel Vm => (MainWindowViewModel)DataContext;

    private async void OnOpenClick(object sender, RoutedEventArgs e)
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

    private async void OnDrop(object sender, DragEventArgs e)
    {
        var file = e.DataTransfer.TryGetFiles()?.FirstOrDefault();
        var path = file?.TryGetLocalPath();
        if (path != null) await Vm.OpenFileAsync(path);
    }

    private async Task<string> PickFolderAsync(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    private async void OnExportWizardClick(object sender, RoutedEventArgs e)
    {
        if (Vm.RootBlock == null) return;
        var window = new ExporterWindow { DataContext = new ExporterViewModel(Vm.RootBlock, Vm.Settings) };
        await window.ShowDialog(this);
    }

    private async void OnExportRoomClick(object sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync("Export room background to…");
        if (folder != null) Vm.Room.ExportBackground(folder);
    }

    private async void OnExportCelsClick(object sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync("Export costume cels to…");
        if (folder != null) await Vm.Costume.ExportCelsAsync(folder);
    }

    private void OnClearCachesClick(object sender, RoutedEventArgs e)
    {
        ScummBackgroundCache.ClearDiskCache();
        AkosCelCache.ClearDiskCache();
        Log.Info("Decode caches cleared.");
    }

    private void OnExitClick(object sender, RoutedEventArgs e) => Close();
    private void OnShowRoomTab(object sender, RoutedEventArgs e) => Vm.SelectedViewerIndex = (int)ViewerTab.Room;
    private void OnShowCostumeTab(object sender, RoutedEventArgs e) => Vm.SelectedViewerIndex = (int)ViewerTab.Costume;
    private void OnShowScriptTab(object sender, RoutedEventArgs e) => Vm.SelectedViewerIndex = (int)ViewerTab.Script;
}
