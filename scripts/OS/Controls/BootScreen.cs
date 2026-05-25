using Godot;
using System;
using System.Threading.Tasks;
using Range = Godot.Range;

public partial class BootScreen : CanvasLayer
{
    private static BootScreen Instance;
    [Export] public bool Enabled = true;
    [Export] public bool DarkBoot;
    [Export] public ProgressBar LoadingBar;
    [Export] public ColorRect Background;
    [Export] public RichTextLabel VerboseText;
    [Export] public TextureRect Logo;
    [Export] public CanvasLayer OSLayer;
    [Export] public float MaxLogoAlpha = 0.3f;
    [Export] public Color LogoColor = Colors.White;
    [Export] public Color BackgroundColor = new(0.078f, 0.114f, 0.122f);
    [Export] public MainCanvas MainCanvas;
    
    private bool _isBooting = true;
    private bool _altHeldDetected = false;
    private bool _ctrlHeldDetected = false;
    private bool _shiftHeldDetected = false;
    private bool _introComplete = false;
    private bool _exitQueued = false;
    private bool _isThemeManagerInitialized = false;
    
    private static bool _pendingProgressVisible = false;
    
    private static readonly NodePath BackgroundPath = "Background";
    private static readonly NodePath LogoPath = "Logo";
    private static readonly NodePath VerboseTextPath = "VerboseText";
    private static readonly NodePath LoadingBarPath = "LoadingBar";
    private static readonly NodePath OSLayerPath = "../OS";
    private static readonly NodePath MainCanvasPath = "../OS/MainCanvas";

    public static bool IsEnabled => Instance != null && Instance.Enabled;
    public static bool IsBooting => Instance != null && Instance._isBooting;
    
    private string mmucsVersion;

    public override void _EnterTree()
    {
        Instance = this;
        
    }

    private void AssignNodes()
    {
        Background = GetNode<ColorRect>(BackgroundPath);
        Logo = GetNode<TextureRect>(LogoPath);
        VerboseText = GetNode<RichTextLabel>(VerboseTextPath);
        LoadingBar = GetNode<ProgressBar>(LoadingBarPath);
        OSLayer = GetNode<CanvasLayer>(OSLayerPath);
        MainCanvas = GetNode<MainCanvas>(MainCanvasPath);
    }
    

    public override void _Ready()
    {
        mmucsVersion = ProjectSettings.GetSetting("application/config/version").AsString();
        
        AssignNodes();
    
        OSLayer.Visible = false;
        Visible = true;
        LoadingBar.Visible = false;

        try 
        {
            EventBus.Instance.ThemeManagerInitialized += InstanceOnThemeManagerInitialized;
            OSLayer.Visible = true;
            ThemeManager.Instance.Init();
        }
        catch (Exception ex)
        {
            StatusBar.SetStatus($"Theme Manager failed to initialize: {ex.Message}. Falling back to default theme.", StatusBar.EStatusType.Error);
        }

        Background.Color = DarkBoot ? Colors.Black : BackgroundColor;
        Background.MouseFilter = Control.MouseFilterEnum.Stop;
        if (DarkBoot) MaxLogoAlpha = 1.0f;
        Logo.Modulate = new Color(LogoColor.R, LogoColor.G, LogoColor.B, 0.0f);

        if (Enabled)  
        {
            Callable.From(async void () => { await StartBootIntro(); }).CallDeferred();
        }
        else{
            MainCanvas.Init();
            FinishBoot();
        }
    }
    
    
    private void InstanceOnThemeManagerInitialized(string path)
    {
        _isThemeManagerInitialized = true;
    }

    public static void AddBootText(string text)
    {
        Callable.From(() => 
        {
            Instance.VerboseText.AppendText("\n" + text);
        }).CallDeferred();
    }

    public static void SetProgress(float percentage)
    {
        if (Instance == null || Instance.LoadingBar == null) return;
        Instance.LoadingBar.CallDeferred(Range.MethodName.SetValue, percentage);
    }

    public static void SetProgressVisible(bool visible)
    {
        _pendingProgressVisible = visible;
        if (Instance == null || Instance.LoadingBar == null) return;
        //Instance.LoadingBar.CallDeferred(CanvasItem.MethodName.SetVisible, visible);
    }

   

    private async Task StartBootIntro()
    {
        if (Logo == null) StatusBar.SetStatus("CRITICAL: 'Logo' node is null!", StatusBar.EStatusType.Error);
        if (MainCanvas == null) StatusBar.SetStatus("CRITICAL: 'MainCanvas' is null!");

        Tween introTween = CreateTween(); 
        if (introTween == null) StatusBar.SetStatus("CRITICAL: Failed to create boot animation", StatusBar.EStatusType.Error);

        introTween?.SetTrans(Tween.TransitionType.Cubic);
        introTween?.SetEase(Tween.EaseType.Out);

        introTween?.TweenInterval(1.0f);
        introTween?.TweenProperty(Logo, "modulate:a", MaxLogoAlpha, 0.5f);

        StatusBar.SetStatus("Initializing OS...");
        if (introTween != null)
        {
            StatusBar.SetStatus("Waiting for boot complete...");
            await ToSignal(introTween, Tween.SignalName.Finished);
        }
        
        try
        {
            if (MainCanvas == null)
            {
                StatusBar.SetStatus("OS canvas can't be found", StatusBar.EStatusType.Error);
                throw new ArgumentNullException(paramName: nameof(MainCanvas), message: "OS canvas can't be found");
            }
            
            await MainCanvas.Init();
            
            // MainCanvas.ForceUpdateTransform();
            // await ToSignal(EventBus.Instance, EventBus.SignalName.UIScaleChangedCompleted);
        }
        catch (Exception e)
        {
            StatusBar.SetStatus($"OS exception: {e.Message}", StatusBar.EStatusType.Error);
        }
        
        StatusBar.SetStatus($"Welcome to MMUCS v{mmucsVersion}");
        
        if (_ctrlHeldDetected || _altHeldDetected || _shiftHeldDetected)
        {
            await Task.Delay(TimeSpan.FromSeconds(5.0));
        }
        else
        {
            await Task.Delay(TimeSpan.FromSeconds(1.0));
        }

        _introComplete = true;
        if (_exitQueued)
        {
            AnimateExitSequence();
        }
        else
        {
            FinishBoot();
        }
    }

    public static void RequestExit()
    {
        if (Instance == null) return;

        if (Instance._introComplete)
        {
            Instance.AnimateExitSequence();
        }
        else
        {
            Instance._exitQueued = true;
        }
    }

    private void AnimateExitSequence()
    {
        SetProgressVisible(false);
        
        VerboseText.Visible = false;

        Tween exitTween = CreateTween();
        exitTween.SetTrans(Tween.TransitionType.Cubic);
        
        exitTween.SetEase(Tween.EaseType.In);
        exitTween.TweenProperty(Logo, "modulate:a", 0.0f, 0.5f);
        
        exitTween.TweenCallback(Callable.From(() => Background.MouseFilter = Control.MouseFilterEnum.Ignore));
        
        exitTween.SetEase(Tween.EaseType.InOut);
        exitTween.TweenProperty(Background, "modulate:a", 0.0f, 0.5f);
        
        exitTween.TweenCallback(Callable.From(FinishBoot));
    }

    public override void _Input(InputEvent @event)
    {
        if(!_isBooting) return;
        if (@event is InputEventKey { Pressed: true } key)
        {
            if (key.Keycode == Key.Alt && !_altHeldDetected)
            {
                _altHeldDetected = true;
                StatusBar.SetStatus("Alt key hold caught! Marking layout for reset.", StatusBar.EStatusType.Notify);
                ConfigManager.ResetPanelLayouts();
            }
            if (key.Keycode == Key.Ctrl && !_ctrlHeldDetected)
            {
                _ctrlHeldDetected = true;
                StatusBar.SetStatus("Control key hold caught! Marking config for reset.", StatusBar.EStatusType.Notify);
                ConfigManager.ResetAppSettings();
                ThemeManager.Instance.RefreshState();
            }
            if (key.Keycode == Key.Shift && !_shiftHeldDetected)
            {
                _shiftHeldDetected = true;
                StatusBar.SetStatus("Shift key hold caught! Clearing all caches.", StatusBar.EStatusType.Notify);
                AkosCelCache.ClearDiskCache();
                ScummBackgroundCache.ClearDiskCache();
            }
        }
    }

    private async void FinishBoot()
    {
        _isBooting = false;

        if (_altHeldDetected)
        {
            EventBus.Instance.EmitSignal(EventBus.SignalName.ConfigResetTriggered);
            
            // await Task.Delay(TimeSpan.FromSeconds(5.0));
        }

        if (_ctrlHeldDetected)
        {
            EventBus.Instance.EmitSignal(EventBus.SignalName.ThemeResetTriggered);
            
            // await Task.Delay(TimeSpan.FromSeconds(5.0));
        }

        if (!_isThemeManagerInitialized && !ThemeManager.IsReady)
        {
            await ToSignal(EventBus.Instance, EventBus.SignalName.ThemeManagerInitialized);
        }

        EventBus.Instance.EmitSignal(EventBus.SignalName.StartupComplete);

        Visible = false;
        SetProcess(false);
        SetPhysicsProcess(false);
        QueueFree();
    }
}