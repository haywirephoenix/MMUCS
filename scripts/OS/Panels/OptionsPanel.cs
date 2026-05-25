using Godot;
using System.Linq;


public partial class OptionsPanel : FloatingPanel
{
    //todo: fix wallpaper focus
    //todo: persist more settings
    public override string PanelTitle => "Options";

    #region Dependencies


    private AppSettings AppSettings => ConfigManager.AppSettings;

    #endregion

    #region Layout

    //Export] public VBoxContainer _sidebarContainer;
    //[Export] public Control _contentArea;
    
    // private PanelContainer selectedWallpaperPanel;
    // private readonly List<PanelContainer> wallpaperPanels = new();

    [Export] public Button[] _navButtons;
    [Export] private Control[] _pages;
    private int _activePage = -1;

    #endregion

    #region Style Constants

    // Sidebar
    private static readonly Color SidebarBg       = new(0.13f, 0.13f, 0.15f, 1f);
    private static readonly Color NavNormal        = new(1, 1, 1, 0f);
    private static readonly Color NavHover         = new(1, 1, 1, 0.07f);
    private static readonly Color NavSelected      = new(0.25f, 0.50f, 0.95f, 0.85f);
    private static readonly Color TextPrimary      = new(0.92f, 0.92f, 0.94f, 1f);
    private static readonly Color TextSecondary    = new(0.55f, 0.55f, 0.60f, 1f);
    private static readonly Color DividerColor     = new(1, 1, 1, 0.07f);
    private static readonly Color ContentBg        = new(0.10f, 0.10f, 0.12f, 1f);
    private static readonly Color SectionBg        = new(0.16f, 0.16f, 0.19f, 1f);
    private static readonly Color AccentBlue       = new(0.25f, 0.50f, 0.95f, 1f);

    private const int SidebarWidth      = 175;
    private const int NavButtonHeight   = 36;
    private const int SectionRadius     = 8;
    private const int PagePadding       = 20;
    
    OptionButton resOpt = new OptionButton();
    private readonly Vector2I[] resolutions =
    [
        new(1280, 720),
        new(1366, 768),
        new(1600, 900),
        new(1920, 1080),
        new(2560, 1440),
        new(3840, 2160)
    ];

    #endregion

    #region Page Definitions

    private readonly (string Label, string Icon)[] _navItems =
    {
        ("Appearance", "🎨"),
        ("Display",    "🖥"),
        ("Fonts",      "𝐀"),
        ("General",    "⚙"),
    };

    #endregion

    public override void _Notification(int what) { }
    public override void LoadLayout()
    {
        IsOpen = false;
        
        CompleteInitialization();
    }
    public override void LoadLayoutDeferred()
    {
        IsOpen = false;
        CompleteInitialization();
    }
    
    private bool _optionsInitialized = false;
    
    public override void Open(bool open, bool animate = false, float delay = 0)
    {
        if(!_optionsInitialized) InitOptions();
        base.Open(open, animate, delay);
        SetCentered();
    }
    
    [Export] public ColorPickerButton bgColorPicker;
    [Export] public ColorPickerButton tintColorPicker;
    [Export] public OptionButton modeOpt;
    [Export] public MarginContainer appearanceColor;
    [Export] public CheckButton glassToggle;
    [Export] public VBoxContainer sideBarContainer;
    [Export] public Label scaleLabel;
    [Export] public HSlider scaleSlider;
    [Export] public CheckButton animToggle;
    [Export] private GridContainer wallpaperGrid;
    [Export] private Control wallpaperGridOuter;
    [Export] public CheckButton hiDpiToggle;

    private const string s_optionsPath = "Layout/MarginContainer/ContentRoot/Options";
    private const string s_stackPath = s_optionsPath+"/ContentBackground/ContentScroll/ContentPageStack/Stack";
    private const string s_sidebarPath = s_optionsPath+"/SidebarBackground/SideBar";
    
    private static readonly NodePath PathColorPickerButton197 = s_stackPath+"/AppearancePage/WallpaperCard/SectionPanel/WallpaperRows/BGColorPickerOuter/ColorPickerRow/_ColorPickerButton_197";
    private static readonly NodePath PathOptionButton194 = s_stackPath+"/AppearancePage/WallpaperCard/SectionPanel/WallpaperRows/WallpaperModeOuter/WallpaperModeRow/_OptionButton_194";
    private static readonly NodePath PathBGColorPickerOuter = s_stackPath+"/AppearancePage/WallpaperCard/SectionPanel/WallpaperRows/BGColorPickerOuter";
    private static readonly NodePath PathGlassCheckButton = s_stackPath+"/AppearancePage/SectionCard/SectionPanel/Rows/SettingsRowMargin/SettingsRow/_CheckButton_198";
    private static readonly NodePath PathSideBar = s_sidebarPath;
    private static readonly NodePath PathScaleLabel = s_stackPath+"/DisplayPage/SectionCard/SectionPanel/Rows/_MarginContainer_207/SettingsRow/ScaleContainer/ScaleLabel";
    private static readonly NodePath PathScaleSlider = s_stackPath+"/DisplayPage/SectionCard/SectionPanel/Rows/_MarginContainer_207/SettingsRow/ScaleContainer/ScaleSlider";
    private static readonly NodePath PathAnimToggle = s_stackPath+"/GeneralPage/SectionCard/SectionPanel/Rows/SettingsRowMargin/SettingsRow/AnimToggle";
    private static readonly NodePath PathWallpaperGrid = s_stackPath+"/AppearancePage/WallpaperCard/SectionPanel/WallpaperRows/WallpapersGridOuter/WallpapersGridRow/WallpaperGrid";
    private static readonly NodePath PathWallpapersGridOuter = s_stackPath+"/AppearancePage/WallpaperCard/SectionPanel/WallpaperRows/WallpapersGridOuter";
    
    private static readonly NodePath PathAppearanceNavButton = s_sidebarPath+"/AppearanceNavButton";
    private static readonly NodePath PathDisplayNavButton = s_sidebarPath+"/DisplayNavButton";
    private static readonly NodePath PathFontsNavButton = s_sidebarPath+"/FontsNavButton";
    private static readonly NodePath PathGeneralNavButton = s_sidebarPath+"/GeneralNavButton";
    
    private static readonly NodePath PathAppearancePage = s_stackPath+"/AppearancePage";
    private static readonly NodePath PathDisplayPage = s_stackPath+"/DisplayPage";
    private static readonly NodePath PathFontsPage = s_stackPath+"/FontsPage";
    private static readonly NodePath PathGeneralPage = s_stackPath+"/GeneralPage";

  

    private void UpdateUIState()
    {
        glassToggle.SetPressedNoSignal(AppSettings.GlassEnabled);
        hiDpiToggle.SetPressedNoSignal(AppSettings.HiDPIEnabled);
        // resOpt.Selected
        bgColorPicker.Color = AppSettings.WallpaperColor;
        tintColorPicker.Color = AppSettings.GlassTintColor;
        modeOpt.Selected = (int)AppSettings.WallpaperMode;
        scaleSlider.Value = AppSettings.GuiScale;
        UpdateScaleLabelText(AppSettings.GuiScale);
        
        // bool isColor = AppSettings.WallpaperMode == Consts.WallPaperModeEnum.Color;
        // glassToggle.Disabled = isColor;
        
        OnWallpaperModeChanged((int)ConfigManager.AppSettings.WallpaperMode);
    }

    private void InitOptions()
    {
        _navButtons =
        [
            GetNode<Button>(PathAppearanceNavButton),
            GetNode<Button>(PathDisplayNavButton),
            GetNode<Button>(PathFontsNavButton),
            GetNode<Button>(PathGeneralNavButton)
        ];

        _pages =
        [
            GetNode<BoxContainer>(PathAppearancePage),
            GetNode<BoxContainer>(PathDisplayPage),
            GetNode<BoxContainer>(PathFontsPage),
            GetNode<BoxContainer>(PathGeneralPage),
        ];
        
        UpdateUIState();
        
        bgColorPicker.ColorChanged   += c   => EventBus.Instance.EmitSignal(EventBus.SignalName.WallpaperColorChangeRequested, c);
        tintColorPicker.ColorChanged   += c   => EventBus.Instance.EmitSignal(EventBus.SignalName.WindowColorChangeRequested, c);
        modeOpt.ItemSelected       += i   => EventBus.Instance.EmitSignal(EventBus.SignalName.WallpaperModeChangeRequested, (int)i);
        glassToggle.Toggled        += v   => EventBus.Instance.EmitSignal(EventBus.SignalName.GlassChangeRequested, v);
        hiDpiToggle.Toggled        += v   => EventBus.Instance.EmitSignal(EventBus.SignalName.HiDPIChangeRequested, v);
        animToggle.Toggled         += v   => EventBus.Instance.EmitSignal(EventBus.SignalName.WindowAnimationsChangeRequested, v);
        scaleSlider.DragEnded      += OnScaleSliderDragEnd;
        scaleSlider.ValueChanged   += UpdateScaleLabelText;
        resOpt.ItemSelected        += OnResolutionChanged;
        
        EventBus.Instance.Connect(EventBus.SignalName.GlassStateChanged,
            Callable.From<bool>(v => glassToggle.SetPressedNoSignal(v)));
        
        EventBus.Instance.Connect(EventBus.SignalName.WallpaperModeApplied,
            Callable.From<int>(OnWallpaperModeChanged));

        foreach (var (btn, index) in _navButtons.Select((b, i) => (b, i)))
            btn.Connect(BaseButton.SignalName.Pressed, Callable.From(() => SelectPage(index)));

        PopulateWallpapers();
        SelectPage(0);
        Visible = false;
        _optionsInitialized = true;
    }
    
    private void OnResolutionChanged(long idx)
    {
        var r = resolutions[idx];
        DisplayServer.WindowSetSize(r);
    }

    private void UpdateScaleLabelText(double v)
    {
        scaleLabel.Text = $"{v:0.00}×";
    }
    private void OnScaleSliderDragEnd(bool valueHasChanged)
    {
        if (valueHasChanged)
            EventBus.Instance.EmitSignal(EventBus.SignalName.UIScaleChangeRequested, (float)scaleSlider.Value);
    }

    private void OnWallpaperModeChanged(int newModeIndex)
    {
        var newMode = (Consts.WallPaperModeEnum)newModeIndex;
        bool isColor = newMode == Consts.WallPaperModeEnum.Color;

        appearanceColor.Visible = isColor;
        wallpaperGridOuter.Visible = !isColor;
        glassToggle.Disabled = isColor;
        glassToggle.SetPressedNoSignal(AppSettings.GlassEnabled);
    }
    
    private StyleBoxFlat _styleNormal;
    private StyleBoxFlat _styleSelected;
    private PanelContainer _currentActivePanel;
    
    private void InitializeStyles()
    {
        // Create the base look
        _styleNormal = new StyleBoxFlat {
            BgColor = new Color(0.15f, 0.15f, 0.15f),
            CornerRadiusTopLeft = 12, CornerRadiusTopRight = 12,
            CornerRadiusBottomLeft = 12, CornerRadiusBottomRight = 12,
            BorderWidthLeft = 2, BorderWidthTop = 2, 
            BorderWidthRight = 2, BorderWidthBottom = 2,
            BorderBlend = true,
            BorderColor = Colors.Transparent // No border
        };

        // Create the selected look by duplicating and changing only the border
        _styleSelected = (StyleBoxFlat)_styleNormal.Duplicate();
        _styleSelected.BorderColor = new Color("#4C9AFF"); // Blue border
        _styleSelected.BorderBlend = false;
    }
    
    private void PopulateWallpapers()
    {
        InitializeStyles();
        wallpaperGrid.QueueFreeChildren();
        var activeIndex = ConfigManager.AppSettings.WallpaperIndex;
        
        /*
        string activePath = FileUtils.GetOrCreatePath(ConfigManager.GUISettings.WallpaperPath);
        
        FileUtils.GetOrCreatePath(Consts.WALL_DIR);
        FileUtils.GetOrCreatePath(Consts.WALL_DIR_BLURRED);

        using var dir = DirAccess.Open(Consts.WALL_DIR);
        if (dir == null)
        {
            GD.PrintErr($"FileUtils: Failed to open directory {Consts.WALL_DIR}");
            return;
        }

        dir.ListDirBegin();
        string file;

        while ((file = dir.GetNext()) != "")
        {
            if (dir.CurrentIsDir())
                continue;

            if (!file.EndsWith(".png"))
                continue;
            
            if (file.EndsWith(".import") || file.StartsWith('.'))
                continue;

            string path = FileUtils.CombinePaths(Consts.WALL_DIR, file);

            var tex = GD.Load<Texture2D>(path);*/
        var wallpapers = ThemeManager.WallPapers;
        // var blurWallpapers = ThemeManager.WallPaperBlurs;

        for (int i = 0; i < wallpapers.Length; i++)
        {

            var tex = wallpapers[i];
            
            var btnsize = new Vector2(92, 72);
            
            var borderPanel= new PanelContainer();
            borderPanel.CustomMinimumSize = btnsize;
            
            var maskPanel = new PanelContainer();
            maskPanel.CustomMinimumSize = btnsize;

            // if (path == activePath)
            if (i == activeIndex)
            {
                borderPanel.AddThemeStyleboxOverride("panel", _styleSelected);
                _currentActivePanel = borderPanel;
            }
            else
            {
                borderPanel.AddThemeStyleboxOverride("panel", _styleNormal);
            }
            
            maskPanel.AddThemeStyleboxOverride("panel", _styleNormal);
            
            maskPanel.ClipContents = true;
            maskPanel.ClipChildren = ClipChildrenMode.Only;
            
            
            var button = new Button();
            button.Flat = true;
            button.MouseFilter = Control.MouseFilterEnum.Stop;

            button.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            button.OffsetLeft = 0;
            button.OffsetTop = 0;
            button.OffsetRight = 0;
            button.OffsetBottom = 0;
            //button.Icon = tex;
            //button.ExpandIcon = true;
            button.ClipContents =  true;
            button.ClipChildren = ClipChildrenMode.AndDraw;
            
            
            // Wallpaper image
            
            var texRect = new TextureRect();
            texRect.Texture = tex;
            texRect.ExpandMode = TextureRect.ExpandModeEnum.FitWidthProportional;
            texRect.StretchMode = TextureRect.StretchModeEnum.Scale;
            texRect.CustomMinimumSize = new Vector2(88, 68);
            
            maskPanel.AddChild(texRect);
            maskPanel.AddChild(button);
            borderPanel.AddChild(maskPanel);

            int cuurIndex = i;
            button.Pressed += () =>
            {
                SelectWallpaper(borderPanel,cuurIndex);
            };

            // wallpaperPanels.Add(borderPanel);
            wallpaperGrid.AddChild(borderPanel);
            
        }

        // dir.ListDirEnd();
    }
    private void SelectWallpaper(PanelContainer borderPanel, int index)
    {
        // if(!ThemeManager.UpdateWallpaper(path)) return;
        EventBus.Instance.EmitSignal(EventBus.SignalName.WallpaperChangeRequested, index);
        // Reset the previous panel to normal
        _currentActivePanel?.AddThemeStyleboxOverride("panel", _styleNormal);
    
        // Apply the selected style to the outer borderPanel
        borderPanel.AddThemeStyleboxOverride("panel", _styleSelected);

        _currentActivePanel = borderPanel;
    }

    private void SelectPage(int index)
    {
        if (_activePage == index) return;

        if (_pages.Length == 0)
        {
            GD.Print("Page list is empty");
            return;
        }
        
        index = Mathf.Clamp(index, 0, _pages.Length - 1);
        

        // Deselect old
        if (_activePage >= 0 && _activePage < _navButtons.Length)
            ApplyNavStyle(_navButtons[_activePage], false);

        // Select new
        _activePage = index;
        ApplyNavStyle(_navButtons[index], true);

        // Swap page
        foreach (var page in _pages)
            page.Visible = false;

        if (index < _pages.Length)
            _pages[index].Visible = true;
    }
    
    private static void ApplyNavStyle(Button btn, bool selected)
    {
        var color = selected ? NavSelected : NavNormal;
        var hover = selected ? NavSelected : NavHover;

        btn.AddThemeStyleboxOverride("normal",   MakeFlat(color, 6, new Vector2(6, 0)));
        btn.AddThemeStyleboxOverride("hover",    MakeFlat(hover, 6, new Vector2(6, 0)));
        btn.AddThemeStyleboxOverride("pressed",  MakeFlat(NavSelected, 6, new Vector2(6, 0)));
        btn.AddThemeStyleboxOverride("focus",    MakeFlat(new Color(0, 0, 0, 0), 0));
        btn.AddThemeColorOverride("font_color",  selected ? TextPrimary : TextSecondary);
        btn.AddThemeFontSizeOverride("font_size", 13);
    }
    
    private static StyleBoxFlat MakeFlat(Color color, int radius, Vector2? padding = null)
    {
        var s = new StyleBoxFlat();
        s.BgColor = color;
        s.SetCornerRadiusAll(radius);
        if (padding is Vector2 p)
        {
            s.ContentMarginLeft   = p.X;
            s.ContentMarginRight  = p.X;
            s.ContentMarginTop    = p.Y;
            s.ContentMarginBottom = p.Y;
        }
        return s;
    }

}
