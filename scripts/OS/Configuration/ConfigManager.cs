using System;
using System.Collections.Generic;
using Godot;
public partial class ConfigManager : Node
{
    
    [Export] public bool ResetOnStart = false;
    private static ConfigManager Instance { get; set; }
    public static UserSettings GUISettings { get; private set; }
    public static LayoutSettings Layout { get; private set; }

    private const string GUISettingsPath = "user://guisettings.tres";
    private const string LayoutPath = "user://layout.cfg";

    private const string SettingsPath = "user://settings.cfg";
    private const string SectionShell = "shell";
    private const string SectionApp = "app";
    private const string SectionTheme = "theme";

    [Signal] public delegate void GUISettingsChangedEventHandler(UserSettings newSettings);

    public static void SaveSettings(AppSettings s)
    {
        var cfg = new ConfigFile();

        cfg.SetValue(SectionShell, "mode", (int)s.ShellMode);
        cfg.SetValue(SectionShell, "position", s.ShellPosition);
        cfg.SetValue(SectionShell, "size", s.ShellSize);
        cfg.SetValue(SectionShell, "screen_count", s.ShellScreenCount);

        cfg.SetValue(SectionApp, "gui_scale", s.GuiScale);
        cfg.SetValue(SectionApp, "window_animations", s.WindowAnimations);
        cfg.SetValue(SectionApp, "hidpi", s.HiDPIEnabled);
        cfg.SetValue(SectionApp, "auto_load", s.AutoLoadLastFile);
        cfg.SetValue(SectionApp, "last_file", s.LastFilePath);

        cfg.SetValue(SectionTheme, "wallpaper_mode", (int)s.WallpaperMode);
        cfg.SetValue(SectionTheme, "wallpaper_path", s.WallpaperPath);
        cfg.SetValue(SectionTheme, "wallpaper_blur", s.WallpaperBlurPath);
        cfg.SetValue(SectionTheme, "wallpaper_color", s.WallpaperColor);
        cfg.SetValue(SectionTheme, "glass_tint", s.GlassTintColor);
        cfg.SetValue(SectionTheme, "glass_enabled", s.GlassEnabled);
        cfg.SetValue(SectionTheme, "font_path", s.MainFontPath);

        cfg.Save(SettingsPath);
    }

    public static AppSettings LoadSettings()
    {
        var d = AppSettings.Default;
        var cfg = new ConfigFile();
        if (cfg.Load(SettingsPath) != Error.Ok) return d;

        return new AppSettings(
            shellMode: (Window.ModeEnum)(int)cfg.GetValue(SectionShell, "mode", (int)d.ShellMode),
            shellPosition: (Vector2I)cfg.GetValue(SectionShell, "position", d.ShellPosition),
            shellSize: (Vector2I)cfg.GetValue(SectionShell, "size", d.ShellSize),
            shellScreenCount: (int)cfg.GetValue(SectionShell, "screen_count", d.ShellScreenCount),
            guiScale: (float)cfg.GetValue(SectionApp, "gui_scale", d.GuiScale),
            windowAnimations: (bool)cfg.GetValue(SectionApp, "window_animations", d.WindowAnimations),
            hiDPIEnabled: (bool)cfg.GetValue(SectionApp, "hidpi", d.HiDPIEnabled),
            autoLoadLastFile: (bool)cfg.GetValue(SectionApp, "auto_load", d.AutoLoadLastFile),
            lastFilePath: (string)cfg.GetValue(SectionApp, "last_file", d.LastFilePath),
            wallpaperMode: (Consts.WallPaperModeEnum)(int)cfg.GetValue(SectionTheme, "wallpaper_mode", (int)d.WallpaperMode),
            wallpaperPath: (string)cfg.GetValue(SectionTheme, "wallpaper_path", d.WallpaperPath),
            wallpaperBlurPath: (string)cfg.GetValue(SectionTheme, "wallpaper_blur", d.WallpaperBlurPath),
            wallpaperColor: (Color)cfg.GetValue(SectionTheme, "wallpaper_color", d.WallpaperColor),
            glassTintColor: (Color)cfg.GetValue(SectionTheme, "glass_tint", d.GlassTintColor),
            glassEnabled: (bool)cfg.GetValue(SectionTheme, "glass_enabled", d.GlassEnabled),
            mainFontPath: (string)cfg.GetValue(SectionTheme, "font_path", d.MainFontPath)
        );
    }

    public static bool TryLoadPanelLayout(string panelId, out WindowManager.PanelLayout layout)
    {
        layout = default;
        var cfg = new ConfigFile();
        if (cfg.Load(LayoutPath) != Error.Ok) return false;
        if (!cfg.HasSection(panelId)) return false;

        layout = new WindowManager.PanelLayout(
            position: new Vector2((float)cfg.GetValue(panelId, "x"), (float)cfg.GetValue(panelId, "y")),
            size: new Vector2((float)cfg.GetValue(panelId, "w"), (float)cfg.GetValue(panelId, "h")),
            visible: (bool)cfg.GetValue(panelId, "visible", true)
        );
        return true;
    }

    public static void SavePanelLayouts(IEnumerable<FloatingPanel> panels)
    {
        var cfg = new ConfigFile();
        foreach (var panel in panels)
        {
            cfg.SetValue(panel.PanelId, "x", panel.Position.X);
            cfg.SetValue(panel.PanelId, "y", panel.Position.Y);
            cfg.SetValue(panel.PanelId, "w", panel.Size.X);
            cfg.SetValue(panel.PanelId, "h", panel.Size.Y);
            cfg.SetValue(panel.PanelId, "visible", panel.Visible);
        }
        cfg.Save(LayoutPath);
    }



    public override void _EnterTree()
    {
        Instance = this;

        if (ResetOnStart)
            ResetToDefaults();
        else
            LoadAll();

        GetWindow().CloseRequested += OnShellCloseRequested;
    }

    public override void _Ready()
    {
        //return;
        //ApplyShellSettings();

        // if (!ResetOnStart)
    }

    public static void SaveAll()
    {
        WindowManager.SaveLayout();
        ResourceSaver.Save(GUISettings, GUISettingsPath);
        // ResourceSaver.Save(Layout, LayoutPath);
    }
    private static void LoadAll()
    {
        GUISettings = ResourceLoader.Exists(GUISettingsPath)
            ? ResourceLoader.Load<UserSettings>(GUISettingsPath)
            : new UserSettings();

        Layout = ResourceLoader.Exists(LayoutPath)
            ? ResourceLoader.Load<LayoutSettings>(LayoutPath)
            : new LayoutSettings();
    }

    public static void ResetToDefaults()
    {
        using var dir = DirAccess.Open("user://");
        if (dir != null)
        {
            if (dir.FileExists(GUISettingsPath)) dir.Remove(GUISettingsPath);
            if (dir.FileExists(LayoutPath)) dir.Remove(LayoutPath);
        }
        GUISettings = new UserSettings();
        Layout = new LayoutSettings();

        //ApplyShellSettings();

        Instance.EmitSignal(SignalName.GUISettingsChanged, GUISettings);

        GD.Print("Registry Reset: Shell and Layout returned to project defaults.");
    }

    // --- GUI ---

    public static void UpdateGUISettings(Action<UserSettings> updateAction)
    {
        updateAction(GUISettings);
        SaveGUISettings();
        Instance.EmitSignal(SignalName.GUISettingsChanged, GUISettings);
    }

    public static void SaveGUISettings()
    {
        ResourceSaver.Save(GUISettings, GUISettingsPath);
    }

    private static void LoadGUISettings()
    {
        if (ResourceLoader.Exists(GUISettingsPath))
            GUISettings = ResourceLoader.Load<UserSettings>(GUISettingsPath);
        else
            GUISettings = new UserSettings();
    }

    // --- Layout ---



    public static void UpdatePanelRecord(FloatingPanel panel)
    {
        Layout.PanelRects[panel.PanelId] = new Rect2(panel.Position, panel.Size);
        Layout.PanelVisibility[panel.PanelId] = panel.Visible;
    }

    // --- Shell ---

    private static void ApplyShellSettings()
    {
        var window = Instance.GetWindow();
        var settings = GUISettings;

        if (window == null || settings == null) return;


        if (settings.ShellPosition == new Vector2I(-1, -1))
            window.MoveToCenter();
        else
            window.Position = settings.ShellPosition;

        if (settings.ShellSize != new Vector2I(-1, -1) && settings.ShellPosition > new Vector2I(200, 100))
            window.Size = settings.ShellSize;

        window.Mode = settings.ShellMode;
    }

    private static void UpdateShellSettings()
    {
        var window = Instance.GetWindow();
        // Update the settings resource with the final state
        UpdateGUISettings(s =>
        {
            s.ShellMode = window.Mode;
            if (window.Mode == Window.ModeEnum.Windowed)
            {
                s.ShellPosition = window.Position;
                s.ShellSize = window.Size;
            }
        });
    }

    private static void OnShellCloseRequested()
    {

        SaveAll();

        // Safe to quit now that settings are updated and saved
        Instance.GetTree().Quit();
    }
}