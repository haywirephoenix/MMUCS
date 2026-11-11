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
