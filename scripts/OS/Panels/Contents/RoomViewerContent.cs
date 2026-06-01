using System;
using Godot;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

public partial class RoomViewerContent : Control
{
    [Export] public FloatingPanel ParentPanel;
    [Export] public bool CacheDisabled {get{return _cacheDisabled;} set{ _cacheDisabled = value;}}

    [Export] public ZoomableViewport _previewContainer;
    [Export] public SubViewport _viewport;
    [Export] public ItemList _objectNamesList;
    [Export] public Node2D _roomRoot;
    [Export] public Sprite2D _backgroundSprite;
    [Export] public Node2D _hotspotLayer;
    [Export] public Node2D _objectLayer;
    [Export] public Node2D _actorLayer;
    [Export] public Node2D _boundsLayer;
    [Export] public Button resetBtn;
    
    [Export] public Button boundsToggle;
    [Export] public Button hotspotsToggle;
    [Export] public Button objectsToggle;
    [Export] public Button actorsToggle;
    [Export] public Button walkboxesToggle;
    [Export] public MSlider paletteSlider;
    [Export] public PopupMenu _optionsMenu;
    
    private Dictionary<int, ImageTexture> _obimTextures = new(); //tod: move to cache
    
    private ScummBlock _selectedBlock;
    
    private ScummBlock _lflfBlock;
    private ScummBlock _roomBlock;
    private ScummBlock _rmscBlock;
    private ScummBlock _rmhdBlock;
    
    private float _zoom = 1.0f;
    private float _roomWidth = 10;
    private float _roomHeight = 10;

    private string _roomName = "";
    private int _roomNo = -1;
    private int _previousRoomId = -2;

    private bool wasForced;

    private Dictionary<string, Rect2> _hotspotRects = new();
    private string _selectedAssetId;
    private Rect2? _currentSelectedObjectRect = null;
    private Color _selectionColor = new Color(1, 1, 0, 1.0f);
    private float _selectionThickness = 2.0f;
    
    private bool _IsNoBGRoom() => _roomNo == 3;

    private CancellationTokenSource _cts;

    // Overlay toggles
    private bool _showBounds;
    private bool _showHotspots;
    private bool _showObjects;
    private bool _showActors;
    private bool _showWalkboxes;
    private bool _cacheDisabled;

    private const string s_ContentRootPath = "ContentRoot";
    private const string s_HSplitContainerPath = "ContentRoot/_VBoxContainer_186/HSplitContainer";
    private const string s_SubViewportPath = "ContentRoot/_VBoxContainer_186/HSplitContainer/PreviewWrapper/ViewportContainer/SubViewport";
    private const string s_RoomRootPath = "ContentRoot/_VBoxContainer_186/HSplitContainer/PreviewWrapper/ViewportContainer/SubViewport/RoomRoot";
    private const string s_TogglesPath = "ContentRoot/_VBoxContainer_186/_HBoxContainer_187/";


    private static readonly NodePath PathOptionsMenu = "ContentRoot/_VBoxContainer_186/_HBoxContainer_187/MenuBar/Options";

    private static readonly NodePath PathViewportContainer = s_HSplitContainerPath + "/PreviewWrapper/ViewportContainer";
    private static readonly NodePath PathObjectNamesList = s_HSplitContainerPath + "/ObjectNamesList";
    private static readonly NodePath PathSubViewport = s_SubViewportPath;
    private static readonly NodePath PathRoomRoot = s_RoomRootPath;
    private static readonly NodePath PathbackgroundSprite = s_RoomRootPath + "/backgroundSprite";
    private static readonly NodePath PathhotspotLayer = s_RoomRootPath + "/hotspotLayer";
    private static readonly NodePath PathobjectLayer = s_RoomRootPath + "/objectLayer";
    private static readonly NodePath PathactorLayer = s_RoomRootPath + "/actorLayer";
    private static readonly NodePath PathBoundsLayer = s_RoomRootPath + "/boundsLayer";
    
    private static readonly NodePath PathResetButton = s_TogglesPath + "/ResetButton";
    private static readonly NodePath PathBoundsToggle = s_TogglesPath + "/BoundsToggle";
    private static readonly NodePath PathHotspotsToggle = s_TogglesPath + "/HotspotsToggle";
    private static readonly NodePath PathObjectsToggle = s_TogglesPath + "/ObjectsToggle";
    private static readonly NodePath PathActorsToggle = s_TogglesPath + "/ActorsToggle";
    private static readonly NodePath PathWalkboxesToggle = s_TogglesPath + "/WalkboxesToggle";
    private static readonly NodePath PathPaletteSlider = s_TogglesPath + "/PaletteSlider";

    private long selectedIdx = -1;
    
    public void AssignNodes()
    {
        _optionsMenu = GetNode<PopupMenu>(PathOptionsMenu); 
        _previewContainer = GetNode<ZoomableViewport>(PathViewportContainer);
        _objectNamesList = GetNode<ItemList>(PathObjectNamesList);
        _viewport = GetNode<SubViewport>(PathSubViewport);
        _roomRoot = GetNode<Node2D>(PathRoomRoot);
        _backgroundSprite = GetNode<Sprite2D>(PathbackgroundSprite);
        _hotspotLayer = GetNode<Node2D>(PathhotspotLayer);
        _objectLayer = GetNode<Node2D>(PathobjectLayer);
        _actorLayer = GetNode<Node2D>(PathactorLayer);
        _boundsLayer = GetNode<Node2D>(PathBoundsLayer);
        // _camera = GetNode<Camera2D>(PathroomCamera);
        resetBtn = GetNode<Button>(PathResetButton);
        
        boundsToggle    = GetNode<Button>(PathBoundsToggle);
        hotspotsToggle  = GetNode<Button>(PathHotspotsToggle);
        objectsToggle   = GetNode<Button>(PathObjectsToggle);
        actorsToggle    = GetNode<Button>(PathActorsToggle);
        walkboxesToggle = GetNode<Button>(PathWalkboxesToggle);
        paletteSlider   = GetNode<MSlider>(PathPaletteSlider);
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
        // _AddToggle(toolbar, "Hotspots", true, v => { _showHotspots = v; QueueRedraw(); });
        // _AddToggle(toolbar, "Objects", true, v => { _showObjects = v; QueueRedraw(); });
        // _AddToggle(toolbar, "Actors", true, v => { _showActors = v; QueueRedraw(); });
        // _AddToggle(toolbar, "Walkboxes", false, v => { _showWalkboxes = v; QueueRedraw(); });

        SetupToggle(boundsToggle, OnBoundsToggled, boundsToggle.ButtonPressed);
        SetupToggle(hotspotsToggle, OnHotspotsToggled, hotspotsToggle.ButtonPressed);
        SetupToggle(objectsToggle, OnObjectsToggled, objectsToggle.ButtonPressed);
        SetupToggle(actorsToggle, OnActorsToggled, actorsToggle.ButtonPressed);
        SetupToggle(walkboxesToggle, OnWalkboxesToggled, walkboxesToggle.ButtonPressed);
        
        paletteSlider.IndexChanged += OnPaletteIndexChanged;
        
        _objectNamesList.ItemSelected += OnObjectSelected;
        // if (_objectNamesList.ItemCount > 0)
        //     OnObjectSelected(0); 
        
        // EventBus.Instance.RoomLoaded += _OnRoomLoaded;
        resetBtn.Pressed += ResetBtnOnPressed;
        
        _objectLayer.Connect("draw", Callable.From(_DrawObjectLayer));
        _hotspotLayer.Connect("draw", Callable.From(_DrawHotspotLayer));
        _boundsLayer.Connect("draw", Callable.From(_DrawBoundsLayer));
        
        _optionsMenu.IdPressed += OptionsMenuOnIdPressed;

        // _hotspotLayer.Connect("draw", Callable.From(_DrawHotspotLayer));
    }
    private void OptionsMenuOnIdPressed(long id)
    {
        switch (id)
        {
            case 0: OnObjectsToggled(!_showObjects);  break;
            case 1: ShowObjectsList(!_objectNamesList.Visible); break;
        }
        UpdateOptionsMenuState();
    }

    private void UpdateOptionsMenuState()
    {
        _optionsMenu.SetItemChecked(0, _showObjects);
        _optionsMenu.SetItemChecked(1, _objectNamesList.Visible);
    }

    public void OnExit()
    {
        resetBtn.Pressed -= ResetBtnOnPressed;
    }
    
    private void SetupToggle(Button toggleNode, Action<bool> toggleHandler, bool initialState)
    {
        toggleNode.Toggled += (v) => toggleHandler(v);
        toggleHandler(initialState);
    }
    
    private void OnBoundsToggled(bool v)
    {
        _showBounds = v;
        _boundsLayer.QueueRedraw();
    }

    private void OnHotspotsToggled(bool v)
    {
        _showHotspots = v;
        _hotspotLayer.QueueRedraw();
    }

    private void OnObjectsToggled(bool v)
    {
        _showObjects = v;
        _objectLayer.QueueRedraw();
    }

    private void ShowObjectsList(bool v)
    {
        _objectNamesList.Visible = v;
    }

    private void OnActorsToggled(bool v)
    {
        _showActors = v;
        _actorLayer.QueueRedraw();
    }

    private void OnWalkboxesToggled(bool v)
    {
        _showWalkboxes = v;
        QueueRedraw();
    }
    
    private void OnPaletteIndexChanged(int newIndex)
    {
        if (_roomBlock != null) GetBackgroundData();
    }

    private void OnObjectSelected(long i)
    {
        selectedIdx = i;
        _objectLayer.QueueRedraw();
        _boundsLayer.QueueRedraw();
    }
    private void ResetBtnOnPressed()
    {
        _previewContainer.ZoomToFitDeferred();
    }
   
    public void SetBackground(Image image)
    {
        if (_backgroundSprite.Texture != null)
        {
            _backgroundSprite.Texture.Dispose();
            _backgroundSprite.Texture = null;
        }

        if (image == null) return;

        var texture = ImageTexture.CreateFromImage(image);
        _backgroundSprite.Texture = texture;
        _backgroundSprite.Centered = false;
        _backgroundSprite.Position = Vector2.Zero;
        // _backgroundSprite.TextureFilter = TextureFilterEnum.Nearest;
        // _viewportContainer.CenterCameraOnTexture();
        if (wasForced)
        {
            wasForced = false;
            return;
        }
        _previewContainer.ZoomToFitOnceDeferred();
    }

   
    public void AddHotspot(string id, Rect2 rect, string displayName = "")
    {
        _hotspotRects[id] = rect;

        var hotspot = new ColorRect();
        hotspot.Position = rect.Position;
        hotspot.Size = rect.Size;
        hotspot.Color = new Color(1, 1, 0, 0.15f);
        hotspot.Name = id;
        hotspot.GuiInput += (@event) => _OnAssetClicked(@event, id, "hotspot");
        _hotspotLayer.AddChild(hotspot);

        if (!string.IsNullOrEmpty(displayName))
        {
            var label = new Label();
            label.Text = displayName;
            label.Position = rect.Position + new Vector2(2, 2);
            label.AddThemeColorOverride("font_color", Colors.Yellow);
            _hotspotLayer.AddChild(label);
        }
    }
    
    public void SetRoomBounds(string id, Rect2 rect)
    {
        _hotspotRects[id] = rect;

        var hotspot = new ColorRect();
        hotspot.Position = rect.Position;
        hotspot.Size = rect.Size;
        hotspot.Color = new Color(1, 1, 0, 0.15f);
        hotspot.Name = id;
        hotspot.GuiInput += (@event) => _OnAssetClicked(@event, id, "hotspot");
        _hotspotLayer.AddChild(hotspot);
    }

    private void _OnAssetClicked(InputEvent @event, string id, string type)
    {
        if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
        {
            _selectedAssetId = id;
            EventBus.Instance.EmitSignal(EventBus.SignalName.AssetSelected, id, type);
        }
    }
    
    public void OnBlockSelected(ScummBlock block)
    {
        if(!ParentPanel.IsOpen) return;

        _showObjects = false;
        UpdateOptionsMenuState();
        
        if (block == null) { OnRoomDeselected(); return; }

        switch (block.Tag)
        {
            case ScummTag.LFLF:             _lflfBlock = block;                 break;
            case ScummTag.ROOM:
            case ScummTag.RMSC:             _lflfBlock = block.Parent;          break;
            case ScummTag.RMHD:             _lflfBlock = block.Parent.Parent;   break; // RMHD > ROOM > LFLF
            default:                        return;
        }

        if (_lflfBlock == null)
        {
            // GD.Print("Not a room block");
            OnRoomDeselected(); return;
        }
        
        ResetToggles();
        paletteSlider.Reset();
        
        if (_objectNamesList.ItemCount > 0 && _objectNamesList.IsVisibleInTree())
            FocusManager.SetNextFocusOverride(_objectNamesList);

        _roomBlock = _lflfBlock.FindChild(ScummTag.ROOM);          // direct child of LFLF
        _rmscBlock = _lflfBlock.FindChild(ScummTag.RMSC);          // direct child of LFLF
        _rmhdBlock = _lflfBlock.FindChildRecursive(ScummTag.RMHD); // LFLF > ROOM > RMHD
        _selectedBlock = _lflfBlock;
        
        _roomName = "";
        _roomNo = -1;
        

        if (_lflfBlock?.GetMetaDataDict() != null)
        {
            if(_lflfBlock.GetMetadataItem(ScummMeta.LFLF.roomNo, out Variant foundRoomNum))
                _roomNo = (int)foundRoomNum;
            
            if(_lflfBlock.GetMetadataItem(ScummMeta.LFLF.roomName, out Variant foundRoomName))
                _roomName = (string)foundRoomName;
        }
        
        if (_rmhdBlock?.GetMetaDataDict() != null)
        {
            if(_rmhdBlock.GetMetadataItem(ScummMeta.RMHD.width, out Variant wVal))
                _roomWidth =  (float)wVal;
            if(_rmhdBlock.GetMetadataItem(ScummMeta.RMHD.height, out Variant foundheight))
                _roomHeight = (float)foundheight;
            // GD.Print($"room width: {rawW} room height: {rawH}");
        }
        
        if (_roomBlock.Offset != 45980903 && ScummDecoders.TryGetRoomInfo(_rmhdBlock, _roomBlock, out var roomInfo))
            paletteSlider.Setup(roomInfo.TotalPalettes, roomInfo.DefaultPaletteIndex);
        else
            paletteSlider.Reset();
        

        OnRoomSelected();
        EventBus.Instance.EmitSignal(EventBus.SignalName.RoomSelected);
        _objectLayer.QueueRedraw();
        _boundsLayer.QueueRedraw();
        QueueRedraw();
        _previewContainer.ResetHasFitOnce();
    }

    private void ResetToggles()
    {
        boundsToggle.SetPressed(false);
        hotspotsToggle.SetPressed(false);
        objectsToggle.SetPressed(false);
        actorsToggle.SetPressed(false);
        walkboxesToggle.SetPressed(false);
    }

    private void OnRoomSelected(bool force = false)
    {
        if (!force && _previousRoomId == _roomNo) return;
        _previousRoomId = _roomNo;
        ParentPanel.SetTitle($"{ParentPanel.PanelTitle} [ {_roomNo} : {_roomName} ]"); // — {roomPath}
        
        // GD.Print("Room Selected");

        wasForced = force;

        GetBackgroundData();
        
        QueueRedraw();
    }
    
    private async void GetBackgroundData()
    {
        ClearObimTextures();
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        if (_IsNoBGRoom())
        {
            SetBackground(null); 
            LoadObimTextures(token);
            return;
        }

        try
        {
            var result = await ScummBackgroundCache.GetCachedBackgroundAsync(
                _roomNo, _rmhdBlock, _roomBlock, CacheDisabled, paletteSlider.CurrentIndex, token);

            if (token.IsCancellationRequested) return;

            if (result.ExecutedImage != null)
            {
                SetBackground(result.ExecutedImage);
                LoadObimTextures(token);
            }
        }
        catch (OperationCanceledException) { }
    }
    
    private async void LoadObimTextures(CancellationToken token)
    {
        ClearObimTextures();

        int obimIndex = 0;
        foreach (var child in _roomBlock.Children)
        {
            if (child.Tag != ScummTag.OBIM) continue; 

            bool hasImage = child.GetMetadataItem(ScummMeta.OBIM.imageCount, out Variant imgCount)
                && (int)imgCount > 0;

            if (hasImage)
            {

                ScummDecoders.TryGetObimInfo(child, out var info);
                int i = obimIndex;
                
                var result = await ObimCache.GetCachedObimAsync(
                    child, 
                    frameIndex: 0, 
                    info.DefaultPaletteIndex, 
                    bakeImage: true, 
                    token);

                if (token.IsCancellationRequested) return;

                _obimTextures[i] = (result.Surface != null && result.ExecutedImage != null)
                    ? ImageTexture.CreateFromImage(result.ExecutedImage)
                    : null;
            }
            else
            {
                _obimTextures[obimIndex] = null;
            }

            obimIndex++;
        }

        _objectLayer.QueueRedraw();
    }
    

    private void OnRoomDeselected()
    {
        ParentPanel.SetTitle(ParentPanel.PanelTitle);
        ClearObimTextures();
        ResetToggles();
        paletteSlider.Reset();
    }
    
    private void ClearObimTextures()
    {
        foreach (var tex in _obimTextures.Values)
            tex?.Dispose();
        _obimTextures.Clear();
    }
    
    private void _DrawBoundsLayer()
    {
        if (_showBounds && _rmhdBlock != null) 
            _boundsLayer.DrawRect(new Rect2(Vector2.Zero, new Vector2(_roomWidth, _roomHeight)), Colors.Orange, false, 2.0f);

        if (_currentSelectedObjectRect.HasValue)
        {
            _boundsLayer.DrawRect(
                _currentSelectedObjectRect.Value, 
                _selectionColor, 
                false, 
                _selectionThickness
            );
        }
    }
    
    private void ScaleRoomToViewport()
    {
        if(_rmhdBlock == null) return;
        Vector2 containerSize = _previewContainer.Size;
        
        // var rmhd = _rmhdBlock;
        float roomW = _roomWidth;
        float roomH = _roomHeight;
        Vector2 roomSize = new Vector2(roomW, roomH);

        float scaleX = containerSize.X / roomSize.X;
        float scaleY = containerSize.Y / roomSize.Y;

        float finalScale = Mathf.Min(scaleX, scaleY);

        _roomRoot.Scale = new Vector2(finalScale, finalScale);

        Vector2 offset = (containerSize - (roomSize * finalScale)) / 2f;
        _roomRoot.Position = offset;
    }
    
    //todo: separate object list populate/draw
    private void _DrawObjectLayer()
    {
        if (_roomBlock == null) return;

        _objectNamesList.Clear();
        _objectNamesList.AddItem("None");

        _currentSelectedObjectRect = null; 

        int currentIndex = 0;
        foreach (var child in _roomBlock.Children)
        {
            if (child.Tag != ScummTag.OBIM) continue;
            if (!child.GetMetadataItem(ScummMeta.OBIM.x, out var xv)) continue;

            float x = (float)xv;
            float y = (float)child.GetMetadataItem(ScummMeta.OBIM.y);
            float w = (float)child.GetMetadataItem(ScummMeta.OBIM.width);
            float h = (float)child.GetMetadataItem(ScummMeta.OBIM.height);

            if (w <= 0) w = 100;
            if (h <= 0) h = 100;

            var rect = new Rect2(x, y, w, h);
        
            int visualUIIndex = currentIndex + 1; 
            bool isSelected = (visualUIIndex == selectedIdx);
        
            if (isSelected)
                _currentSelectedObjectRect = rect;
        
            if (_showObjects)
            {
                if (_obimTextures.TryGetValue(currentIndex, out var tex) && tex != null)
                {
                    bool canDraw = tex.GetWidth() < 640 || isSelected;
                    if (canDraw)
                    {
                        var size = tex.GetSize() == Vector2.Zero ? new Vector2(w, h) : tex.GetSize();
                        _objectLayer.DrawTextureRect(tex, new Rect2(rect.Position, size), false);
                    }
                }
            }

            string label = child.GetMetadataItem(ScummMeta.OBIM.name, out var n) ? (string)n : "?";
            _objectNamesList.AddItem(label, selectable: true);
        
            currentIndex++;
        }
    
        SelectCurrentIndex();
    }
    
    private void _DrawHotspotLayer()
    {
        if (!_showHotspots || _roomBlock == null) return;
    }
    
    private void SelectCurrentIndex()
    {
        if (_objectNamesList == null || _objectNamesList.ItemCount == 0) return;
        
        if (selectedIdx < 0 || selectedIdx >= _objectNamesList.ItemCount)
            selectedIdx = 0;

        SelectObjectInList((int)selectedIdx);
    }

    private void SelectObjectInList(int idx)
    {
        if (_objectNamesList != null && idx >= 0 && idx < _objectNamesList.ItemCount)
            _objectNamesList.Select(idx);
    }
}