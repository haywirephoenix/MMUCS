using System;
using Godot;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

public partial class ExporterContent : Control
{
    [ExportGroup("Customize")]
    [Export] public ZoomableViewport ZoomableViewport;
    [Export] public Material akosShaderMaterial;
    // [Export] public CheckButton           ExpBgsButton;
    // [Export] public CheckButton ExpObjsButton;
    // [Export] public CheckButton ExpCostsButton;
    
    [Export] public OptionButton _modeOptionButton;
    // [Export] public CheckButton  _optionsToggle;
    // [Export] public CheckButton  _infoToggle;
    [Export] public CheckButton  _alphaToggle;
    [Export] public CheckButton  _shadowColorToggle;
    // [Export] public CheckButton  _vectorToggle;
    [Export] public Slider       _shadowSlider;
    [Export] public OptionButton _filterOptionButton;
    // [Export] public OptionButton _stretchOptionButton;
    // [Export] public CheckButton  _autoZoomToggle;
    // [Export] public MSlider      _paletteSlider;
    
    private AkosData                _akos;
    private AkosParser              _parser = new();
    private ScummBlock              _currentBlock;
    private Color[]                 _externalPalette;
    private CancellationTokenSource _decodeCts;
    private int                     _selectedCelIndex = 0;
    private ViewMode                CurrentViewMode   = ViewMode.Shader;
    private TextureRect             _celPreview;
    private bool                    shaderActive = true;
    private ImageTexture            _paletteTextureCache;
    
    private enum ViewMode { Shader, Index, Raw }
   
    private const string s_controlsPath = "ContentRoot/VBoxContainer/Steps/Step2_Customize/VBoxContainer/Content/Controls";
   
    private static readonly NodePath PathModeOptionButton    =s_controlsPath + "/ModeOptionButton";
    private static readonly NodePath PathAlphaToggle         =s_controlsPath + "/AlphaToggle";
    private static readonly NodePath PathShadowColorToggle   =s_controlsPath + "/ShadowColorToggle";
    private static readonly NodePath PathVectorToggle        =s_controlsPath + "/VectorToggle";
    private static readonly NodePath PathShadowSlider        =s_controlsPath + "/ShadowSlider";
    private static readonly NodePath PathFilterOptionButton  =s_controlsPath + "/FilterOptionButton";
    private static readonly NodePath PathStretchOptionButton =s_controlsPath + "/StretchOptionButton";
    private static readonly NodePath PathAutoZoomToggle      =s_controlsPath + "/AutoZoomToggle";
    private static readonly NodePath PathPaletteSlider       =s_controlsPath + "/PaletteSlider";
    private async void InitCustomize()
    {
        if (!ScummStatics.akosPreviewBlockFound) return;
        _currentBlock = ScummStatics.akosPreviewBlock;
        
        
        _celPreview   = ZoomableViewport._celPreview;
        
        _modeOptionButton    = GetNode<OptionButton>(PathModeOptionButton);
        _alphaToggle         = GetNode<CheckButton>(PathAlphaToggle);
        _shadowColorToggle   = GetNode<CheckButton>(PathShadowColorToggle);
        // _vectorToggle        = GetNode<CheckButton>(PathVectorToggle);
        _shadowSlider        = GetNode<Slider>(PathShadowSlider);
        _filterOptionButton  = GetNode<OptionButton>(PathFilterOptionButton);
        // _stretchOptionButton = GetNode<OptionButton>(PathStretchOptionButton);
        // _autoZoomToggle      = GetNode<CheckButton>(PathAutoZoomToggle);
        // _paletteSlider       = GetNode<MSlider>(PathPaletteSlider);
        
        _modeOptionButton.ItemSelected    += ModeOptionButtonOnItemSelected;
        _alphaToggle.Toggled              += AlphaToggleOnToggled;
        _shadowColorToggle.Toggled        += ShadowColorToggleOnToggled;
        _shadowSlider.ValueChanged        += _OnShadowAmountChanged;
        _filterOptionButton.ItemSelected  += FilterOptionButtonOnItemSelected;
        
        SetShaderParameter("is_active", shaderActive);
        
       
        
        _akos = await Task.Run(() => _parser.Parse(_currentBlock, _externalPalette));

        _UpdateShaderPalette();

        _RefreshCelPreview();
    }

    private void DeInitCustomize()
    {
        _modeOptionButton.ItemSelected   -= ModeOptionButtonOnItemSelected;
        _alphaToggle.Toggled             -= AlphaToggleOnToggled;
        _shadowColorToggle.Toggled       -= ShadowColorToggleOnToggled;
        _shadowSlider.ValueChanged       -= _OnShadowAmountChanged;
        _filterOptionButton.ItemSelected -= FilterOptionButtonOnItemSelected;
    }
    
    private void ModeOptionButtonOnItemSelected(long index)
    {
        CurrentViewMode = (ViewMode)index;
        _RefreshCelPreview();
    }
    
    private void ToggleShader(bool toggledOn)
    {
        SetShaderParameter("is_active", toggledOn);
        _alphaToggle.Disabled       = !toggledOn;
        _shadowColorToggle.Disabled = !toggledOn;
        // _vectorToggle.Disabled      = !toggledOn;
        _shadowSlider.Editable      = toggledOn;
    }
    
    

    private void FilterOptionButtonOnItemSelected(long index)
    {
        bool islinear = index == 1;
        TextureFilterEnum selected = islinear ? TextureFilterEnum.Linear : TextureFilterEnum.Nearest;
        _celPreview.TextureFilter = selected;
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
    
    private async void _RefreshCelPreview()
    {
        if (_selectedCelIndex < 0 || _akos.CelInfos == null)
        {
            return;
        }

        _decodeCts?.Cancel();
        _decodeCts?.Dispose();
        _decodeCts = new CancellationTokenSource();
        var token = _decodeCts.Token;

        int capturedIndex = _selectedCelIndex;
        // int activePaletteIdx = 0;
        // int activePaletteIdx = _akos.DefaultPaletteIndex;

        try
        {
            var result = await AkosCelCache.GetCachedCelAsync(_akos, capturedIndex, bakeImages: true, cacheDisabled:true, token);
            if (result.Surface == null || token.IsCancellationRequested) return;

            // if (_selectedCelIndex != capturedIndex) return;
        
            Image displayImage = (CurrentViewMode != ViewMode.Raw)
                                     ? result.ShaderDataImage
                                     : result.RawUnshadedImage;

            if (displayImage != null)
                _celPreview.Texture = ImageTexture.CreateFromImage(displayImage);
            UpdateUI();
        }
        catch (OperationCanceledException) { }
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

        _alphaToggle.Disabled       = !shaderActive;
        _shadowColorToggle.Disabled = !shaderActive;
        _shadowSlider.Editable      = shaderActive;


        // _previewContainer.ZoomToFit();
        // _previewContainer.CenterCameraDeferred();
        // ZoomCameraToFit();
    }
    
    private void SetShaderParameter(string paramName, Variant value)
    {
        if (_celPreview.Material is ShaderMaterial sm)
            sm.SetShaderParameter(paramName, value);
    }
    
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
    
}