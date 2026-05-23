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

    private Color[] ResolvedColors;
    
    private static readonly NodePath PathPreviewContainer = "Layout/MarginContainer/ContentRoot/Tabs/Preview/PreviewContainer";
    private static readonly NodePath PathObjectCamera = "Layout/MarginContainer/ContentRoot/Tabs/Preview/PreviewContainer/SubViewport/Object Camera";
    private static readonly NodePath PathFrameSlider= "Layout/MarginContainer/ContentRoot/Tabs/Preview/FrameSlider";
    private static readonly NodePath PathObjectPreview = "Layout/MarginContainer/ContentRoot/Tabs/Preview/PreviewContainer/SubViewport/Object Preview";
    private static readonly NodePath PathPaletteGrid= "Layout/MarginContainer/ContentRoot/Tabs/Palette/_VBoxContainer_264/PaletteGrid";
    
    public override void AssignNodes()
    {
        base.AssignNodes();
        _previewContainer = GetNode<ZoomableViewport>(PathPreviewContainer);
        _frameSlider = GetNode<Slider>(PathFrameSlider);
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
    protected override void _OnBlockSelected(ScummBlock block)
    {
        ScummBlock obimBlock = null;

        if (block is { Tag: ScummTag.OBIM })
            obimBlock = block;

        _bompSelected = block.Tag == ScummTag.BOMP;
    
        _selectedBompFrameIndex = 0;
        
        ResetSlider();

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

            var imag = _obimBlock.FindChildRecursive(ScummTag.IMAG);
            if (imag != null)
            {
                _totalBompFrames = 0;
                var bomps = imag.FindChildrenRecursive(ScummTag.BOMP);
                foreach (var child in bomps)
                {
                    if (child.Tag == ScummTag.BOMP) _totalBompFrames++;
                }
            }
            else
            {
                _totalBompFrames = 0;
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

        if (_totalBompFrames > 0)
        {
            _selectedBompFrameIndex = Mathf.Clamp(_selectedBompFrameIndex, 0, _totalBompFrames - 1);
        }
        else
        {
            _selectedBompFrameIndex = 0;
        }

        if (_totalBompFrames > 1)
        {
            _frameSlider.Editable = true;
            _frameSlider.MinValue = 0;
            _frameSlider.MaxValue = _totalBompFrames - 1;
            _frameSlider.TickCount = _totalBompFrames;
            _frameSlider.SetValueNoSignal(_selectedBompFrameIndex);
        }
        else
        {
            ResetSlider();
        }

        if (_obimBlock != null)
            GetObjectData(_obimBlock, _selectedBompFrameIndex);
    }

    private void ResetSlider()
    {
        _frameSlider.Editable = false;
        _frameSlider.TickCount = 0;
        _frameSlider.MinValue = 0;
        _frameSlider.MaxValue = 0;
        _frameSlider.SetValueNoSignal(0);
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

        if (image != null)
            texture = ImageTexture.CreateFromImage(image);
        
        _objectPreview.Texture = texture;

        ZoomCameraToFit();
    }
    
    private CancellationTokenSource _cts;
    
    private async void GetObjectData(ScummBlock objectBlock, int frameIndex = 0)
    {
        if (_currentImageCount == 0)
        {
            SetOBIMTexture(null);
            return;
        }
        _cts?.Cancel();
        _cts = new();
        var token = _cts.Token;
    
        try
        {
            Image resultImage = await Task.Run(() =>
            {
                if (ScummDecoders.DecodeObjectImage(
                    objectBlock, token, frameIndex, out Image decoded, out byte[] rawBuffer, out int pitch, out Color[] resolvedPalette))
                {
                    ResolvedColors = resolvedPalette;
                    return decoded;
                }
                return null;
            }, token);

            if (!token.IsCancellationRequested)
            {
                _PopulatePalette();
                SetOBIMTexture(resultImage);
            }
        }
        catch (OperationCanceledException) { }
    }


    private void ZoomCameraToFit()
    {
        _previewContainer.ZoomToFitDeferred();
    }
}