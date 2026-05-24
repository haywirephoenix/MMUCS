using System;
using Godot;

public partial class ThemeManager : Node
{
    #region References
    
    [ExportGroup("References")]
    [Export] public TextureRect WallpaperTextureRect { get; set; }
    [Export] public ColorRect WallpaperColorRect { get; set; }
    [Export] public Material WindowMaterial { get; set; }
    [Export] public StyleBox WindowStyleBox { get; set; }
    [Export] public Shader WindowShader { get; set; }
    
    [Export] public Texture2D WallpaperFractal { get; set; }
    [Export] public Texture2D WallpaperFractalBlur { get; set; }
    [Export] public Texture2D WallpaperFireSkull { get; set; }
    [Export] public Texture2D WallpaperFireSkullBlur { get; set; }
    
    #endregion
    
    #region Config
    
    // [ExportGroup("Config")]
    private Consts.WallPaperModeEnum WallpaperMode => ConfigManager.AppSettings.WallpaperMode;
    // private Texture2D WallpaperImage => ;
    // private string WallpaperPath => ConfigManager.AppSettings.WallpaperName;
    private int WallpaperIndex => ConfigManager.AppSettings.WallpaperIndex;
    private string WallpaperBlurPath => ConfigManager.AppSettings.WallpaperBlurName;
    private Color WallpaperColor => ConfigManager.AppSettings.WallpaperColor;
    private bool GlassEnabled =>  ConfigManager.AppSettings.GlassEnabled;
    private bool HiDPIEnabled =>  ConfigManager.AppSettings.HiDPIEnabled;
    private bool WindowAnimations =>  ConfigManager.AppSettings.WindowAnimations;
    private Color GlassTintColor => ConfigManager.AppSettings.GlassTintColor;
    private float GUIScale => ConfigManager.AppSettings.GuiScale;
    // private Color WindowColor = Colors.SlateGray;
    
    public bool WallpaperEnabled => WallpaperMode == Consts.WallPaperModeEnum.Image;

    public static bool GlassAvailable => Instance.WallpaperEnabled;
    
    #endregion

    #region References
    
    [ExportGroup("References")]
    [Export] public TextureRect WallpaperTextureRect { get; set; }
    [Export] public ColorRect WallpaperColorRect { get; set; }
    [Export] public Material WindowMaterial { get; set; }
    [Export] public StyleBox WindowStyleBox { get; set; }
    [Export] public Shader WindowShader { get; set; }
    
    [Export] public Texture2D WallpaperFractal { get; set; }
    [Export] public Texture2D WallpaperFractalBlur { get; set; }
    [Export] public Texture2D WallpaperFireSkull { get; set; }
    [Export] public Texture2D WallpaperFireSkullBlur { get; set; }
    
    #endregion

    #region Consts
    
    private const string SHADER_BLURRED_WALL_ID = "blurred_wallpaper";
    private const string SHADER_GLASS_ENABLE_ID = "glass_enabled";
    private const string SHADER_GLASS_TINT_ID = "tint";
    
    
    private const string FILE_BLURRED = "_blurred";
    
    #endregion

    public static ThemeManager Instance;

    public static Texture2D[] WallPapers;

    public static Texture2D[] WallPaperBlurs;

    public override void _EnterTree()
    {
        Instance = this;
        
        WallPapers = [ WallpaperFractal, WallpaperFireSkull];
        WallPaperBlurs = [ WallpaperFractalBlur, WallpaperFireSkullBlur ];
    
    }

    public override void _Ready()
    {
        // EventBus.Instance.Connect(EventBus.SignalName.WallpaperChangeRequested,
        //     Callable.From<string>(OnWallpaperChanged));
        EventBus.Instance.Connect(EventBus.SignalName.WallpaperChangeRequested,
        Callable.From<int>(OnWallpaperChanged));
        
        EventBus.Instance.Connect(EventBus.SignalName.WallpaperModeChangeRequested,
            Callable.From<int>(i => OnWallpaperModeChanged((Consts.WallPaperModeEnum)i)));
        EventBus.Instance.Connect(EventBus.SignalName.WallpaperColorChangeRequested,
            Callable.From<Color>(OnWallpaperColorChanged));
        EventBus.Instance.Connect(EventBus.SignalName.GlassChangeRequested,
            Callable.From<bool>(OnGlassEnabledChanged));
        EventBus.Instance.Connect(EventBus.SignalName.GlassChangeSystemRequested,
            Callable.From<bool>(OnGlassEnabledChangedSystem));
        EventBus.Instance.Connect(EventBus.SignalName.UIScaleChangeRequested,
            Callable.From<float>(OnUIScaleChanged));
        EventBus.Instance.Connect(EventBus.SignalName.HiDPIChangeRequested,
            Callable.From<bool>(OnHiDPIEnabledChanged));
        EventBus.Instance.Connect(EventBus.SignalName.WindowAnimationsChangeRequested,
            Callable.From<bool>(OnWindowAnimationsChanged));
        
        OnWallpaperChanged(WallpaperIndex);
        OnWallpaperColorChanged(WallpaperColor);
        OnGlassEnabledChanged(GlassEnabled);
        OnWallpaperModeChanged(WallpaperMode);
        OnUIScaleChanged(GUIScale);
        OnHiDPIEnabledChanged(HiDPIEnabled);
    }


    public void OnUIScaleChanged(float newScale)
    {
        GetTree().Root.ContentScaleFactor = newScale;
        ConfigManager.UpdateAppSettings(s => s with {GuiScale = newScale} );
    }

    private bool glassWasDisabledByWallpaperToggle;
    
    /*
    private bool GetBlurredWallpaper(string originalPath, out string blurredPath)
    {
        blurredPath = null;
        if (string.IsNullOrEmpty(originalPath)) return false;

        string fileName = originalPath.GetBaseName().GetFile();
        string exten = originalPath.GetExtension();
    
        blurredPath = Consts.WALL_DIR_BLURRED + fileName + FILE_BLURRED + "." + exten;

        bool exists = FileAccess.FileExists(blurredPath);
        
        if(exists)
            GD.Print($"Blurred exists: {blurredPath}");
        else
            GD.PushError($"Blurred wallpaper missing at: {blurredPath}");
        
        return exists;
    }*/


    private void OnWallpaperChanged(int wallpaperIndex)
    {
        if(wallpaperIndex >= WallPapers.Length) return;
        var wptex = WallPapers[wallpaperIndex];
        if (wptex == null) return;
        
        if (wallpaperIndex == ConfigManager.AppSettings.WallpaperIndex) return; 
        if (WindowMaterial is not ShaderMaterial shaderMat) return;

        var blurTex = WallPaperBlurs[wallpaperIndex];
        // var blurName = blurTex.GetName();
        ConfigManager.UpdateAppSettings(s => s with {WallpaperIndex = wallpaperIndex} );

        // ConfigManager.GUISettings.WallpaperName = wpName;
        // ConfigManager.GUISettings.WallpaperBlurName = blurName;
    
        WallpaperTextureRect.Texture = wptex;
        shaderMat.SetShaderParameter(SHADER_BLURRED_WALL_ID, blurTex);
    }
    /*
    private void OnWallpaperChanged(string wallpaperName)
    {
        if (string.IsNullOrEmpty(wallpaperName)) return;
    
        if (wallpaperName == ConfigManager.GUISettings.WallpaperName) return;

        if (WindowMaterial is not ShaderMaterial shaderMat) return;

        var wptex = GD.Load<Texture2D>(wallpaperName);
        if (wptex == null) return;

        if (!GetBlurredWallpaper(wallpaperName, out string blurredPath)) return;
    
        var blurtex = GD.Load<Texture2D>(blurredPath); 
        if (blurtex == null) return;
    
        ConfigManager.GUISettings.WallpaperName = wallpaperName;
        ConfigManager.GUISettings.WallpaperBlurName = blurredPath;
    
        WallpaperTextureRect.Texture = wptex;
        shaderMat.SetShaderParameter(SHADER_BLURRED_WALL_ID, blurtex);
    
        GD.Print($"Wallpaper changed to: {wallpaperName}");
    }
    */
    private void OnWallpaperColorChanged(Color value)
    {
        // if(WallpaperColorRect == null) return;
        // if(WallpaperColorRect.Color == value) return;
        WallpaperColorRect.Color = value;
        ConfigManager.UpdateAppSettings(s => s with {WallpaperColor = value} );
    }
    private void OnWindowAnimationsChanged(bool value)
    {
        ConfigManager.UpdateAppSettings(s => s with {WindowAnimations = value} );
    }

    private void OnHiDPIEnabledChanged(bool value)
    {
        Window root = GetTree().Root;
        

        if (value)
        {
            root.ContentScaleMode = Window.ContentScaleModeEnum.CanvasItems;
            root.ContentScaleFactor = DisplayServer.ScreenGetMaxScale();
            // GD.Print("dpi on");
        }
        else
        {
            root.ContentScaleMode = Window.ContentScaleModeEnum.Viewport;
            root.ContentScaleFactor = 1.0f;
            // GD.Print("dpi off");
        }
        ConfigManager.UpdateAppSettings(s => s with {HiDPIEnabled = value} );
    }

    private void OnGlassEnabledChangedSystem(bool value)
    => OnGlassEnabledChangedInternal(value, true);
    private void OnGlassEnabledChanged(bool value)
    => OnGlassEnabledChangedInternal(value);
    
    private void OnGlassEnabledChangedInternal(bool value, bool systemChange = false)
    {
        if (!value && !systemChange)
        {
            // User explicitly turned glass off — clear the flag so it won't
            // be auto-restored when switching wallpaper mode back to Image
            glassWasDisabledByWallpaperToggle = false;
        }

        if (WindowMaterial is not ShaderMaterial shaderMat)
        {
            GD.PrintErr("Glass material is null");
            return;
        }

        shaderMat.SetShaderParameter(SHADER_GLASS_ENABLE_ID, value);
        
        ConfigManager.UpdateAppSettings(s => s with {GlassEnabled = value} );
        
        if (systemChange)
            EventBus.Instance.EmitSignal(EventBus.SignalName.GlassStateChanged, value);
    }

    private bool OnWallpaperModeChanged(Consts.WallPaperModeEnum value)
    {
        bool rectEnable = value == Consts.WallPaperModeEnum.Image;

        if (WallpaperTextureRect != null)
        {
            WallpaperTextureRect.Visible = rectEnable;

            if (!rectEnable && GlassEnabled)
            {
                glassWasDisabledByWallpaperToggle = true;
                OnGlassEnabledChangedSystem(false);
            }
            else if (rectEnable && glassWasDisabledByWallpaperToggle)
            {
                glassWasDisabledByWallpaperToggle = false;
                OnGlassEnabledChangedSystem(true);
            }
        }
        
        ConfigManager.UpdateAppSettings(s => s with {WallpaperMode = value} );
        EventBus.Instance.EmitSignal(EventBus.SignalName.GlassStateChanged, ConfigManager.AppSettings.GlassEnabled);
        EventBus.Instance.EmitSignal(EventBus.SignalName.WallpaperModeApplied, (int)value);
        return true;
    }
    private void OnTintColorChanged(Color value)
    {
        // if(value == GlassTintColor) return;
        if (WindowMaterial is not ShaderMaterial shaderMat) return;
        shaderMat.SetShaderParameter(SHADER_GLASS_TINT_ID, value);
        ConfigManager.UpdateAppSettings(s => s with {GlassTintColor = value} );
    }
    // private void OnWindowColorChanged(Color value)
    // {
    //     if (value == WindowColor) return;
    //     if (WindowStyleBox is not StyleBoxFlat flatBox) return;
    //     
    //     flatBox.BgColor = value;
    //     
    // }
}