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
    // [Export] public Label _celInfoLabel;
    [Export] public Camera2D _camera;
    [Export] public ZoomableViewport _previewContainer;
    [Export] public GridContainer _paletteGrid;

    private Color[] ResolvedColors;
    
    private static readonly NodePath PathPreviewContainer = "Layout/MarginContainer/ContentRoot/Tabs/Preview";
    private static readonly NodePath PathObjectCamera = "Layout/MarginContainer/ContentRoot/Tabs/Preview/SubViewport/Object Camera";
    private static readonly NodePath PathObjectPreview = "Layout/MarginContainer/ContentRoot/Tabs/Preview/SubViewport/Object Preview";
    private static readonly NodePath PathPaletteGrid= "Layout/MarginContainer/ContentRoot/Tabs/Palette/_VBoxContainer_264/PaletteGrid";
    
    public override void AssignNodes()
    {
        base.AssignNodes();
        _previewContainer = GetNode<ZoomableViewport>(PathPreviewContainer);
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
    
        // ResolvedColors length (256)
        for (int i = 0; i < ResolvedColors.Length; i++)
        {
            
            Color c = ResolvedColors[i];
            // if (c.R == 1.0f && c.G == 0.0f && c.B == 1.0f) continue; //skip magenta

            var swatch = new ColorRect();
            swatch.Color = c;
            swatch.CustomMinimumSize = new Vector2(20, 20);
            
            swatch.TooltipText = $"Global Index: {i}\nColor: {c}";
            _paletteGrid.AddChild(swatch);
        }
    }

    protected override void OnReady()
    {
    }
    
    string _currentName = "";
    private int _currentImageCount = 0;
    protected override void _OnBlockSelected(ScummBlock block)
    {
        ScummBlock obimBlock = null;
        
        if(block is { Tag: ScummTag.OBIM })
            obimBlock = block;
        else if (block.Tag == ScummTag.BOMP)
            obimBlock = block.FindParent(ScummTag.OBIM);
        else if (block.Tag == ScummTag.SMAP)
            obimBlock = block.FindParent(ScummTag.OBIM);
        
        if (obimBlock is { Tag: ScummTag.OBIM } && obimBlock.GetMetaDataDict() != null)
        {
            _currentName = obimBlock.GetMetadataItem(ScummMeta.OBIM.name, out Variant nameVal) 
                ? (string)nameVal : "";

            _currentImageCount = obimBlock.GetMetadataItem(ScummMeta.OBIM.imageCount, out Variant imgCountVal) 
                ? (int)imgCountVal : 0;

            _obimBlock = obimBlock;
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
        if(_obimBlock != null)
            GetObjectData(_obimBlock);
        if(_currentImageCount == 0)
            SetOBIMTexture(null);
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
    
    private async void GetObjectData(ScummBlock objectBlock, int objectId = -1)
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
                    objectBlock, token,
                    out Image decoded, out byte[] rawBuffer, out int pitch, out Color[] resolvedPalette))
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

    
    /*
    private async void GetOBIMData()
    {
        if (_IsNoImgObim()) { SetOBIMTexture(null); return; }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        // int roomId = _roomNo;
        // var rmhd = _rmhdBlock;
        // var room = _roomBlock;

        try
        {
            Image resultImage = await Task.Run(() =>
            {
                // if (!CacheDisabled && ScummBackgroundCache.TryLoadFromCache(roomId, out int w, out int h, out int p, out byte[] cachedIndices))
                // {
                //     var apal = room.FindChildRecursive(ScummTag.APAL);
                //     return ScummBackgroundCache.CreateImageFromIndexed(cachedIndices, w, h, p, apal.DataSpan);
                // }
                
                // if (ScummDecoders.DecodeBackgroundImage(roomId, rmhd, room, token, 
                //     out Image decodedImage, out byte[] rawBuffer, out int pitch))
                // {
                //     string path = ScummBackgroundCache.GetBackgroundCachePath(roomId);
                //     ScummBackgroundCache.SaveIndexedBackground(path, (int)rmhd.GetMetadataItem(ScummMeta.RMHD.width), (int)rmhd.GetMetadataItem(ScummMeta.RMHD.height), pitch, rawBuffer);
                //
                //     return decodedImage;
                // }
                
                if(ScummDecoders.DecodeSMAP())

                return null;
            }, token);

            if (!token.IsCancellationRequested)
            {
                SetOBIMTexture(resultImage);
                // SelectObjectInList(0);
            }
        }
        catch (OperationCanceledException) { }
    }
    
    private async void _RefreshCelPreview()
    {
        if (_selectedCelIndex < 0 || _akos.CelInfos == null)
        {
            _objectPreview.Texture = null;
            // ClearPallette();
            return;
        }
        
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        // int capturedIndex = _selectedCelIndex;
        // _UpdateCelLabels(_akos.CelInfos[capturedIndex]);

        try 
        {
            var surface = await ScummDecoders.GetCachedCelAsync(_akos, capturedIndex, token);
            if (surface == null || token.IsCancellationRequested) return;
            var dataImage = IndexedRenderer.CreateDataImage(surface, _akos);

            // var dataImage = (CurrentViewMode != ViewMode.Raw) 
            //     ? IndexedRenderer.CreateDataImage(surface, _akos)
            //     : IndexedRenderer.CreateImage(surface, _akos);

            // if (_selectedCelIndex == capturedIndex)
            // {
                _objectPreview.Texture = ImageTexture.CreateFromImage(dataImage);
                ZoomCameraToFit();
                
                // _PopulatePalette();
            // }
        }
        catch (OperationCanceledException) {  }
    }*/
    
    private void ZoomCameraToFit()
    {
        // if(_autoZoomToggle.ButtonPressed)
            _previewContainer.ZoomToFitDeferred();
        // else
            // _previewContainer.ZoomToFitOnceDeferred();
    }
}