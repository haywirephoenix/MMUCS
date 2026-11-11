using System;
using Godot;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

public partial class AkosViewerContent : Control
{
    [Export] public FloatingPanel ParentPanel;
    
    [Export] public bool CacheDisabled { get { return _cacheDisabled; } set { _cacheDisabled = value; } }
    private bool _cacheDisabled;
    [Export] public Material akosShaderMaterial;

    [Export] public TabContainer _tabs;

    //options
    [Export] public OptionButton _modeOptionButton;
    [Export] public CheckButton _optionsToggle;
    [Export] public CheckButton _infoToggle;
    [Export] public CheckButton _alphaToggle;
    [Export] public CheckButton _shadowColorToggle;
    [Export] public CheckButton _vectorToggle;
    [Export] public Slider _shadowSlider;
    [Export] public OptionButton _filterOptionButton;
    [Export] public OptionButton _stretchOptionButton;
    [Export] public CheckButton _autoZoomToggle;
    [Export] public MSlider _paletteSlider;

    // Tab 1 – Cel Browser
    [Export] public ItemList _celList;
    [Export] public TextureRect _celPreview;
    [Export] public Label _celInfoLabel;
    [Export] public Camera2D _camera;
    [Export] public ZoomableViewport _previewContainer;
    
    // ── State
    private AkosData                _akos;
    private int                     _selectedCelIndex = -1;
    private Color[]                 _externalPalette;
    private AkosParser              _parser = new AkosParser();
    private ScummBlock              _currentBlock;
    private CancellationTokenSource _decodeCts;

    private ViewMode CurrentViewMode = ViewMode.Shader;
    private enum ViewMode { Shader, Index, Raw }


    private const string s_tabsPath = "ContentRoot/Tabs";
    private const string s_celsVBox = s_tabsPath + "/Cels/VBoxContainer";
    private const string s_previewRow = s_celsVBox + "/PreviewRow";
    private const string s_configGridPath = s_previewRow + "/ConfigGrid";
    private const string s_previewWrapperPath = s_previewRow + "/PreviewWrapper";

    // Main Containers
    private static readonly NodePath PathTabs = s_tabsPath;
    private static readonly NodePath PathConfigGrid = s_configGridPath;

    // Options Row (Top of Cels tab)
    private static readonly NodePath PathOptionsToggle = s_celsVBox + "/OptionsRow/OptionsToggle";
    private static readonly NodePath PathInfoToggle = s_celsVBox + "/OptionsRow/InfoToggle";

    // Config Grid Controls
    private static readonly NodePath PathModeOptionButton = s_configGridPath + "/Controls/ModeOptionButton";
    private static readonly NodePath PathAlphaToggle = s_configGridPath + "/Controls/AlphaToggle";
    private static readonly NodePath PathShadowColorToggle = s_configGridPath + "/Controls/ShadowColorToggle";
    private static readonly NodePath PathVectorToggle = s_configGridPath + "/Controls/VectorToggle";
    private static readonly NodePath PathShadowSlider = s_configGridPath + "/Controls/ShadowSlider";
    private static readonly NodePath PathFilterOptionButton = s_configGridPath + "/Controls/FilterOptionButton";
    private static readonly NodePath PathStretchOptionButton = s_configGridPath + "/Controls/StretchOptionButton";
    private static readonly NodePath PathAAutoZoomToggle = s_configGridPath + "/Controls/AutoZoomToggle";
    private static readonly NodePath PathPaletteSlider = s_configGridPath + "/Controls/PaletteSlider";
    
    // Preview Row Items
    private static readonly NodePath PathCelList = s_previewRow + "/Cel List";
    private static readonly NodePath PathCelInfo = s_previewWrapperPath + "/Cel Info";

    // Viewport & Camera Sub-Tree
    private static readonly NodePath PathPreviewContainer = s_previewWrapperPath + "/PreviewContainer";
    private static readonly NodePath PathCelCamera = s_previewWrapperPath + "/PreviewContainer/SubViewport/CelCamera";
    private static readonly NodePath PathCelPreview = s_previewWrapperPath + "/PreviewContainer/SubViewport/Cel Preview";

   
    public void AssignNodes()
    {
        _tabs = GetNode<TabContainer>(PathTabs);
        _headerGrid = GetNode<GridContainer>(PathheaderGrid);
        _configGrid = GetNode<GridContainer>(PathConfigGrid);
        _codecLabel = GetNode<Label>(PathCodecLabel);
        _celList = GetNode<ItemList>(PathCelList);
        _celPreview = GetNode<TextureRect>(PathCelPreview);
        _celInfoLabel = GetNode<Label>(PathCelInfo);

        _previewContainer = GetNode<ZoomableViewport>(PathPreviewContainer);
        _camera = GetNode<Camera2D>(PathCelCamera);

        _modeOptionButton    = GetNode<OptionButton>(PathModeOptionButton);
        _optionsToggle       = GetNode<CheckButton>(PathOptionsToggle);
        _infoToggle          = GetNode<CheckButton>(PathInfoToggle);
        _alphaToggle         = GetNode<CheckButton>(PathAlphaToggle);
        _shadowColorToggle   = GetNode<CheckButton>(PathShadowColorToggle);
        _vectorToggle        = GetNode<CheckButton>(PathVectorToggle);
        _shadowSlider        = GetNode<Slider>(PathShadowSlider);
        _filterOptionButton  = GetNode<OptionButton>(PathFilterOptionButton);
        _stretchOptionButton = GetNode<OptionButton>(PathStretchOptionButton);
        _autoZoomToggle      = GetNode<CheckButton>(PathAAutoZoomToggle);
        _paletteSlider       = GetNode<MSlider>(PathPaletteSlider);
        
        _choreTree = GetNode<Tree>(PathChoreTree);
        _sequenceDetail = GetNode<Label>(PathSequenceDetail);
        _paletteGrid = GetNode<GridContainer>(PathPaletteGrid);
    }
    
    public override void _Notification(int what)
    {
        base._Notification(what);

        if (what == NotificationResized)
        {
            _previewContainer?.ZoomToFitDeferred();
        }
    }
    
    public void OnReady()
    {
        _optionsToggle.Toggled            += OptionsToggleOnToggled;
        _infoToggle.Toggled               += InfoToggleOnToggled;
        _modeOptionButton.ItemSelected    += ModeOptionButtonOnItemSelected;
        _alphaToggle.Toggled              += AlphaToggleOnToggled;
        _shadowColorToggle.Toggled        += ShadowColorToggleOnToggled;
        _shadowSlider.ValueChanged        += _OnShadowAmountChanged;
        _vectorToggle.Toggled             += OnVectorToggled;
        _filterOptionButton.ItemSelected  += FilterOptionButtonOnItemSelected;
        _stretchOptionButton.ItemSelected += StretchOptionButtonOnItemSelected;
        _autoZoomToggle.Toggled           += AutoZoomToggleOnToggled;
        _paletteSlider.IndexChanged       += OnPaletteIndexChanged;

        _choreTree.ItemCollapsed           += _OnItemCollapsed;
        _choreTree.ItemSelected            += _OnChoreItemSelected;
        _celList.ItemSelected              += _OnCelSelected;
        _tabs.TabChanged                   += _OnTabChanged;
        VectorToggle.ResetClickCount();
    }

    public void OnExit()
    {
        _optionsToggle.Toggled            -= OptionsToggleOnToggled;
        _infoToggle.Toggled               -= InfoToggleOnToggled;
        _modeOptionButton.ItemSelected    -= ModeOptionButtonOnItemSelected;
        _alphaToggle.Toggled              -= AlphaToggleOnToggled;
        _shadowColorToggle.Toggled        -= ShadowColorToggleOnToggled;
        _shadowSlider.ValueChanged        -= _OnShadowAmountChanged;
        _vectorToggle.Toggled             -= OnVectorToggled;
        _filterOptionButton.ItemSelected  -= FilterOptionButtonOnItemSelected;
        _stretchOptionButton.ItemSelected -= StretchOptionButtonOnItemSelected;
        _autoZoomToggle.Toggled           -= AutoZoomToggleOnToggled;
        _paletteSlider.IndexChanged       -= OnPaletteIndexChanged;
        _choreTree.ItemCollapsed          -= _OnItemCollapsed;
        _choreTree.ItemSelected           -= _OnChoreItemSelected;
        _celList.ItemSelected             -= _OnCelSelected;
        _tabs.TabChanged                  -= _OnTabChanged;
    }

    
    private async void _OnTabChanged(long tab)
    {
        if (tab == 1 && _akos.Chores == null)
            _LazyLoadChores();
    }


    public async void OnBlockSelected(ScummBlock block)
    {
        if(!ParentPanel.IsOpen) return;
        if (block.Tag != ScummTag.AKOS)
        {
            ClearPallette();
            _paletteSlider.Reset();
            return;
        }
        _currentBlock = block;
        
        EventBus.Instance.EmitSignal(EventBus.SignalName.AkosSelected);

        if (_celList.ItemCount > 0 && _celList.IsVisibleInTree())
            FocusManager.SetNextFocusOverride(_celList);

        _parser = new();
        _paletteSlider.Reset();

        _akos = await Task.Run(() => _parser.Parse(block, _externalPalette));

        _PopulateOverview();
        _PopulateCelList();
        
        SetupPaletteSlider(block);

        if (_celList.ItemCount > 0)
        {
            _celList.Select(0);
            _OnCelSelected(0);
        }

        if (_tabs.CurrentTab == 2)
        {
            _LazyLoadChores();
        }
        _previewContainer.ResetHasFitOnce();
        AutoZoomToggleOnToggled(_autoZoomToggle.ButtonPressed);
    }
   
}