using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MMUCS.Desktop.ViewModels;

namespace MMUCS.Desktop.Views.Media;

/// <summary>The media player (Window ▸ Media player): drives the view model's playback ticks while open.</summary>
public partial class MediaPlayerWindow : Window
{
    private readonly DispatcherTimer _timer;

    public MediaPlayerWindow()
    {
        InitializeComponent();
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => Vm?.Tick());
        Opened += (_, _) => _timer.Start();
        Timeline.SeekRequested += frame => Vm?.SeekSound(frame);
    }

    private MediaPlayerViewModel Vm => DataContext as MediaPlayerViewModel;

    private async void OnFontFolderClick(object sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Font folder (font/NNNN, or an export holding font/)",
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path) Vm?.UseFontFolder(path);
    }

    private async void OnFontFileClick(object sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Font file",
            FileTypeFilter = [new FilePickerFileType("Fonts") { Patterns = ["*.ttf", "*.otf", "*.ttc"] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) Vm?.UseTypeface(path);
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        Vm?.Dispose();
        base.OnClosed(e);
    }
}
