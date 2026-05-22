using Godot;
using System;
using System.Threading;
using System.Threading.Tasks;
using Range = Godot.Range;

public partial class StatusBar : Control
{
    [Export] public int statusShowDuration = 5;
    [Export] public ProgressBar _progressBar;
    [Export] public Label _statusLabel;
    
    private static StatusBar Instance;
    private CancellationTokenSource _statusCts;

    public override void _EnterTree()
    {
        Instance = this;
    }

    public static void SetProgress(float percentage)
    {
        if (BootScreen.IsBooting && BootScreen.IsEnabled)
        {
            BootScreen.SetProgress(percentage);
        }
        else if (Instance?._progressBar != null)
        {
            Instance._progressBar.CallDeferred(Range.MethodName.SetValue, percentage);
        }
    }

    public static void SetProgressVisible(bool visible)
    {
        if (BootScreen.IsBooting && BootScreen.IsEnabled)
        {
            BootScreen.SetProgressVisible(visible);
        }
        else if (Instance?._progressBar != null)
        {
            Instance._progressBar.CallDeferred(CanvasItem.MethodName.SetVisible, visible);
        }
    }

    public static void SetStatus(string text)
    {
        if (BootScreen.IsBooting && BootScreen.IsEnabled) return;
        Instance?.CallDeferred(nameof(_SetStatus), text);
    }

    private void _SetStatus(string text)
    {
        
        _statusCts?.Cancel();
        _statusCts?.Dispose();
        _statusCts = new CancellationTokenSource();
        var token = _statusCts.Token;

        _statusLabel.Text = text;

        if (string.IsNullOrEmpty(text)) return;

        Task.Delay(TimeSpan.FromSeconds(statusShowDuration), token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
            {
                CallDeferred(MethodName.ClearStatusText);
            }
        }, token);
    }

    private void ClearStatusText()
    {
        _statusLabel.Text = string.Empty;
    }
}