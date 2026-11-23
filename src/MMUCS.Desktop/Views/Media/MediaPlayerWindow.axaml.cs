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

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        Vm?.Dispose();
        base.OnClosed(e);
    }
}
