using Godot;

public partial class MainCanvas : VBoxContainer
{
    
    [Export] public Control _canvas;
    [Export] public MMenuBar _menuBar;
    [Export] public StatusBar _statusBar;
    
    //move to window manager?
    [Export] public BlockHierarchyPanel _blockHierarchyPanel;
    [Export] public HexPanel _hexPanel;
    [Export] public RoomPreviewPanel _roomPanel;
    [Export] public MetadataPanel _metadataPanel;
    [Export] public AkosViewerPanel _akosPanel;
    [Export] public OptionsPanel _optionsPanel;
    
    private static readonly NodePath PathCanvas = "Canvas";
    private static readonly NodePath PathMenuBar = "../MenuBarContainer/MenuBar";
    private static readonly NodePath PathStatusBar = "../StatusBarContainer/StatusBar";
    private static readonly NodePath PathBlockHierarchy = "Canvas/Block Hierarchy";
    private static readonly NodePath PathHexView = "Canvas/Hex View";
    private static readonly NodePath PathRoomPreview = "Canvas/Room Preview";
    private static readonly NodePath PathMetadata = "Canvas/Metadata";
    private static readonly NodePath PathAKOSViewer = "Canvas/AKOS Viewer";
    private static readonly NodePath PathOptions = "Canvas/Options";

    
    public FloatingPanel GetPanelByID(long id) => id switch
    {
        Consts.TREE_PNL_ID => _blockHierarchyPanel,
        Consts.HEX_PNL_ID => _hexPanel,
        Consts.ROOM_PNL_ID => _roomPanel,
        Consts.META_PNL_ID => _metadataPanel,
        Consts.AKOS_PNL_ID => _akosPanel,
        Consts.OPTS_PNL_ID => _optionsPanel,
        _  => null,
    };

    private Vector2 _canvasSize;
    private Vector2 _canvasHalfSize;
    
    // private readonly MMenuBar _menuBar = new();
    // private readonly StatusBar _statusBar = new();
    
    private static MainCanvas Instance;

    public override void _EnterTree()
    {
        PhysicsServer2D.Singleton.SetActive(false);
        NavigationServer2D.Singleton.SetActive(false);
        
        PhysicsServer3D.Singleton.SetActive(false);
        NavigationServer3D.Singleton.SetActive(false);
        
        OS.LowProcessorUsageMode = true;
    }


    public override void _Ready()
    {
        Instance = this;
        
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        EventBus.Instance.FileParsed += OnScummblockLoaded;

        // OpenAllWindows();
        HideAllWindows();
        
        LoadLastFile();
    }

    private void LoadLastFile()
    {
        //todo: change guisettings, load from here?
        if (!ConfigManager.GUISettings.AutoLoadLastFile) return;
        
        var path = ConfigManager.GUISettings.LastFilePath;

        if (!FileUtils.PathExists(path))
        {
            MMenuBar.OpenFileBrowser();
            return;
        }

        OnFileSelected(path);
    }

    private void HideAllWindows()
    {
        for (int i = 0; i < Consts.MAIN_PANEL_COUNT; i++)
        {
            var panel = GetPanelByID(i);
            panel.Visible = false;
        }
    }

    private void OpenAllWindows()
    {
        float delayStep = 0.05f;
        for (int i = 0; i < Consts.MAIN_PANEL_COUNT; i++)
        {
            var panel = GetPanelByID(i);
            // if(panel == _hexPanel) continue;
            // if(panel.Visible)
                panel?.PlayOpenAnimation(i * delayStep);
        }
    }

    public void OnScummblockLoaded(ScummBlock block)
    {
        _blockHierarchyPanel.LoadBlocks(block);
        OpenAllWindows();
    }
    
    public void OnFileSelected(string path)
    {
        // GD.Print(path);
        _statusBar.OnFileSelected(path);
    }
    
    public void ToggleVisible(int panelID)
    {
        var panel = GetPanelByID(panelID);
        // panel.Visible = !panel.Visible;
        // panel.MoveToFront();
        
        panel?.AnimateVisible(!panel.Visible);
    }
   
}