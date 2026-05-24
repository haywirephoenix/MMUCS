using Godot;
using System;
using System.Threading;
using System.Threading.Tasks;
using Range = Godot.Range;

public partial class StatusBar : Control
{
  
    public enum EStatusType
    {
        Normal,
        Notify,
        Warning,
        Error
    }
    
    [Export] public int statusShowDuration = 5;
    [Export] public ProgressBar _progressBar;
    [Export] public Label _statusLabel;
    
    private static StatusBar Instance;
    private CancellationTokenSource _statusCts;
    
    private const string s_colorTagGreen = "[color=green]";
    private const string s_colorTagOrange = "[color=orange]";
    private const string s_colorTagRed = "[color=red]";
    
    private const string s_colorTagStart = "[color=";
    private const string s_colorTagEnd = "[/color]";
    private const string s_closeSqBr = "]";
    private const string s_green = "green";
    private const string s_red = "red";

    public override void _EnterTree()
    {
        Instance = this;
    }
    
    public static void SetStatus(string text, EStatusType type = EStatusType.Normal)
    {
        Callable.From(() => Instance._SetStatus(text, type)).CallDeferred();
    }
    
    public static void SetProgress(float percentage)
    {
        Callable.From(() => Instance._SetProgress(percentage)).CallDeferred();
    }

    public static void SetProgressVisible(bool visible)
    {
        Callable.From(() => Instance._SetProgressVisible(visible)).CallDeferred();
    }
    
    private void _SetProgressVisible(bool visible)
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

    private void _SetProgress(float percentage)
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

    
    
   

    


    private string ColorText(string text, EStatusType type = EStatusType.Normal)
    {
        switch (type)
        {
            case EStatusType.Normal: return text;
            case EStatusType.Notify: return s_colorTagGreen + text + s_colorTagEnd;
            case EStatusType.Warning: return s_colorTagOrange + text + s_colorTagEnd;
            case EStatusType.Error: return s_colorTagRed + text + s_colorTagEnd;
            default: return text;
        }
    }
    private void _SetStatus(string text, EStatusType type = EStatusType.Normal)
    {
        _statusCts?.Cancel();
        _statusCts?.Dispose();
        _statusCts = new CancellationTokenSource();
        var token = _statusCts.Token;

        string formattedText = ColorText(text, type);
        _statusLabel.Text = formattedText;

        if (string.IsNullOrEmpty(text)) return;
    
        GD.PrintRich(formattedText);
    
        if (BootScreen.IsBooting)
        {
            BootScreen.AddBootText(formattedText);
        }

        _ = WaitForClearStatusAsync(token);
    }

    private async Task WaitForClearStatusAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(statusShowDuration), token);
        
            ClearStatusText(); 
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ClearStatusText()
    {
        _statusLabel.Text = string.Empty;
    }
    

}