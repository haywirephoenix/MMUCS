using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MMUCS.Desktop.ViewModels;

namespace MMUCS.Desktop.Views;

public partial class ExporterWindow : Window
{
    public ExporterWindow() => InitializeComponent();

    private ExporterViewModel Vm => (ExporterViewModel)DataContext;

    private async void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        IStorageFolder start = null;
        if (!string.IsNullOrWhiteSpace(Vm.OutputFolder))
            start = await StorageProvider.TryGetFolderFromPathAsync(Vm.OutputFolder);

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Export to…",
            SuggestedStartLocation = start,
        });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (path != null) Vm.OutputFolder = path;
    }

    private async void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        var folder = await StorageProvider.TryGetFolderFromPathAsync(Vm.OutputFolder);
        if (folder != null) await Launcher.LaunchFileAsync(folder);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Closing mid-export cancels it; the summary is simply discarded.
        Vm?.Cancel();
        Vm?.Preview.Stop();
        Vm?.SaveSettings();
        base.OnClosing(e);
    }
}
