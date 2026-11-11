using System;
using Godot;
using System.Threading;
using System.Threading.Tasks;

public class AkosViewController
{
    public AkosData AkosData { get; private set; }
    public ScummBlock CurrentBlock { get; set; }
    public Color[] ExternalPalette { get; set; }
    public AkosParser Parser { get; private set; } = new();
    
    public int SelectedCelIndex { get; set; } = 0;
    public bool CacheDisabled { get; set; } = false;
    public ViewMode CurrentViewMode { get; set; } = ViewMode.Shader;
    
    public enum ViewMode { Shader, Index, Raw }

    private Control _owner;
    private TextureRect _celPreview;
    private Material _akosShaderMaterial;
    private ZoomableViewport _previewContainer;

    private OptionButton _modeOptionButton;
    private CheckButton _alphaToggle;
    private CheckButton _shadowColorToggle;
    private Slider _shadowSlider;
    private OptionButton _filterOptionButton;

    private CancellationTokenSource _decodeCts;
    private ImageTexture _paletteTextureCache;

    public Action<AkosCelInfo> OnCelLabelUpdated;
    public Action OnPaletteUpdated;

    public AkosViewController(Control owner, Material shaderMaterial, ZoomableViewport viewport = null)
    {
        _owner              = owner;
        _akosShaderMaterial = shaderMaterial;
        _previewContainer   = viewport;
        _celPreview         = viewport._celPreview;
    }


    public void BindUI(string controlsBasePath)
    {
        
        _modeOptionButton   = _owner.GetNode<OptionButton>(controlsBasePath+"ModeOptionButton");
        _alphaToggle        = _owner.GetNode<CheckButton>(controlsBasePath+"AlphaToggle");
        _shadowColorToggle  = _owner.GetNode<CheckButton>(controlsBasePath+"ShadowColorToggle");
        _shadowSlider       = _owner.GetNode<Slider>(controlsBasePath+"ShadowSlider");
        _filterOptionButton = _owner.GetNode<OptionButton>(controlsBasePath+"FilterOptionButton");

        _modeOptionButton.ItemSelected    += OnModeSelected;
        _alphaToggle.Toggled              += OnAlphaToggled;
        _shadowColorToggle.Toggled        += OnShadowColorToggled;
        _shadowSlider.ValueChanged        += OnShadowAmountChanged;
        _filterOptionButton.ItemSelected  += OnFilterSelected;

        SetShaderParameter("is_active", true);
    }

    public void UnbindUI()
    {
        if (_modeOptionButton == null) return;

        _modeOptionButton.ItemSelected    -= OnModeSelected;
        _alphaToggle.Toggled              -= OnAlphaToggled;
        _shadowColorToggle.Toggled        -= OnShadowColorToggled;
        _shadowSlider.ValueChanged        -= OnShadowAmountChanged;
        _filterOptionButton.ItemSelected  -= OnFilterSelected;
    }
    public async Task LoadBlockAsync(ScummBlock block)
    {
        CurrentBlock = block;
        AkosData = await Task.Run(() => Parser.Parse(block, ExternalPalette));
        UpdateShaderPalette();
    }

    public void UpdatePaletteIndex(int activePaletteIdx)
    {
        if (AkosData == null || CurrentBlock == null) return;
        Parser.UpdatePaletteOnly(AkosData, CurrentBlock, activePaletteIdx);
        RefreshCelPreview();
    }

    public async void RefreshCelPreview()
    {
        if (SelectedCelIndex < 0 || AkosData?.CelInfos == null || _celPreview == null)
        {
            if (_celPreview != null) _celPreview.Texture = null;
            OnPaletteUpdated?.Invoke();
            return;
        }

        _decodeCts?.Cancel();
        _decodeCts?.Dispose();
        _decodeCts = new CancellationTokenSource();
        var token = _decodeCts.Token;

        int capturedIndex = SelectedCelIndex;
        OnCelLabelUpdated?.Invoke(AkosData.CelInfos[capturedIndex]);

        try
        {
            var result = await AkosCelCache.GetCachedCelAsync(AkosData, capturedIndex, bakeImages: true, CacheDisabled, token);
            if (result.Surface == null || token.IsCancellationRequested) return;
            if (SelectedCelIndex != capturedIndex) return;

            Image displayImage = (CurrentViewMode != ViewMode.Raw) ? result.ShaderDataImage : result.RawUnshadedImage;
            if (displayImage != null)
                _celPreview.Texture = ImageTexture.CreateFromImage(displayImage);

            UpdateUI();
            OnPaletteUpdated?.Invoke();
        }
        catch (OperationCanceledException) { }
    }

    private void UpdateUI()
    {
        if (_celPreview == null) return;

        bool shaderActive = (CurrentViewMode == ViewMode.Shader);
        if (CurrentViewMode == ViewMode.Raw)
        {
            if (_celPreview.Material != null) _celPreview.Material = null;
        }
        else
        {
            if (_celPreview.Material == null) _celPreview.Material = _akosShaderMaterial;
            SetShaderParameter("is_active", shaderActive);
            UpdateShaderPalette();
        }

        if (_alphaToggle != null) _alphaToggle.Disabled = !shaderActive;
        if (_shadowColorToggle != null) _shadowColorToggle.Disabled = !shaderActive;
        if (_shadowSlider != null) _shadowSlider.Editable = shaderActive;

        _previewContainer?.ZoomToFitDeferred();
    }

    public void UpdateShaderPalette()
    {
        if (AkosData?.ResolvedColors == null) return;

        var img = Image.CreateEmpty(256, 1, false, Image.Format.Rgb8);
        for (int i = 0; i < 256; i++)
        {
            Color c = (i < AkosData.ResolvedColors.Length) ? AkosData.ResolvedColors[i] : Colors.Transparent;
            img.SetPixel(i, 0, c);
        }

        _paletteTextureCache = ImageTexture.CreateFromImage(img);
        SetShaderParameter("room_palette", _paletteTextureCache);
    }

    private void SetShaderParameter(string paramName, Variant value)
    {
        if (_celPreview?.Material is ShaderMaterial sm)
            sm.SetShaderParameter(paramName, value);
    }

    private void OnModeSelected(long index) { CurrentViewMode = (ViewMode)index; RefreshCelPreview(); }
    private void OnAlphaToggled(bool toggledOn) => SetShaderParameter("alpha_enabled", toggledOn);
    private void OnShadowColorToggled(bool toggledOn) => SetShaderParameter("shadow_color_enabled", toggledOn);
    private void OnShadowAmountChanged(double value) => SetShaderParameter("shadow_amount", value);
    private void OnFilterSelected(long index)
    {
        if (_celPreview == null) return;
        bool isLinear = index == 1;
        _celPreview.TextureFilter = isLinear ? CanvasItem.TextureFilterEnum.Linear : CanvasItem.TextureFilterEnum.Nearest;
        SetShaderParameter("use_linear_filter", isLinear);
    }   
}