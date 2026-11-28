using Avalonia.Controls;
using Avalonia.Interactivity;
using MMUCS.Desktop.ViewModels;

namespace MMUCS.Desktop.Views.Game;

/// <summary>The game window's Mods view (<see cref="ModsViewModel"/>), shown over the game.</summary>
public partial class ModsView : UserControl
{
    public ModsView() => InitializeComponent();

    /// <summary>Shows the _mods folder in the system's file manager.</summary>
    private async void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ModsViewModel { HasModsFolder: true } vm || TopLevel.GetTopLevel(this) is not { } top) return;
        if (await top.StorageProvider.TryGetFolderFromPathAsync(new System.Uri(vm.LocalModsPath)) is { } folder) await top.Launcher.LaunchFileAsync(folder);
    }
}
