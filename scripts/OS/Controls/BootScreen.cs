using Godot;
using System;
using Range = Godot.Range;

public partial class BootScreen : CanvasLayer
{
    private static BootScreen Instance;
    [Export] public bool Enabled = true;
    [Export] public bool DarkBoot;
    [Export] public ProgressBar LoadingBar;
    [Export] public ColorRect Background;
    [Export] public TextureRect Logo;
    [Export] public CanvasLayer OSLayer;
    [Export] public float MaxLogoAlpha = 0.3f;
    [Export] public Color LogoColor = Colors.White;
    [Export] public Color BackgroundColor = new(0.078f, 0.114f, 0.122f);
    
    private bool _isBooting = true;
    private bool _altHeldDetected = false;
    private bool _introComplete = false;
    private bool _exitQueued = false;
    
    // A fallback property to track visibility state if called before _Ready
    private static bool _pendingProgressVisible = false;

    public static bool IsEnabled => Instance != null && Instance.Enabled;
    public static bool IsBooting => Instance != null && Instance._isBooting;

    public override void _EnterTree()
    {
        Instance = this;
        OSLayer.Visible = false;
        Visible = true;
        LoadingBar.Visible = false;
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

    public override void _Ready()
    {
        OSLayer.Visible = true;
       
        Background.Color = DarkBoot ? Colors.Black : BackgroundColor;
        Background.MouseFilter = Control.MouseFilterEnum.Stop;
        
        // LoadingBar.Visible = _pendingProgressVisible; 

        if (DarkBoot) MaxLogoAlpha = 1.0f;
        
        Logo.Modulate = new Color(LogoColor.R, LogoColor.G, LogoColor.B, 0.0f);

        if (Enabled)
            StartBootIntro();
        else
            FinishBoot();
    }

    private void StartBootIntro()
    {
        Tween introTween = CreateTween();
        introTween.SetTrans(Tween.TransitionType.Cubic);
        introTween.SetEase(Tween.EaseType.Out);
        
        introTween.TweenInterval(1.0f);
        introTween.TweenProperty(Logo, "modulate:a", MaxLogoAlpha, 0.5f);
        
        introTween.TweenCallback(Callable.From(() => {
            _introComplete = true;
            if (_exitQueued)
            {
                AnimateExitSequence();
            }
        }));
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

        Tween exitTween = CreateTween();
        exitTween.SetTrans(Tween.TransitionType.Cubic);
        
        exitTween.SetEase(Tween.EaseType.In);
        exitTween.TweenProperty(Logo, "modulate:a", 0.0f, 0.5f);
        
        exitTween.TweenCallback(Callable.From(() => Background.MouseFilter = Control.MouseFilterEnum.Ignore));
        
        exitTween.SetEase(Tween.EaseType.InOut);
        exitTween.TweenProperty(Background, "modulate:a", 0.0f, 0.5f);
        
        exitTween.TweenCallback(Callable.From(FinishBoot));
    }

    public override void _Process(double delta)
    {
        if (_isBooting)
        {
            if (Input.IsKeyPressed(Key.Alt))
            {
                if (!_altHeldDetected)
                {
                    _altHeldDetected = true;
                    GD.Print("Alt key hold caught! Marking config for reset.");
                }
            }
        }
    }

    private void FinishBoot()
    {
        _isBooting = false;

        if (_altHeldDetected)
        {
            EventBus.Instance.EmitSignal(EventBus.SignalName.ConfigResetTriggered);
        }

        EventBus.Instance.EmitSignal(EventBus.SignalName.StartupComplete);

        Visible = false;
        SetProcess(false);
        SetPhysicsProcess(false);
        QueueFree();
    }
}