using System;
using Godot;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Visual room preview. Renders background strips, overlays hotspot rects,
/// object bounding boxes, and actor positions. Click to select assets.
/// </summary>
///

public partial class RoomPreviewPanel : FloatingPanel
{
    public override string PanelTitle => "Room Preview";
    
    [Export] public ZoomableViewport _viewportContainer;
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
    
    private ScummBlock _selectedBlock;
    
    private ScummBlock _lflfBlock;
    private ScummBlock _roomBlock;
    private ScummBlock _rmscBlock;
    private ScummBlock _rmhdBlock;
    
    
    private float _zoom = 1.0f;
    
    private float _roomWidth = 10;
    private float _roomHeight = 10;

    private Dictionary<string, Rect2> _hotspotRects = new();
    private string _selectedAssetId;

    // Overlay toggles
    private bool _showBounds = true;
    private bool _showHotspots = true;
    private bool _showObjects = true;
    private bool _showActors = true;
    private bool _showWalkboxes;
    
    private static readonly NodePath PathViewportContainer = "Layout/MarginContainer/ContentRoot/_VBoxContainer_186/HSplitContainer/ViewportContainer";
    private static readonly NodePath PathObjectNamesList = "Layout/MarginContainer/ContentRoot/_VBoxContainer_186/HSplitContainer/ObjectNamesList";
    private static readonly NodePath PathSubViewport = "Layout/MarginContainer/ContentRoot/_VBoxContainer_186/HSplitContainer/ViewportContainer/SubViewport";
    private static readonly NodePath PathRoomRoot = "Layout/MarginContainer/ContentRoot/_VBoxContainer_186/HSplitContainer/ViewportContainer/SubViewport/RoomRoot";
    private static readonly NodePath PathbackgroundSprite = "Layout/MarginContainer/ContentRoot/_VBoxContainer_186/HSplitContainer/ViewportContainer/SubViewport/RoomRoot/backgroundSprite";
    private static readonly NodePath PathhotspotLayer = "Layout/MarginContainer/ContentRoot/_VBoxContainer_186/HSplitContainer/ViewportContainer/SubViewport/RoomRoot/hotspotLayer";
    private static readonly NodePath PathobjectLayer = "Layout/MarginContainer/ContentRoot/_VBoxContainer_186/HSplitContainer/ViewportContainer/SubViewport/RoomRoot/objectLayer";
    private static readonly NodePath PathactorLayer = "Layout/MarginContainer/ContentRoot/_VBoxContainer_186/HSplitContainer/ViewportContainer/SubViewport/RoomRoot/actorLayer";
    private static readonly NodePath PathBoundsLayer = "Layout/MarginContainer/ContentRoot/_VBoxContainer_186/HSplitContainer/ViewportContainer/SubViewport/RoomRoot/boundsLayer";
    
    private static readonly NodePath PathResetButton = "Layout/MarginContainer/ContentRoot/_VBoxContainer_186/_HBoxContainer_187/ResetButton";
    private static readonly NodePath PathBoundsToggle = "Layout/MarginContainer/ContentRoot/_VBoxContainer_186/_HBoxContainer_187/BoundsToggle";
    private static readonly NodePath PathHotspotsToggle = "Layout/MarginContainer/ContentRoot/_VBoxContainer_186/_HBoxContainer_187/HotspotsToggle";
    private static readonly NodePath PathObjectsToggle = "Layout/MarginContainer/ContentRoot/_VBoxContainer_186/_HBoxContainer_187/ObjectsToggle";
    private static readonly NodePath PathActorsToggle = "Layout/MarginContainer/ContentRoot/_VBoxContainer_186/_HBoxContainer_187/ActorsToggle";
    private static readonly NodePath PathWalkboxesToggle = "Layout/MarginContainer/ContentRoot/_VBoxContainer_186/_HBoxContainer_187/WalkboxesToggle";

    private long selectedIdx = -1;
    protected override void OnReady()
    {
        
        // _AddToggle(toolbar, "Hotspots", true, v => { _showHotspots = v; QueueRedraw(); });
        // _AddToggle(toolbar, "Objects", true, v => { _showObjects = v; QueueRedraw(); });
        // _AddToggle(toolbar, "Actors", true, v => { _showActors = v; QueueRedraw(); });
        // _AddToggle(toolbar, "Walkboxes", false, v => { _showWalkboxes = v; QueueRedraw(); });

        boundsToggle.Toggled += (v) =>
        {
            _showBounds = v;
            _boundsLayer.QueueRedraw();
        };
        hotspotsToggle.Toggled += (v) =>
        {
            _showHotspots = v;
            _hotspotLayer.QueueRedraw();
        };
        
        objectsToggle.Toggled += (v) =>
        {
            _showObjects = v;
            _objectLayer.QueueRedraw();
        };
        actorsToggle.Toggled += (v) =>
        {
            _showActors = v;
            _actorLayer.QueueRedraw();
        };
        walkboxesToggle.Toggled += (v) =>
        {
            _showWalkboxes = v;
            QueueRedraw();
        };
        
        _objectNamesList.ItemSelected += (i) =>
        {
            selectedIdx = i;
            _objectLayer.QueueRedraw();
            // _objectNamesList.GrabFocus();
            // _objectNamesList.Select((int)i);
        };
        
        
        EventBus.Instance.BlockSelected += _OnBlockSelected;
        // EventBus.Instance.RoomLoaded += _OnRoomLoaded;
        resetBtn.Pressed += ResetBtnOnPressed;
        
        _objectLayer.Connect("draw", Callable.From(_DrawObjectLayer));
        _boundsLayer.Connect("draw", Callable.From(_DrawBoundsLayer));
        
        

        // _hotspotLayer.Connect("draw", Callable.From(_DrawHotspotLayer));
    }
    private void ResetBtnOnPressed()
    {
        _viewportContainer.ZoomToFitDeferred();
    }



    public override void AssignNodes()
    {
        base.AssignNodes();
        
        _viewportContainer = GetNode<ZoomableViewport>(PathViewportContainer);
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
        
        boundsToggle = GetNode<Button>(PathBoundsToggle);
        hotspotsToggle = GetNode<Button>(PathHotspotsToggle);
        objectsToggle = GetNode<Button>(PathObjectsToggle);
        actorsToggle = GetNode<Button>(PathActorsToggle);
        walkboxesToggle = GetNode<Button>(PathWalkboxesToggle);
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
        // _viewportContainer.CenterCameraOnTexture();
        _viewportContainer.ZoomToFitOnceDeferred();
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

    
    private static readonly Vector2 H2Half = new Vector2(0.5f, 0.5f);
    
    
    private void _OnBlockSelected(ScummBlock block)
    {
        if (block == null) { OnRoomDeselected(); return; }

        switch (block.Tag)
        {
            case ScummTag.LFLF:             _lflfBlock = block;                 break;
            case ScummTag.ROOM:
            case ScummTag.RMSC:             _lflfBlock = block.Parent;          break;
            case ScummTag.RMHD:             _lflfBlock = block.Parent.Parent;   break; // RMHD > ROOM > LFLF
            default:                        return; // keep current view, do nothing
        }

        if (_lflfBlock == null)
        {
            // GD.Print("Not a room block");
            OnRoomDeselected(); return;
        }
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

        OnRoomSelected();
        _objectLayer.QueueRedraw();
        _boundsLayer.QueueRedraw();
        QueueRedraw();
        _viewportContainer.ResetHasFitOnce();
    }

    private string _roomName = "";
    private int _roomNo = -1;
    private int _previousRoomId = -2;
   

    private void OnRoomSelected()
    {
        if (_previousRoomId == _roomNo) return;
        _previousRoomId = _roomNo;
        SetTitle($"Room Preview [ {_roomNo} : {_roomName} ]"); // — {roomPath}
        
        // GD.Print("Room Selected");

        GetBackgroundData();
        
        QueueRedraw();
    }

  
    private bool _IsNoBGRoom() => _roomNo == 3;

    private CancellationTokenSource _cts;
    
    private async void GetBackgroundData()
    {
        if (_IsNoBGRoom()) { SetBackground(null); return; }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        int roomId = _roomNo;
        var rmhd = _rmhdBlock;
        var room = _roomBlock;

        try
        {
            Image resultImage = await Task.Run(() =>
            {
                if (ScummBackgroundCache.TryLoadFromCache(roomId, out int w, out int h, out int p, out byte[] cachedIndices))
                {
                    var apal = room.FindChildRecursive(ScummTag.APAL);
                    return ScummBackgroundCache.CreateImageFromIndexed(cachedIndices, w, h, p, apal.DataSpan);
                }
                
                if (ScummDecoders.DecodeBackgroundImage(roomId, rmhd, room, token, 
                    out Image decodedImage, out byte[] rawBuffer, out int pitch))
                {
                    string path = ScummBackgroundCache.GetBackgroundCachePath(roomId);
                    ScummBackgroundCache.SaveIndexedBackground(path, (int)rmhd.GetMetadataItem(ScummMeta.RMHD.width), (int)rmhd.GetMetadataItem(ScummMeta.RMHD.height), pitch, rawBuffer);
                
                    return decodedImage;
                }

                return null;
            }, token);

            if (!token.IsCancellationRequested)
            {
                SetBackground(resultImage);
                SelectObjectInList(0);
            }
        }
        catch (OperationCanceledException) { }
    }
    

    private void OnRoomDeselected()
    {
        SetTitle($"Room Preview"); 
        GD.Print("Room Deselected");
    }
    
    private void _DrawBoundsLayer()
    {
        if(!_showBounds) return;
        if (_rmhdBlock == null) return;
        // _viewportContainer.ZoomToFitDeferred();
        // ScaleRoomToViewport();
        // float rawW = (float)wVal;
        // float rawH = (float)_rmhdBlock.Metadata["height"];

                                    // Drawing in room-space
        _boundsLayer.DrawRect(new Rect2(Vector2.Zero, new Vector2(_roomWidth, _roomHeight)), Colors.Orange, false, 2.0f);
    
    }
    
    private void ScaleRoomToViewport()
    {
        if(_rmhdBlock == null) return;
        Vector2 containerSize = _viewportContainer.Size;
        
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
    
    private void _DrawObjectLayer()
    {
        if (!_showObjects || _roomBlock == null) return;

        _objectNamesList.Clear();

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
        
            /* Selection */
            bool isSelected = (currentIndex == selectedIdx);
            float borderThickness = isSelected ? 4.0f : 1.0f;
            Color rectColor = isSelected ? new Color(1, 1, 0, 1.0f) : new Color(1, 1, 0, 0.5f);

            _objectLayer.DrawRect(rect, rectColor, false, borderThickness);
            /* */

            string label = child.GetMetadataItem(ScummMeta.OBIM.name, out var n) ? (string)n : "?";
            _objectNamesList.AddItem(label, selectable: true);
            
        
            currentIndex++;
        }
        SelectCurrentIndex();
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
