using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
namespace MMUCS.scripts.OS.Panels;

public partial class OBIMViewerPanel : FloatingPanel
{
    public override string PanelTitle => "OBIM Viewer";
    [Export] public bool CacheDisabled;
    [Export] public TextureRect _objectPreview;
    [Export] public Camera2D _camera;
    [Export] public ZoomableViewport _previewContainer;
    [Export] public GridContainer _paletteGrid;
    [Export] public Slider _frameSlider;
    [Export] public Slider _paletteSlider;

    private Color[] ResolvedColors;

    private const string s_ContentRootPath = "Layout/MarginContainer/ContentRoot";
    private const string s_SplitContainerPath = s_ContentRootPath + "/ResponsiveView/SplitContainer";
    private const string s_ControlsPath = s_ContentRootPath + "/Controls/VBoxContainer";
    
    private static readonly NodePath PathFrameSlider= s_ControlsPath + "/FrameSlider";
    private static readonly NodePath PathPaletteSlider= s_ControlsPath + "/PaletteSlider";
    
    private static readonly NodePath PathPreviewContainer = s_SplitContainerPath + "/Preview/PreviewContainer";
    private static readonly NodePath PathObjectCamera = s_SplitContainerPath + "/Preview/PreviewContainer/SubViewport/Object Camera";
   
    private static readonly NodePath PathObjectPreview = s_SplitContainerPath + "/Preview/PreviewContainer/SubViewport/Object Preview";
    private static readonly NodePath PathPaletteGrid= s_SplitContainerPath + "/Palette/PaletteMargin/PaletteGrid";
    
    public override void AssignNodes()
    {
        base.AssignNodes();
        _previewContainer = GetNode<ZoomableViewport>(PathPreviewContainer);
        _frameSlider = GetNode<Slider>(PathFrameSlider);
        _paletteSlider = GetNode<Slider>(PathPaletteSlider);
        _camera = GetNode<Camera2D>(PathObjectCamera);
        _objectPreview = GetNode<TextureRect>(PathObjectPreview);
        _paletteGrid = GetNode<GridContainer>(PathPaletteGrid);
    }
    
    private void ClearPallette()
    {
        foreach (Node child in _paletteGrid.GetChildren()) child.QueueFree();
    }
    
    private void _PopulatePalette()
    {
        ClearPallette();
        if (ResolvedColors == null) return;

        for (int i = 0; i < ResolvedColors.Length; i++)
        {
            
            Color c = ResolvedColors[i];

            var swatch = new ColorRect();
            swatch.Color = c;
            swatch.CustomMinimumSize = new Vector2(20, 20);
            
            swatch.TooltipText = $"Global Index: {i}\nColor: {c}";
            _paletteGrid.AddChild(swatch);
        }
    }

    protected override void OnReady()
    {
        _frameSlider.ValueChanged += FrameSliderOnValueChanged;
        _paletteSlider.ValueChanged += PaletteSliderOnValueChanged;
        
        ResetPaletteSlider();
        ResetFrameSlider();
    }
    private void PaletteSliderOnValueChanged(double value)
    {
        _selectedPaletteIndex = (int)value;
        if (_obimBlock != null)
        {
            GetObjectData(_obimBlock, _selectedBompFrameIndex);
        }
    }
    private void FrameSliderOnValueChanged(double value)
    {
        _selectedBompFrameIndex = (int)value;
        if (_obimBlock != null)
        {
            GetObjectData(_obimBlock, _selectedBompFrameIndex);
        }
    }

    string _currentName = "";
    private int _currentImageCount = 0;
    private bool _bompSelected = false;
    private int _selectedBompOffset = -1;
    private int _selectedBompFrameIndex = 0;
    private int _totalBompFrames = 0;
    
    private int _selectedPaletteOffset = -1;
    private int _selectedPaletteIndex = 0;
    private int _totalPalettes = 0;
    protected override void _OnBlockSelected(ScummBlock block)
    {
        ScummBlock obimBlock = null;

        if (block is { Tag: ScummTag.OBIM })
            obimBlock = block;

        _bompSelected = block.Tag == ScummTag.BOMP;
    
        _selectedBompFrameIndex = 0;
        _selectedPaletteIndex = 0;
        
        ResetFrameSlider();
        ResetPaletteSlider();

        if (_bompSelected)
        {
            _selectedBompOffset = block.Offset;
            _selectedBompFrameIndex = block.TagSiblingIndex;
            obimBlock = block.FindParent(ScummTag.OBIM);
        }
        else if (block.Tag == ScummTag.SMAP)
        {
            obimBlock = block.FindParent(ScummTag.OBIM);
        }
    
        if (obimBlock is { Tag: ScummTag.OBIM } && obimBlock.GetMetaDataDict() != null)
        {
            _currentName = obimBlock.GetMetadataItem(ScummMeta.OBIM.name, out Variant nameVal) ? (string)nameVal : "";
            _currentImageCount = obimBlock.GetMetadataItem(ScummMeta.OBIM.imageCount, out Variant imgCountVal) ? (int)imgCountVal : 0;
            _obimBlock = obimBlock;
            
            if (ScummDecoders.TryGetObimInfo(_obimBlock, out var info))
            {
                _totalBompFrames  = info.TotalFrames;
                _totalPalettes    = info.TotalPalettes;
                _selectedPaletteIndex = info.DefaultPaletteIndex;
            }
            else
            {
                _totalBompFrames  = 0;
                _totalPalettes    = 0;
                _selectedPaletteIndex = 0;
            }

            OnOBIMSelected();
            return;
        }

        OnOBIMDeselected();
    }

    private ScummBlock _obimBlock;
    
 
    private bool _IsNoImgObim()
    {
        return _currentImageCount == 0;
    }
    
    private void OnOBIMSelected()
    {
        SetTitle($"{PanelTitle} - {_currentName}");
        
        UpdateSliders();

        if (_obimBlock != null)
            GetObjectData(_obimBlock, _selectedBompFrameIndex);
    }
    
    private void UpdateSliders()
    {
        _selectedBompFrameIndex = _totalBompFrames > 0
            ? Mathf.Clamp(_selectedBompFrameIndex, 0, _totalBompFrames - 1)
            : 0;

        _selectedPaletteIndex = _totalPalettes > 0
            ? Mathf.Clamp(_selectedPaletteIndex, 0, _totalPalettes - 1)
            : 0;

        if (_totalBompFrames > 1) EnableFrameSlider(); else ResetFrameSlider();
        if (_totalPalettes    > 1) EnablePaletteSlider(); else ResetPaletteSlider();
    }

    

    
    private void EnableFrameSlider()
    {
        // _frameSlider.Visible = true;
        _frameSlider.Editable = true;
        _frameSlider.MinValue = 0;
        _frameSlider.MaxValue = _totalBompFrames - 1;
        _frameSlider.TickCount = _totalBompFrames;
        _frameSlider.SetValueNoSignal(_selectedBompFrameIndex);
    }
    
    private void ResetFrameSlider()
    {
        _frameSlider.Editable = false;
        _frameSlider.TickCount = 0;
        _frameSlider.MinValue = 0;
        _frameSlider.MaxValue = 1;
        _frameSlider.SetValueNoSignal(0);
    }
    
    private void EnablePaletteSlider()
    {
        _paletteSlider.Editable = true;
        _paletteSlider.MinValue = 0;
        _paletteSlider.MaxValue = _totalPalettes - 1;
        _paletteSlider.TickCount = _totalPalettes;
        _paletteSlider.SetValueNoSignal(_selectedPaletteIndex);
    }
    
    private void ResetPaletteSlider()
    {
        
        _paletteSlider.Editable = false;
        _paletteSlider.TickCount = 0;
        _paletteSlider.MinValue = 0;
        _paletteSlider.MaxValue = 1;
        _paletteSlider.SetValueNoSignal(0);
    }
    
    private void OnOBIMDeselected()
    {
        SetTitle(PanelTitle);
        SetOBIMTexture(null);
    }
    
    private void SetOBIMTexture(Image image)
    {
        if (_objectPreview.Texture != null)
        {
            _objectPreview.Texture.Dispose();
            _objectPreview.Texture = null;
        }

        ImageTexture texture = null;

        if (image != null) texture = ImageTexture.CreateFromImage(image);
        
        _objectPreview.Texture = texture;

        ZoomCameraToFit();
    }
    
    private CancellationTokenSource _cts;
    
    private async void GetObjectData(ScummBlock objectBlock, int frameIndex = 0)
    {
        if (_currentImageCount == 0) { SetOBIMTexture(null); return; }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        try
        {
            var decodeResult = await Task.Run(() =>
            {
                if (ScummDecoders.TryDecodeObimFrame(objectBlock, token, frameIndex, _selectedPaletteIndex, out var r))
                    return (OBIMDecoders.ObimDecodeResult?)r;
                return null;
            }, token);

            if (!token.IsCancellationRequested && decodeResult.HasValue)
            {
                ResolvedColors = decodeResult.Value.ResolvedPalette;
                _PopulatePalette();
                SetOBIMTexture(decodeResult.Value.Image);
            }
        }
        catch (OperationCanceledException) { }
    }


    private void ZoomCameraToFit()
    {
        _previewContainer.ZoomToFitDeferred();
    }
}