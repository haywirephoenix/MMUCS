using Godot;

public readonly struct AppSettings
{
    // --- Shell ---
    public Window.ModeEnum ShellMode { get; init; }
    public Vector2I ShellPosition { get; init; }
    public Vector2I ShellSize { get; init; }
    public int ShellScreenCount { get; init; }

    // --- App ---
    public float GuiScale { get; init; }
    public bool WindowAnimations { get; init; }
    public bool HiDPIEnabled { get; init; }
    public bool AutoLoadLastFile { get; init; }
    public string LastFilePath { get; init; }
    public bool FirstRun { get; init; }

    // --- Theme ---
    public Consts.WallPaperModeEnum WallpaperMode { get; init; }
    public int WallpaperIndex { get; init; }
    public string WallpaperName { get; init; }
    public string WallpaperBlurName { get; init; }
    public Color WallpaperColor { get; init; }
    public Color GlassTintColor { get; init; }
    public bool GlassEnabled { get; init; }
    public string MainFont { get; init; }

    // --- Constructor ---
    public AppSettings(
        Window.ModeEnum shellMode,
        Vector2I shellPosition,
        Vector2I shellSize,
        int shellScreenCount,
        float guiScale,
        bool windowAnimations,
        bool hiDpiEnabled,
        bool autoLoadLastFile,
        string lastFilePath,
        bool firstRun,
        Consts.WallPaperModeEnum wallpaperMode,
        int wallpaperIndex,
        string wallpaperName,
        string wallpaperBlurName,
        Color wallpaperColor,
        Color glassTintColor,
        bool glassEnabled,
        string mainFont)
    {
        ShellMode = shellMode;
        ShellPosition = shellPosition;
        ShellSize = shellSize;
        ShellScreenCount = shellScreenCount;
        GuiScale = guiScale;
        WindowAnimations = windowAnimations;
        HiDPIEnabled = hiDpiEnabled;
        AutoLoadLastFile = autoLoadLastFile;
        LastFilePath = lastFilePath;
        FirstRun = firstRun;
        WallpaperMode = wallpaperMode;
        WallpaperIndex = wallpaperIndex;
        WallpaperName = wallpaperName;
        WallpaperBlurName = wallpaperBlurName;
        WallpaperColor = wallpaperColor;
        GlassTintColor = glassTintColor;
        GlassEnabled = glassEnabled;
        MainFont = mainFont;
    }

    public static AppSettings Default => new(
        shellMode:        Window.ModeEnum.Maximized,
        shellPosition:    new Vector2I(-1, -1),
        shellSize:        new Vector2I(1920, 1080),
        shellScreenCount: -1,
        guiScale:         1.0f,
        windowAnimations: true,
        hiDpiEnabled:     true,
        autoLoadLastFile: true,
        firstRun: true,
        lastFilePath:     "",
        wallpaperMode:    Consts.WallPaperModeEnum.Image,
        wallpaperIndex:   0,
        wallpaperName:    "fire-skull.png",
        wallpaperBlurName:"fractal1_blurred.png",
        wallpaperColor:   Colors.SlateGray,
        glassTintColor:   new Color(0.18f, 0.384f, 0.851f, 0.027f),
        glassEnabled:     true,
        mainFont:     "Roboto-Regular"
    );
    
    public static void SaveSettings(AppSettings s)
    {
        var cfg = new ConfigFile();

        cfg.SetValue(SettingKeys.Shell.Section, SettingKeys.Shell.Mode, (int)s.ShellMode);
        cfg.SetValue(SettingKeys.Shell.Section, SettingKeys.Shell.Position, s.ShellPosition);
        cfg.SetValue(SettingKeys.Shell.Section, SettingKeys.Shell.Size, s.ShellSize);
        cfg.SetValue(SettingKeys.Shell.Section, SettingKeys.Shell.ScreenCount, s.ShellScreenCount);

        cfg.SetValue(SettingKeys.App.Section, SettingKeys.App.GuiScale, s.GuiScale);
        cfg.SetValue(SettingKeys.App.Section, SettingKeys.App.WindowAnimations, s.WindowAnimations);
        cfg.SetValue(SettingKeys.App.Section, SettingKeys.App.HiDpi, s.HiDPIEnabled);
        cfg.SetValue(SettingKeys.App.Section, SettingKeys.App.AutoLoad, s.AutoLoadLastFile);
        cfg.SetValue(SettingKeys.App.Section, SettingKeys.App.LastFile, s.LastFilePath);
        cfg.SetValue(SettingKeys.App.Section, SettingKeys.App.FirstRun, s.FirstRun);

        cfg.SetValue(SettingKeys.Theme.Section, SettingKeys.Theme.WallpaperMode, (int)s.WallpaperMode);
        cfg.SetValue(SettingKeys.Theme.Section, SettingKeys.Theme.WallpaperIndex, s.WallpaperIndex);
        cfg.SetValue(SettingKeys.Theme.Section, SettingKeys.Theme.WallpaperPath, s.WallpaperName);
        cfg.SetValue(SettingKeys.Theme.Section, SettingKeys.Theme.WallpaperBlur, s.WallpaperBlurName);
        cfg.SetValue(SettingKeys.Theme.Section, SettingKeys.Theme.WallpaperColor, s.WallpaperColor);
        cfg.SetValue(SettingKeys.Theme.Section, SettingKeys.Theme.GlassTint, s.GlassTintColor);
        cfg.SetValue(SettingKeys.Theme.Section, SettingKeys.Theme.GlassEnabled, s.GlassEnabled);
        cfg.SetValue(SettingKeys.Theme.Section, SettingKeys.Theme.FontPath, s.MainFont);

        cfg.Save(SettingKeys.SettingsPath);
    }
    
    public static AppSettings LoadSettings()
    {
        var d = AppSettings.Default;
        var cfg = new ConfigFile();
        if (cfg.Load(SettingKeys.SettingsPath) != Error.Ok) return d;

        return new AppSettings(
            shellMode: (Window.ModeEnum)(int)cfg.GetValue(SettingKeys.Shell.Section, SettingKeys.Shell.Mode, (int)d.ShellMode),
            shellPosition: (Vector2I)cfg.GetValue(SettingKeys.Shell.Section, SettingKeys.Shell.Position, d.ShellPosition),
            shellSize: (Vector2I)cfg.GetValue(SettingKeys.Shell.Section, SettingKeys.Shell.Size, d.ShellSize),
            shellScreenCount: (int)cfg.GetValue(SettingKeys.Shell.Section, SettingKeys.Shell.ScreenCount, d.ShellScreenCount),
            
            guiScale: (float)cfg.GetValue(SettingKeys.App.Section, SettingKeys.App.GuiScale, d.GuiScale),
            windowAnimations: (bool)cfg.GetValue(SettingKeys.App.Section, SettingKeys.App.WindowAnimations, d.WindowAnimations),
            hiDpiEnabled: (bool)cfg.GetValue(SettingKeys.App.Section, SettingKeys.App.HiDpi, d.HiDPIEnabled),
            autoLoadLastFile: (bool)cfg.GetValue(SettingKeys.App.Section, SettingKeys.App.AutoLoad, d.AutoLoadLastFile),
            lastFilePath: (string)cfg.GetValue(SettingKeys.App.Section, SettingKeys.App.LastFile, d.LastFilePath),
            firstRun: (bool)cfg.GetValue(SettingKeys.App.FirstRun, SettingKeys.App.FirstRun, d.FirstRun),
            
            wallpaperMode: (Consts.WallPaperModeEnum)(int)cfg.GetValue(SettingKeys.Theme.Section, SettingKeys.Theme.WallpaperMode, (int)d.WallpaperMode),
            wallpaperIndex: (int)cfg.GetValue(SettingKeys.Theme.Section, SettingKeys.Theme.WallpaperIndex, d.WallpaperIndex),
            wallpaperName: (string)cfg.GetValue(SettingKeys.Theme.Section, SettingKeys.Theme.WallpaperPath, d.WallpaperName),
            wallpaperBlurName: (string)cfg.GetValue(SettingKeys.Theme.Section, SettingKeys.Theme.WallpaperBlur, d.WallpaperBlurName),
            wallpaperColor: (Color)cfg.GetValue(SettingKeys.Theme.Section, SettingKeys.Theme.WallpaperColor, d.WallpaperColor),
            glassTintColor: (Color)cfg.GetValue(SettingKeys.Theme.Section, SettingKeys.Theme.GlassTint, d.GlassTintColor),
            glassEnabled: (bool)cfg.GetValue(SettingKeys.Theme.Section, SettingKeys.Theme.GlassEnabled, d.GlassEnabled),
            mainFont: (string)cfg.GetValue(SettingKeys.Theme.Section, SettingKeys.Theme.FontPath, d.MainFont)
        );
    }
    
    public static void ResetAppSettings()
    {
        using var dir = DirAccess.Open(SettingKeys.UserDir);
        if (dir != null && dir.FileExists(SettingKeys.SettingsPath)) 
            dir.Remove(SettingKeys.SettingsPath);
    }
}