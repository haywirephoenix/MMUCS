using Godot;

public partial class ZoomableViewport : SubViewportContainer
{
    [Export] public Camera2D _camera;
    [Export] public TextureRect _celPreview;
    [Export] public Sprite2D _celSprite;

    [Export] public float ZoomStep = 0.5f;
    [Export] public bool FitOncePerGroup = true;

    private float _currentZoom = 1.0f;
    private bool _panning;
    
    private Vector2 _panStart;
    
    private float _zoomMin = 1.0f; 
    private float _zoomMax = 2.0f; 
   
    private const float FitPadding = 0.95f; 
    
    public void ZoomToFitDeferred() => CallDeferred(nameof(ZoomToFit));
    public void ZoomToFitOnceDeferred() => CallDeferred(nameof(ZoomToFitOnce));
    public void CenterCameraDeferred() => CallDeferred(nameof(CenterCameraOnTexture));
    public void ResetZoomDeferred() => CallDeferred(nameof(ResetZoom));
    
    private bool _hasFitOnce = false;
    
    public void ResetHasFitOnce() => _hasFitOnce = false;
    
    
    public override void _Ready()
    {
        CallDeferred(nameof(InitializeZoomConstraints));
        Resized += InitializeZoomConstraints;
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mb)
        {
            if (mb.IsPressed())
            {
                if (mb.ButtonIndex == MouseButton.WheelUp)
                {
                    ZoomCamera(ZoomStep);
                }
                else if (mb.ButtonIndex == MouseButton.WheelDown)
                {
                    ZoomCamera(-ZoomStep);
                }else if (mb.ButtonIndex == MouseButton.Right)
                {
                    ResetZoom();
                }
            }
            
            if (mb.ButtonIndex == MouseButton.Left)
            {
                _panning = mb.Pressed;
                _panStart = mb.GlobalPosition;
            }
        }
        if (@event is InputEventMouseMotion motion && _panning)
        {
            _camera.Position -= motion.Relative / _currentZoom;
        }
    }

    private Vector2 GetTargetSize()
    {
        Texture2D targetTexture = null;
        Vector2 textureSize = Vector2.One;
        
        if(_celPreview?.Texture != null)
            targetTexture =  _celPreview.Texture;
        else if (_celSprite?.Texture != null)
            targetTexture = _celSprite.Texture;
        
        if (targetTexture != null)
            textureSize = targetTexture.GetSize();
        
        return textureSize;
    }
    
    private Vector2 GetTargetPosition()
    {
        Vector2 targetPos = Vector2.Zero;
        
        if(_celPreview != null)
            targetPos =  _celPreview.GlobalPosition;
        else if (_celSprite != null)
            targetPos = _celSprite.GlobalPosition;
        
        return targetPos;
    }
    
    private void InitializeZoomConstraints()
    {
        Vector2 viewportSize = Size;
        Vector2 textureSize = GetTargetSize();

        float scaleX = viewportSize.X / textureSize.X;
        float scaleY = viewportSize.Y / textureSize.Y;
        float fitZoom = Mathf.Min(scaleX, scaleY) * FitPadding;

        _zoomMin = Mathf.Min(fitZoom * 0.5f, 1.0f);
        _zoomMax = Mathf.Max(4.0f, fitZoom * 8f);

        _currentZoom = Mathf.Clamp(_currentZoom, _zoomMin, _zoomMax);
        _camera.Zoom = new Vector2(_currentZoom, _currentZoom);

        // CenterCameraOnTexture();
    }

    private void ZoomCamera(float direction)
    {
        Vector2 worldCenter = GetViewportCenterInWorld();

        _currentZoom += direction * (_currentZoom * 0.2f);
        _currentZoom = Mathf.Clamp(_currentZoom, _zoomMin, _zoomMax);
        _camera.Zoom = new Vector2(_currentZoom, _currentZoom);

        if (_camera.AnchorMode == Camera2D.AnchorModeEnum.FixedTopLeft)
            _camera.Position = worldCenter - Size / (2f * _currentZoom);
    }
    
    private Vector2 GetViewportCenterInWorld()
    {
        if (_camera.AnchorMode == Camera2D.AnchorModeEnum.DragCenter)
            return _camera.Position;
        else
            return _camera.Position + Size / (2f * _currentZoom);
    }

    public void ZoomToFitOnce()
    {
        if(FitOncePerGroup && _hasFitOnce) return;
        if(!ZoomToFit()) return;
        if (FitOncePerGroup) _hasFitOnce = true;
    }
    
    public bool ZoomToFit()
    {
        Vector2 textureSize = GetTargetSize();
        if (textureSize == Vector2.One) return false;

        InitializeZoomConstraints();

        Vector2 viewportSize = Size;
        float scaleX = viewportSize.X / textureSize.X;
        float scaleY = viewportSize.Y / textureSize.Y;
        float fitZoom = Mathf.Min(scaleX, scaleY) * FitPadding;

        _currentZoom = Mathf.Clamp(fitZoom, _zoomMin, _zoomMax);
        _camera.Zoom = new Vector2(_currentZoom, _currentZoom);

        CenterCameraOnTexture();

        return true;
    }
    
    public void ResetZoom()
    {
        InitializeZoomConstraints();
        _currentZoom = 1.0f;
        _currentZoom = Mathf.Clamp(_currentZoom, _zoomMin, _zoomMax);
        _camera.Zoom = new Vector2(_currentZoom, _currentZoom);
        CenterCameraOnTexture();
    }

    public void CenterCameraOnTexture()
    {
        Vector2 textureSize = GetTargetSize();
        Vector2 topLeft = Vector2.Zero;

        if (_celPreview != null && _celPreview.Texture != null)
        {
            topLeft = _celPreview.Position;
        }
        else if (_celSprite != null && _celSprite.Texture != null)
        {
            topLeft = _celSprite.Centered
                ? _celSprite.Position - textureSize * 0.5f
                : _celSprite.Position;
        }

        Vector2 textureCenter = topLeft + textureSize * 0.5f;

        if (_camera.AnchorMode == Camera2D.AnchorModeEnum.DragCenter)
            _camera.Position = textureCenter;
        else
        {
            float inverseZoom = _currentZoom > 0f ? (0.5f / _currentZoom) : 0.5f;
            _camera.Position = textureCenter - Size * inverseZoom;
            // _camera.Position = textureCenter - Size / (2f * _currentZoom);
        }
           
    }
}