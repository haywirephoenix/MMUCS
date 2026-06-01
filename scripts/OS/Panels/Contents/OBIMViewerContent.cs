using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;

public partial class OBIMViewerContent  : Control
{
    [Export] public FloatingPanel ParentPanel;
    
    [Export] public bool CacheDisabled;
    [Export] public TextureRect _objectPreview;
    [Export] public Camera2D _camera;
    [Export] public ZoomableViewport _previewContainer;
    [Export] public GridContainer _paletteGrid;
    [Export] public MSlider _frameSlider;
    [Export] public MSlider _paletteSlider;
    private const float paletteSwatchSize = 10;

    private Color[] ResolvedColors;

    private const string s_ContentRootPath = "ContentRoot";
    private const string s_SplitContainerPath = s_ContentRootPath + "/ResponsiveView/SplitContainer";
    private const string s_ControlsPath = s_ContentRootPath + "/Controls/VBoxContainer";
    
    private static readonly NodePath PathFrameSlider= s_ControlsPath + "/FrameSlider";
    private static readonly NodePath PathPaletteSlider= s_ControlsPath + "/PaletteSlider";
    
    private static readonly NodePath PathPreviewContainer = s_SplitContainerPath + "/Preview/PreviewContainer";
    private static readonly NodePath PathObjectCamera = s_SplitContainerPath + "/Preview/PreviewContainer/SubViewport/Object Camera";
   
    private static readonly NodePath PathObjectPreview = s_SplitContainerPath + "/Preview/PreviewContainer/SubViewport/Object Preview";
    private static readonly NodePath PathPaletteGrid= s_SplitContainerPath + "/Palette/PaletteMargin/PaletteGrid";
    
    public void AssignNodes()
    {
        _previewContainer = GetNode<ZoomableViewport>(PathPreviewContainer);
        _frameSlider = GetNode<MSlider>(PathFrameSlider);
        _paletteSlider = GetNode<MSlider>(PathPaletteSlider);
        _camera = GetNode<Camera2D>(PathObjectCamera);
        _objectPreview = GetNode<TextureRect>(PathObjectPreview);
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
        _frameSlider.IndexChanged += OnFrameIndexChanged;
        _paletteSlider.IndexChanged += OnPaletteIndexChanged; 
        
        _frameSlider.Reset();
        _paletteSlider.Reset();
    }
    
    public void OnExit()
    {
        _frameSlider.IndexChanged -= OnFrameIndexChanged;
        _paletteSlider.IndexChanged -= OnPaletteIndexChanged;
    }
    
    private void OnPaletteIndexChanged(int newIndex)
    {
        if (_obimBlock != null) GetObjectData(_obimBlock, _frameSlider.CurrentIndex);
    }

    private void OnFrameIndexChanged(int newIndex)
    {
        if (_obimBlock != null) GetObjectData(_obimBlock, newIndex);
    }

    string _currentName = "";
    private int _currentImageCount = 0;
    private bool _bompSelected = false;

    public void OnBlockSelected(ScummBlock block)
    {
        if(!ParentPanel.IsOpen) return;
        
        ScummBlock obimBlock = null;

        if (block is { Tag: ScummTag.OBIM })
            obimBlock = block;

        _bompSelected = block.Tag == ScummTag.BOMP;
        
        _frameSlider.Reset();
        _paletteSlider.Reset();

        if (_bompSelected)
        {
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
                _frameSlider.Setup(info.TotalFrames, _bompSelected ? block.TagSiblingIndex : 0);
                _paletteSlider.Setup(info.TotalPalettes, info.DefaultPaletteIndex);
            }
            else
            {
                _frameSlider.Reset();
                _paletteSlider.Reset();
            }

            OnOBIMSelected();
            EventBus.Instance.EmitSignal(EventBus.SignalName.ObimSelected);
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
        ParentPanel.SetTitle($"{ParentPanel.PanelTitle} - {_currentName}");

        if (_obimBlock != null)
            GetObjectData(_obimBlock, _frameSlider.CurrentIndex);
    }
    
    private void OnOBIMDeselected()
    {
        ParentPanel.SetTitle(ParentPanel.PanelTitle);
        SetOBIMTexture(null);
        ClearPallette();
        _frameSlider.Reset();
        _paletteSlider.Reset();
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
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        try
        {
            var result = await ObimCache.GetCachedObimAsync(
                objectBlock, 
                frameIndex, 
                _paletteSlider.CurrentIndex, 
                bakeImage: true, 
                token);

            if (!token.IsCancellationRequested && result.Surface != null)
            {
                ResolvedColors = result.ResolvedPalette;
                _PopulatePalette();
                SetOBIMTexture(result.ExecutedImage);
            }
        }
        catch (OperationCanceledException) { }
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
            swatch.CustomMinimumSize = new Vector2(paletteSwatchSize, paletteSwatchSize);
            
            swatch.TooltipText = $"Global Index: {i}\nColor: {c}";
            _paletteGrid.AddChild(swatch);
        }
    }

    private void ZoomCameraToFit()
    {
        _previewContainer.ZoomToFitDeferred();
    }
}