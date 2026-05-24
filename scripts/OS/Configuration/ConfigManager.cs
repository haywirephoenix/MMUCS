using System;
using System.Collections.Generic;
using Godot;

public partial class ConfigManager : Node
{
    [Export] public bool ResetOnStart = false;
    private static ConfigManager Instance { get; set; }
    private static AppSettings _appSettings = AppSettings.Default;
    public static AppSettings AppSettings => _appSettings;

    public override void _EnterTree()
    {
        Instance = this;

        if (ResetOnStart)
            ResetAllToDefaults();
        else
            LoadAll();

        GetWindow().CloseRequested += OnShellCloseRequested;
    }

    public override void _Ready()
    {
        ApplyShellSettings();
    }

    private static void LoadAll()
    {
        _appSettings = LoadSettings();

        // Layout = ResourceLoader.Exists(LayoutPath)
        //     ? ResourceLoader.Load<LayoutSettings>(LayoutPath)
        //     : new LayoutSettings();
    }

    private static void SaveAll()
    {
        WindowManager.SaveLayout();
        UpdateShellSettings();
        SaveSettings(AppSettings);
    }

    // --- Core Settings Read / Write Engine ---

    private static void SaveSettings(AppSettings s) => AppSettings.SaveSettings(s);
    private static AppSettings LoadSettings() => AppSettings.LoadSettings();
    

    // --- State Handlers (Mutating structurally immutable data) ---
    
    public static void UpdateAppSettings(Func<AppSettings, AppSettings> updateFunc)
    {
        _appSettings = updateFunc(_appSettings);
        
        SaveSettings(_appSettings);
        EventBus.Instance.EmitSignal(EventBus.SignalName.AppSettingsChanged);
    }

    private static void ApplyShellSettings()
    {
        var settings = AppSettings;

        if (settings.ShellMode == Window.ModeEnum.Windowed)
        {
            if (settings.ShellSize != new Vector2I(-1, -1))
                ShellManager.Resize(settings.ShellSize);

            if (settings.ShellPosition == new Vector2I(-1, -1))
                ShellManager.MoveToCenter();
            else
                ShellManager.MoveTo(settings.ShellPosition);
        }

        // ShellManager.SetWindowMode(settings.ShellMode);
    }

    private static void UpdateShellSettings()
    {
        var window = Instance?.GetWindow();
        if (window == null) return;
        
        UpdateAppSettings(s => s with 
        {
            ShellMode = window.Mode,
            ShellPosition = window.Mode == Window.ModeEnum.Windowed ? window.Position : s.ShellPosition,
            ShellSize = window.Mode == Window.ModeEnum.Windowed ? window.Size : s.ShellSize
        });
    }

    // --- Resets ---

    public static void ResetAppSettings()
    {
       AppSettings.ResetAppSettings();
       _appSettings = AppSettings.Default;
    }

    public static void ResetPanelLayouts()  => LayoutSettings.ResetPanelLayouts();
    
    public static void ResetAllToDefaults()
    {
        ResetPanelLayouts();
        ResetAppSettings();
        EventBus.Instance.EmitSignal(EventBus.SignalName.AppSettingsChanged, Variant.From(AppSettings));
        StatusBar.SetStatus("Registry Reset: Settings and Layout returned to project defaults.");
    }

    // --- Panel Layout Passing ---

    public static bool TryLoadPanelLayout(string panelId, out LayoutSettings layout) => LayoutSettings.TryLoadPanelLayout(panelId, out layout);

    public static void SavePanelLayouts(IEnumerable<FloatingPanel> panels) => LayoutSettings.SavePanelLayouts(panels);

    private static void OnShellCloseRequested()
    {
        SaveAll();
        Instance.GetTree().Quit();
    }
}