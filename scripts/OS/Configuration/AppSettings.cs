using Godot;
public readonly struct AppSettings
{
    // Shell
    public readonly Window.ModeEnum ShellMode;
    public readonly Vector2I ShellPosition;
    public readonly Vector2I ShellSize;
    public readonly int ShellScreenCount;

    // App
    public readonly float GuiScale;
    public readonly bool WindowAnimations;
    public readonly bool HiDPIEnabled;
    public readonly bool AutoLoadLastFile;
    public readonly string LastFilePath;

    // Theme
    public readonly Consts.WallPaperModeEnum WallpaperMode;
    public readonly string WallpaperPath;
    public readonly string WallpaperBlurPath;
    public readonly Color WallpaperColor;
    public readonly Color GlassTintColor;
    public readonly bool GlassEnabled;
    public readonly string MainFontPath;

    public AppSettings(
        Window.ModeEnum shellMode, Vector2I shellPosition, Vector2I shellSize, int shellScreenCount,
        float guiScale, bool windowAnimations, bool hiDPIEnabled, bool autoLoadLastFile, string lastFilePath,
        Consts.WallPaperModeEnum wallpaperMode, string wallpaperPath, string wallpaperBlurPath,
        Color wallpaperColor, Color glassTintColor, bool glassEnabled, string mainFontPath)
    {
        ShellMode        = shellMode;
        ShellPosition    = shellPosition;
        ShellSize        = shellSize;
        ShellScreenCount = shellScreenCount;
        GuiScale         = guiScale;
        WindowAnimations = windowAnimations;
        HiDPIEnabled     = hiDPIEnabled;
        AutoLoadLastFile = autoLoadLastFile;
        LastFilePath     = lastFilePath;
        WallpaperMode    = wallpaperMode;
        WallpaperPath    = wallpaperPath;
        WallpaperBlurPath = wallpaperBlurPath;
        WallpaperColor   = wallpaperColor;
        GlassTintColor   = glassTintColor;
        GlassEnabled     = glassEnabled;
        MainFontPath     = mainFontPath;
    }

    public static AppSettings Default => new(
        shellMode:        Window.ModeEnum.Windowed,
        shellPosition:    new Vector2I(-1, -1),
        shellSize:        new Vector2I(1280, 720),
        shellScreenCount: -1,
        guiScale:         1.0f,
        windowAnimations: true,
        hiDPIEnabled:     true,
        autoLoadLastFile: true,
        lastFilePath:     "",
        wallpaperMode:    Consts.WallPaperModeEnum.Image,
        wallpaperPath:    "res://wallpapers/fire-skull.png",
        wallpaperBlurPath:"res://wallpapers/blurred/fractal1_blurred.png",
        wallpaperColor:   Colors.SlateGray,
        glassTintColor:   new Color(1.0f, 1.0f, 1.0f, 0.027f),
        glassEnabled:     true,
        mainFontPath:     "res://fonts/Roboto-Regular.ttf"
    );

    public Font LoadFont() =>
        ResourceLoader.Exists(MainFontPath)
            ? ResourceLoader.Load<Font>(MainFontPath)
            : Consts.GetRobotoFont();
}