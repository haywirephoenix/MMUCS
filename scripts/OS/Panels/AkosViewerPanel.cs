using System;
using Godot;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;


public partial class AkosViewerPanel : FloatingPanel
{
    public override string PanelTitle => "AKOS Viewer";
    [Export] public bool CacheDisabled {get{return _cacheDisabled;} set{ _cacheDisabled = value;}}
    private bool _cacheDisabled;
    [Export] public Material akosShaderMaterial;
 
    [Export] public TabContainer _tabs;
    
    //options
    [Export] public OptionButton _modeOptionButton;
    [Export] public CheckButton _alphaToggle;
    [Export] public CheckButton _shadowColorToggle;
    [Export] public CheckButton _vectorToggle;
    [Export] public Slider _shadowSlider;
    [Export] public OptionButton _filterOptionButton;
    [Export] public OptionButton _stretchOptionButton;
    [Export] public CheckButton _autoZoomToggle;

    // Tab 0 – Overview
    [Export] public  GridContainer _headerGrid;
    [Export] public  Label _codecLabel;

    // Tab 1 – Cel Browser
    [Export] public ItemList _celList;
    [Export] public TextureRect _celPreview;
    [Export] public Label _celInfoLabel;
    [Export] public Camera2D _camera;
    [Export] public ZoomableViewport _previewContainer;
    

    // Tab 2 – Chore / Sequence tree
    [Export] public Tree _choreTree;
    [Export] public Label _sequenceDetail;

    // Tab 3 – Palette
    [Export] public GridContainer _paletteGrid;
    
    private CancellationTokenSource _decodeCts;

    // ── State
    private AkosData _akos;
    private int _selectedCelIndex = -1;
    private Color[] _externalPalette;
    
    private ViewMode CurrentViewMode = ViewMode.Shader;
    private enum ViewMode { Shader,Index,Raw }
    
    private Vector2 _minZoom = new Vector2(0.5f, 0.5f);
    private Vector2 _maxZoom = new Vector2(10.0f, 10.0f);
    private float _zoomStep = 0.5f;
    
    private static readonly NodePath PathTabs = "Layout/MarginContainer/ContentRoot/Tabs";
    
    private static readonly NodePath PathModeOptionButton = "Layout/MarginContainer/ContentRoot/Tabs/Cels/_HBoxContainer_240/ConfigGrid/Controls/ModeOptionButton";
    private static readonly NodePath PathAlphaToggle = "Layout/MarginContainer/ContentRoot/Tabs/Cels/_HBoxContainer_240/ConfigGrid/Controls/AlphaToggle";
    private static readonly NodePath PathShadowColorToggle = "Layout/MarginContainer/ContentRoot/Tabs/Cels/_HBoxContainer_240/ConfigGrid/Controls/ShadowColorToggle";
    private static readonly NodePath PathVectorToggle = "Layout/MarginContainer/ContentRoot/Tabs/Cels/_HBoxContainer_240/ConfigGrid/Controls/VectorToggle";
    private static readonly NodePath PathShadowSlider = "Layout/MarginContainer/ContentRoot/Tabs/Cels/_HBoxContainer_240/ConfigGrid/Controls/ShadowSlider";
    private static readonly NodePath PathFilterOptionButton = "Layout/MarginContainer/ContentRoot/Tabs/Cels/_HBoxContainer_240/ConfigGrid/Controls/FilterOptionButton";
    private static readonly NodePath PathStretchOptionButton = "Layout/MarginContainer/ContentRoot/Tabs/Cels/_HBoxContainer_240/ConfigGrid/Controls/StretchOptionButton";
    private static readonly NodePath PathAAutoZoomToggle = "Layout/MarginContainer/ContentRoot/Tabs/Cels/_HBoxContainer_240/ConfigGrid/Controls/AutoZoomToggle";
    
    private static readonly NodePath PathheaderGrid = "Layout/MarginContainer/ContentRoot/Tabs/Overview/_VBoxContainer_230/headerGrid";
    private static readonly NodePath PathCodecLabel = "Layout/MarginContainer/ContentRoot/Tabs/Overview/_VBoxContainer_230/CodecLabel";
    private static readonly NodePath PathCelList = "Layout/MarginContainer/ContentRoot/Tabs/Cels/_HBoxContainer_240/Cel List";
   
    private static readonly NodePath PathPreviewContainer = "Layout/MarginContainer/ContentRoot/Tabs/Cels/_HBoxContainer_240/VBoxContainer/PreviewContainer";
    private static readonly NodePath PathCelCamera = "Layout/MarginContainer/ContentRoot/Tabs/Cels/_HBoxContainer_240/VBoxContainer/PreviewContainer/SubViewport/CelCamera";
    private static readonly NodePath PathCelPreview = "Layout/MarginContainer/ContentRoot/Tabs/Cels/_HBoxContainer_240/VBoxContainer/PreviewContainer/SubViewport/Cel Preview";
    
    private static readonly NodePath PathCelInfo = "Layout/MarginContainer/ContentRoot/Tabs/Cels/_HBoxContainer_240/VBoxContainer/Cel Info";

    private static readonly NodePath PathChoreTree = "Layout/MarginContainer/ContentRoot/Tabs/Chores/_HSplitContainer_249/ChoreTree";
    private static readonly NodePath PathSequenceDetail = "Layout/MarginContainer/ContentRoot/Tabs/Chores/_HSplitContainer_249/SequenceDetail";
    private static readonly NodePath PathPaletteGrid = "Layout/MarginContainer/ContentRoot/Tabs/Palette/_VBoxContainer_264/PaletteGrid";
    
    public override void AssignNodes()
    {
        base.AssignNodes();
        
        _tabs = GetNode<TabContainer>(PathTabs);
        _headerGrid = GetNode<GridContainer>(PathheaderGrid);
        _codecLabel = GetNode<Label>(PathCodecLabel);
        _celList = GetNode<ItemList>(PathCelList);
        _celPreview = GetNode<TextureRect>(PathCelPreview);
        _celInfoLabel = GetNode<Label>(PathCelInfo);

        _previewContainer = GetNode<ZoomableViewport>(PathPreviewContainer);
        _camera = GetNode<Camera2D>(PathCelCamera);
            
        _modeOptionButton = GetNode<OptionButton>(PathModeOptionButton);
        _alphaToggle = GetNode<CheckButton>(PathAlphaToggle);
        _shadowColorToggle = GetNode<CheckButton>(PathShadowColorToggle);
        _vectorToggle = GetNode<CheckButton>(PathVectorToggle);
        _shadowSlider = GetNode<Slider>(PathShadowSlider);
        _filterOptionButton =  GetNode<OptionButton>(PathFilterOptionButton);
        _stretchOptionButton =  GetNode<OptionButton>(PathStretchOptionButton);
        _autoZoomToggle =  GetNode<CheckButton>(PathAAutoZoomToggle);
        
        _choreTree = GetNode<Tree>(PathChoreTree);
        _sequenceDetail = GetNode<Label>(PathSequenceDetail);
        _paletteGrid = GetNode<GridContainer>(PathPaletteGrid);
    }
    
    protected override void OnReady()
    {
        _modeOptionButton.ItemSelected += ModeOptionButtonOnItemSelected;
        _alphaToggle.Toggled += AlphaToggleOnToggled;
        _shadowColorToggle.Toggled += ShadowColorToggleOnToggled;
        _shadowSlider.ValueChanged += _OnShadowAmountChanged;
        _vectorToggle.Toggled += OnVectorToggled;
        _filterOptionButton.ItemSelected += FilterOptionButtonOnItemSelected;
        _stretchOptionButton.ItemSelected += StretchOptionButtonOnItemSelected;
        _autoZoomToggle.Toggled += AutoZoomToggleOnToggled;
        
        _choreTree.ItemCollapsed += _OnItemCollapsed;
        _choreTree.ItemSelected += _OnChoreItemSelected;
        _celList.ItemSelected += _OnCelSelected;
        _tabs.TabChanged += _OnTabChanged;
        VectorToggle.ResetClickCount();
        
    }
    private void AutoZoomToggleOnToggled(bool toggledOn)
    {
        if(toggledOn)
            _previewContainer.ZoomToFitDeferred();
        else
            _previewContainer.ResetZoomDeferred();
    }
    
    private void ModeOptionButtonOnItemSelected(long index)
    { 
        CurrentViewMode = (ViewMode)index;
        _RefreshCelPreview();
    }

    private void ToggleShader(bool toggledOn)
    {
        SetShaderParameter("is_active", toggledOn);
        _alphaToggle.Disabled = !toggledOn;
        _shadowColorToggle.Disabled = !toggledOn;
        _vectorToggle.Disabled  = !toggledOn;
        _shadowSlider.Editable =  toggledOn;
    }
    private void StretchOptionButtonOnItemSelected(long index)
    {
        _celPreview.StretchMode = index switch
        {
            0 => TextureRect.StretchModeEnum.KeepAspectCentered,
            1 => TextureRect.StretchModeEnum.KeepCentered,
            _ => _celPreview.StretchMode
        };
        _previewContainer.CenterCameraDeferred();
    }
    
    private void FilterOptionButtonOnItemSelected(long index)
    {
        bool islinear = index == 1;
        TextureFilterEnum selected = islinear ? TextureFilterEnum.Linear : TextureFilterEnum.Nearest;
        _celPreview.TextureFilter = selected;
        GD.Print("index : " + index);
        SetShaderParameter("use_linear_filter", islinear);
    }
    private void ShadowColorToggleOnToggled(bool toggledOn)
    {
        SetShaderParameter("shadow_color_enabled", toggledOn);
    }
   
    private void AlphaToggleOnToggled(bool toggledOn)
    {
        SetShaderParameter("alpha_enabled", toggledOn);
    }
    private void OnVectorToggled(bool toggledOn)
    {
        VectorToggle.OnVectorToggled(toggledOn);
    }
    
   

    private void _OnShadowAmountChanged(double value)
    {
        SetShaderParameter("shadow_amount", value);
    }
    
   
    private void SetShaderParameter(string paramName, Variant value)
    {
        if (_celPreview.Material is ShaderMaterial sm)
            sm.SetShaderParameter(paramName, value);
    }
    
    private void _OnItemCollapsed(TreeItem item)
    {
        if (!item.Collapsed)
        {
            var root = _choreTree.GetRoot();
            // var sibling = root.GetChildren();
            // while (sibling != null)
            // {
            //     if (sibling != item) sibling.Collapsed = true;
            //     sibling = sibling.GetNext();
            // }

            if (item.GetParent() == root)
            {
                Callable.From(() => _ExpandAnimation(item)).CallDeferred();
            }
        }
    }
    
    private void _ExpandAnimation(TreeItem animItem)
    {
        if (!IsInstanceValid(animItem)) return;

        var child = animItem.GetFirstChild();
        while (child != null)
        {
            var next = child.GetNext();
            child.Free();
            child = next;
        }

        int animIdx = animItem.GetMetadata(0).AsInt32();
        int dirs = _akos.DirectionCount;
        
        for (int d = 0; d < dirs; d++)
        {
            int choreIdx = animIdx * dirs + d;
            if (choreIdx >= _akos.ChoreOffsets.Length) continue;

            ushort seqOffset = _akos.ChoreOffsets[choreIdx];
            if (seqOffset == 0) continue;

            var dirItem = _choreTree.CreateItem(animItem);
            dirItem.SetText(0, _GetDirName(d));
            dirItem.Collapsed = true;
        
            var steps = _parser.DecodeSingleSequence(_akos, seqOffset);
            foreach (var step in steps)
            {
                var stepItem = _choreTree.CreateItem(dirItem);
                _FormatStepItem(stepItem, step);
            }
        }
    }
    
    private static readonly Dictionary<AkosStepKind, Color> ChoreColors = new()
    {
        { AkosStepKind.DrawSingle, new Color("88ccff") }, // Light Blue
        { AkosStepKind.DrawMany,   new Color("88ffcc") }, // Seafoam
        { AkosStepKind.End,        new Color("ff8888") }, // Soft Red
        { AkosStepKind.Empty,      new Color("888888") }  // Grey
    };
    
    private void _FormatStepItem(TreeItem item, AkosChoreStep step)
    {
        string label = step.Kind switch
        {
            AkosStepKind.DrawSingle => $"Draw cel {step.CelIndex}",
            AkosStepKind.DrawMany   => $"DrawMany ({step.MultiCels.Count} cels)",
            AkosStepKind.End        => "EndSeq",
            AkosStepKind.Empty      => "EmptyCel",
            _                       => step.OpcodeName ?? $"OP_{step.RawCode:X4}",
        };

        item.SetText(0, label);
        item.SetText(1, $"0x{step.Offset:X4}");
        
        if (!ChoreColors.TryGetValue(step.Kind, out Color stepColor))
            stepColor = new Color("ffcc88"); // Orange (Control Ops / Default)
        
        item.SetCustomColor(0, stepColor);
        
        if (step.Kind == AkosStepKind.DrawSingle)
            item.SetMetadata(0, step.CelIndex);
        else
            item.SetMetadata(0, -1);
        
    }

    private string _GetDirName(int d)
    {
        string[] dirNames4 = { "Right", "Left", "Front", "Back" };
        string[] dirNames8 = { "Right", "UpRight", "Up", "UpLeft", "Left", "DownLeft", "Down", "DownRight" };
        var set = _akos.Header.HasManyDirections ? dirNames8 : dirNames4;
        return d < set.Length ? set[d] : $"Dir {d}";
    }
    
    private async void _OnTabChanged(long tab)
    {
        if (tab == 1 && _akos.Chores == null)
            _LazyLoadChores();
    }

    private AkosParser _parser = new AkosParser();

   
    // ── Data loading ──────────────────────────────────────────────────────────

    protected override async void _OnBlockSelected(ScummBlock obimBlock)
    {
        if (obimBlock.Tag != ScummTag.AKOS) return;
        
        if (_celList.ItemCount > 0 && _celList.IsVisibleInTree())
            FocusManager.SetNextFocusOverride(_celList);

        _parser = new();

        _akos = await Task.Run(() => _parser.Parse(obimBlock, _externalPalette));

        _PopulateOverview();
        _PopulateCelList();
    
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
    
    private async void _LazyLoadChores()
    {
        // prevents double-loading if user clicks the tab multiple times
        if (_akos.Chores != null) return;

        _sequenceDetail.Text = "Parsing animation data...";
        _choreTree.Visible = false;

        try
        {
            await Task.Run(() => _parser._DecodeChores(_akos));

            _PopulateChoreTree();
            _sequenceDetail.Text = "Select an animation to view steps.";
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[AKOS] Failed to lazy-load chores: {ex.Message}");
            _sequenceDetail.Text = "Error loading animation data.";
        }
        finally
        {
            _choreTree.Visible = true;
        }
    }


    public void SetExternalPalette(Color[] palette)
    {
        _externalPalette = palette;
    }

    // ── Overview population ───────────────────────────────────────────────────

    private void _PopulateOverview()
    {
        foreach (Node child in _headerGrid.GetChildren()) child.QueueFree();

        var h = _akos.Header;
        _AddGridRow("Version",     h.VersionNumber.ToString());
        _AddGridRow("Flags",       $"0x{h.CostumeFlags:X4}  ({(h.HasManyDirections ? "8-dir" : "4-dir")}{(h.MirroredCostume ? ", mirrored" : "")})");
        _AddGridRow("Chore count", h.ChoreCount.ToString());
        _AddGridRow("Cel count",   h.CelsCount.ToString());
        _AddGridRow("Layer count", h.LayerCount.ToString());
        _AddGridRow("Anim count",  _akos.AnimCount.ToString());
        _AddGridRow("Direction count", _akos.DirectionCount.ToString());

        _codecLabel.Text = h.Codec switch
        {
            AkosCodec.ByleRLE => "1 – Byle RLE\nUsed by SCUMM v7/v8 (COMI/DIG). Variable-length run-length encoding per scanline.",
            AkosCodec.CdatRLE => "2 – CDAT RLE (BOMP)\nBomp decompressor. Used by earlier SCUMM versions.",
            AkosCodec.MajMin  => "5 – MajMin\nMajority/Minority bit-plane codec. Used by HE games.",
            AkosCodec.TRLE    => "16 – TRLE\nTransparent RLE, HE95+. Supports alpha and shadow tables.",
            _                 => $"Unknown codec {(int)h.Codec}",
        };
    }

    private void _AddGridRow(string key, string value)
    {
        var k = new Label();
        k.Text = key;
        k.AddThemeColorOverride("font_color", new Color("aaaaaa"));
        _headerGrid.AddChild(k);

        var v = new Label();
        v.Text = value;
        _headerGrid.AddChild(v);
    }

    // ── Cel browser population ────────────────────────────────────────────────

    private void _PopulateCelList()
    {
        _celList.Clear();
        if (_akos.CelInfos == null) return;

        for (int i = 0; i < _akos.CelInfos.Length; i++)
        {
            var ci = _akos.CelInfos[i];
            _celList.AddItem($"Cel {i:D3}  {ci.Width}×{ci.Height}");
        }

        if (_akos.CelInfos.Length > 0)
            _celList.Select(0);
    }

    private void _OnCelSelected(long index)
    {
        _selectedCelIndex = (int)index;
        // _celList.GrabFocus();
        _RefreshCelPreview();
    }

    private void EnableMaterial()
    {
        _celPreview.Material = akosShaderMaterial;
    }
    private void DisableMaterial()
    {
        _celPreview.Material = akosShaderMaterial;
    }

    private async void _RefreshCelPreview()
    {
        if (_selectedCelIndex < 0 || _akos.CelInfos == null)
        {
            _celPreview.Texture = null;
            ClearPallette();
            return;
        }

        _decodeCts?.Cancel();
        _decodeCts?.Dispose();
        _decodeCts = new CancellationTokenSource();
        var token = _decodeCts.Token;

        int capturedIndex = _selectedCelIndex;
        _UpdateCelLabels(_akos.CelInfos[capturedIndex]);

        try 
        {
            var surface = await AkosCelCache.GetCachedCelAsync(_akos, capturedIndex, token);
            if (surface == null || token.IsCancellationRequested) return;

            var dataImage = (CurrentViewMode != ViewMode.Raw) 
                ? IndexedRenderer.CreateDataImage(surface, _akos)
                : IndexedRenderer.CreateImage(surface, _akos);

            if (_selectedCelIndex == capturedIndex)
            {
                _celPreview.Texture = ImageTexture.CreateFromImage(dataImage);
                UpdateUI();
                
                _PopulatePalette();
            }
        }
        catch (OperationCanceledException) {  }
    }

    private void UpdateUI()
    {
        bool shaderActive = (CurrentViewMode == ViewMode.Shader);
        if (CurrentViewMode == ViewMode.Raw)
        {
            if (_celPreview.Material != null)
                _celPreview.Material = null;
        }
        else
        {
            if (_celPreview.Material == null)
                _celPreview.Material = akosShaderMaterial;
            SetShaderParameter("is_active", shaderActive);
            
            _UpdateShaderPalette(); 
        }
        
        _alphaToggle.Disabled = !shaderActive;
        _shadowColorToggle.Disabled = !shaderActive;
        _shadowSlider.Editable = shaderActive;
        
        
        // _previewContainer.ZoomToFit();
        // _previewContainer.CenterCameraDeferred();
        ZoomCameraToFit();
    }

    private void ZoomCameraToFit()
    {
        if(_autoZoomToggle.ButtonPressed)
            _previewContainer.ZoomToFitDeferred();
        else
            _previewContainer.ZoomToFitOnceDeferred();
    }
    
    private void _UpdateCelLabels(AkosCelInfo ci)
    {
        _celInfoLabel.Text =
            $"Cel {_selectedCelIndex}  —  {ci.Width} × {ci.Height} px\n" +
            $"RelX: {ci.RelX}  RelY: {ci.RelY}\n" +
            $"MoveX: {ci.MoveX}  MoveY: {ci.MoveY}\n" +
            $"AKCD offset: 0x{_akos.CelOffsets?[_selectedCelIndex].AkcdOffset:X8}\n" +
            $"AKCI offset: 0x{_akos.CelOffsets?[_selectedCelIndex].AkciOffset:X4}";
    }
    
    private ImageTexture _paletteTextureCache;

    private void _UpdateShaderPalette()
    {
        if (_akos.ResolvedColors == null) return;

        var img = Image.CreateEmpty(256, 1, false, Image.Format.Rgb8);
        for (int i = 0; i < 256; i++)
        {
            // Use resolved colors if available, otherwise transparent
            Color c = (i < _akos.ResolvedColors.Length) ? _akos.ResolvedColors[i] : Colors.Transparent;
            img.SetPixel(i, 0, c);
        }

        _paletteTextureCache = ImageTexture.CreateFromImage(img);
        SetShaderParameter("room_palette", _paletteTextureCache);
    }
    
    // ── Chore tree ─────────────────────────────────────────────────

    private void _PopulateChoreTree()
    {
        _choreTree.Clear();
        if (_akos == null) return;

        var root = _choreTree.CreateItem();
        root.SetText(0, $"Costume ({_akos.AnimCount} anims)");

        // Only create the animation entries. Not decode yet.
        for (int i = 0; i < _akos.AnimCount; i++)
        {
            var animItem = _choreTree.CreateItem(root);
            animItem.SetText(0, $"Anim {i}");
            animItem.SetMetadata(0, i); // Store the AnimIndex
            animItem.Collapsed = true;
        
            var placeholder = _choreTree.CreateItem(animItem);
            placeholder.SetText(0, "Loading...");
        }
    }

    private void _OnChoreItemSelected()
    {
        var item = _choreTree.GetSelected();
        if (item == null) return;

        var meta = item.GetMetadata(0);
    
        // Check if this item is a Step (3rd level deep)
        // Root (0) -> Anim (1) -> Dir (2) -> Step (3)
        int depth = 0;
        var temp = item;
        while (temp.GetParent() != null) { temp = temp.GetParent(); depth++; }

        if (depth == 3 && meta.VariantType == Variant.Type.Int)
        {
            int celIdx = meta.AsInt32();
            // Only jump if it's a valid cel
            if (celIdx >= 0)
            {
                _celList.Select(celIdx);
                _OnCelSelected(celIdx);
                _tabs.CurrentTab = 1;
            }
        }
    }
    // ── Palette population ────────────────────────────────────────────────────

    private void ClearPallette()
    {
        foreach (Node child in _paletteGrid.GetChildren()) child.QueueFree();
    }
    
    private void _PopulatePalette()
    {
        ClearPallette();
        if (_akos.ResolvedColors == null) return;
    
        // ResolvedColors length (256)
        for (int i = 0; i < _akos.ResolvedColors.Length; i++)
        {
            
            Color c = _akos.ResolvedColors[i];
            // if (c.R == 1.0f && c.G == 0.0f && c.B == 1.0f) continue; //skip magenta

            var swatch = new ColorRect();
            swatch.Color = c;
            swatch.CustomMinimumSize = new Vector2(20, 20);
            
            swatch.TooltipText = $"Global Index: {i}\nColor: {c}";
            _paletteGrid.AddChild(swatch);
        }
    }

    private void _PopulatePaletted()
    {
        foreach (Node child in _paletteGrid.GetChildren()) child.QueueFree();

        if (_akos.ResolvedColors == null) return;
        
        var paletteSpan = _akos.Palette.Span;

        for (int i = 0; i < _akos.ResolvedColors.Length; i++)
        {
            var swatch = new ColorRect();
            swatch.Color = _akos.ResolvedColors[i];
            swatch.CustomMinimumSize = new Vector2(20, 20);
            swatch.TooltipText = $"[{i}] AKPL:{paletteSpan[i]}  {_akos.ResolvedColors[i]}";
            _paletteGrid.AddChild(swatch);
        }
    }
    
    
    private static Label _MakeSectionLabel(string text)
    {
        var lbl = new Label();
        lbl.Text = text;
        lbl.AddThemeColorOverride("font_color", new Color("e8c07d"));
        lbl.AddThemeFontSizeOverride("font_size", 13);
        return lbl;
    }
}
