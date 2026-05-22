using System.Collections.Generic;
using Godot;
using DockZone = WindowManager.DockZone;
public partial class FloatingPanel : Control
{
    [Export] public DockZone DefaultDock { get; set; } = DockZone.None;
    [Export] public float DockedThickness { get; set; } = WindowManager.DefaultThickness;
    public Vector2 PreDockPosition { get; private set; }
    public Vector2 PreDockSize { get; private set; }
    public DockZone CurrentDock => _currentDock;
    private DockZone _currentDock = DockZone.None;
    private DockZone _dragCandidateZone = DockZone.None;

    private Vector2 _dragStartMouse;
    private float _resizeStartDockedThickness;
    private const float UndockThreshold = 40f;
    public virtual string PanelTitle => "Panel";
    [Export] public Vector2 DefaultSize { get; set; } = new(400, 300);
    [Export] public Vector2 MinimumPanelSize { get; set; } = new(200, 100);

    public string PanelId { get; private set; } = "panel";

    [Export] public Control _contentRoot;

    [Export] public Panel _background;
    [Export] public Label _titleLabel;
    [Export] public Button _closeButton;
    [Export] public Button _collapseButton;
    [Export] public Control _titleHBox;
    [Export] public Control _titleBar;
    [Export] public Control _canvas;
    [Export] public Control _handlesContainer;
    [Export] public VBoxContainer _layout;
    [Export] public MarginContainer _marginContainer;
    [Export] public HBoxContainer _btnRow;

    private bool _dragging;
    private Vector2 _dragOffset;
    private bool _collapsed;
    private float _expandedHeight;

    private bool _resizing;
    private Vector2 _resizeStartMouse;
    private Vector2 _resizeStartSize;
    private Vector2 _resizeStartPosition;
    private Vector2 _currentDirection;
    private const float ResizeHandleSize = 12f;
    private const int HandleThickness = 6;

    private bool _userResized = false;

    private const string s_expandIcon = "▽";
    private const string s_collapseIcon = "△";
    private const string s_closeIcon = "x";


    private bool _animationPlayed;
    
    private static readonly HandleData[] Handles;
    static FloatingPanel()
    {
        Handles = [
            new("ResizeHandles/HandleLeft", new Vector2(-1, 0)), 
            new("ResizeHandles/HandleRight", new Vector2(1, 0)), 
            new("ResizeHandles/HandleTop", new Vector2(0, -1)), 
            new("ResizeHandles/HandleBottom", new Vector2(0, 1)),
            new("ResizeHandles/HandleTopLeft", new Vector2(-1, -1)),
            new("ResizeHandles/HandleTopRight", new Vector2(1, -1)), 
            new("ResizeHandles/HandleBottomLeft", new Vector2(-1, 1)), 
            new("ResizeHandles/HandleBottomRight", new Vector2(1, 1))
        ];
    }

    // Top-level Nodes
    private static readonly NodePath PathLayout = "Layout";
    private static readonly NodePath PathBackground = "Background";
    private static readonly NodePath PathResizeHandles = "ResizeHandles";

    // Nested Layout Nodes
    private static readonly NodePath PathMarginContainer = "Layout/MarginContainer";
    private static readonly NodePath PathContentRoot = "Layout/MarginContainer/ContentRoot";

    // Title Bar Hierarchy
    private static readonly NodePath PathTitleBar = "Layout/TitleBar";
    private static readonly NodePath PathTitleHBox = "Layout/TitleBar/TitleHBox";
    private static readonly NodePath PathTitleLabel = "Layout/TitleBar/TitleHBox/TitleLabel";
    private static readonly NodePath PathButtonRow = "Layout/TitleBar/TitleHBox/Button Row";

    // Buttons
    private static readonly NodePath PathCloseButton = "Layout/TitleBar/TitleHBox/Button Row/CloseButton";
    private static readonly NodePath PathCollapseButton = "Layout/TitleBar/TitleHBox/Button Row/CollapseButton";

    public override void _Notification(int what)
    {
        if (what == NotificationExitTree)
        {
            EventBus.Instance.BlockSelected -= _OnBlockSelected;
            WindowManager.UnregisterPanel(this);
        }
    }

    public override void _Ready()
    {
        //_canvas = GetParent<Control>();
        CustomMinimumSize = MinimumPanelSize;
        PanelId = PanelTitle;
        Name = PanelTitle;
        // Size = DefaultSize;
        // SetSize(DefaultSize);
        // ClipContents = true;

        // _BuildLayout();
        // _layout.MinimumSizeChanged += _FitToContents;

        AssignNodes();
        OnReady();
        SetTitle(PanelTitle);



        SubscribeHandles();

        //CallDeferred(nameof(LoadLayout), this);
        _titleBar.GuiInput += _OnTitleBarInput;
        _closeButton.Pressed += OnCloseButtonPressed;
        _collapseButton.Pressed += _OnCollapsePressed;
        EventBus.Instance.FileParsed += OnFileParsed;
        EventBus.Instance.PanelFocusRequested += _OnPanelFocusRequested;

        CallDeferred(nameof(InitializeInBounds));

        CallDeferred(nameof(LoadLayout));
        
        EventBus.Instance.BlockSelected += _OnBlockSelected;
        // CallDeferred(nameof(_FitToContents));
    }

    protected virtual void _OnBlockSelected(ScummBlock obimBlock)
    {
        
    }

    private void InitializeInBounds()
    {
        if (_canvas == null) return;
        _canvas.ForceUpdateTransform();

        if (DefaultDock != DockZone.None)
        {
            DockTo(DefaultDock);
            return;
        }

        Position = Position.ClampToCanvas(Size, _canvas);
    }



    private struct HandleData(string path, Vector2 direction)
    {
        public NodePath Path = path;
        public readonly Vector2 Direction = direction;
    }

    private void SubscribeHandles()
    {
        foreach (var handleData in Handles)
        {
            if (GetNode<Control>(handleData.Path) is { } handle)
            {
                Vector2 capturedDir = handleData.Direction;
                handle.GuiInput += (ev) => UpdateHandleInput(ev, capturedDir);
            }
            else GD.PushWarning($"{Name}: missing resize handle at '{handleData.Path}'");
        }
    }
    private void OnCloseButtonPressed()
    {
        Visible = false;
    }
    public virtual void AssignNodes()
    {
        _layout = GetNode<VBoxContainer>(PathLayout);
        _background = GetNode<Panel>(PathBackground);
        _handlesContainer = GetNode<Control>(PathResizeHandles);
        
        _marginContainer = GetNode<MarginContainer>(PathMarginContainer);
        _contentRoot = GetNode<VBoxContainer>(PathContentRoot);

        _titleBar = GetNode<Control>(PathTitleBar);
        _titleHBox = GetNode<HBoxContainer>(PathTitleHBox);
        _titleLabel = GetNode<Label>(PathTitleLabel);

        _btnRow = GetNode<HBoxContainer>(PathButtonRow);
        _closeButton = GetNode<Button>(PathCloseButton);
        _collapseButton = GetNode<Button>(PathCollapseButton);

        _canvas = GetParent<Control>();
    }



    protected virtual void LoadLayout()
    {
        // Config.LoadLayout(this);
        WindowManager.RegisterPanel(this);
        WindowManager.RefreshZoneLayout(_currentDock);
    }
    private void OnFileParsed(ScummBlock root)
    {
        // _FitToContents();
    }

    protected virtual void OnReady() { }


    public void UpdateHandleInput(InputEvent @event, Vector2 direction) =>
        _OnHandleInput(@event, direction, isSync: false, 0f);

    public void SyncResize(InputEvent @event, Vector2 direction, float syncedThickness) =>
        _OnHandleInput(@event, direction, isSync: true, syncedThickness);

    private void _OnHandleInput(InputEvent @event, Vector2 direction, bool isSync, float syncedThickness)
    {
        if (!isSync && FocusManager.CurrentResized != null && FocusManager.CurrentResized != this)
            return;

        if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
            HandleResizeToggle(mb, direction, isSync);
        else if (@event is InputEventMouseMotion && _resizing)
            ExecuteResizeStep(@event, direction, isSync, syncedThickness);
    }

    private void HandleResizeToggle(InputEventMouseButton mb, Vector2 direction, bool isSync)
    {
        MoveToFront();
        _resizing = mb.Pressed;

        if (mb.Pressed)
        {
            if (!isSync) FocusManager.CurrentResized = this;

            _currentDirection = direction;
            _resizeStartMouse = GetGlobalMousePosition();
            _resizeStartSize = Size;
            _resizeStartPosition = Position;
        
            _resizeStartDockedThickness = DockedThickness; 
        }
        else
        {
            if (!isSync) FocusManager.CurrentResized = null;

            Vector2 finalSize = Size.SnapVector();
            finalSize.X = Mathf.Max(finalSize.X, MinimumPanelSize.X);
            finalSize.Y = Mathf.Max(finalSize.Y, MinimumPanelSize.Y);

            if (_currentDirection.X < 0 || _currentDirection.Y < 0)
            {
                Vector2 sizeDiff = finalSize - Size;
                if (_currentDirection.X < 0) Position -= new Vector2(sizeDiff.X, 0);
                if (_currentDirection.Y < 0) Position -= new Vector2(0, sizeDiff.Y);
            }

            if (_currentDock == DockZone.None)
                Size = finalSize;
            else
                WindowManager.RefreshZoneLayout(_currentDock);
        }
    }

   private void ExecuteResizeStep(InputEvent @event, Vector2 direction, bool isSync, float syncedThickness)
{
    if (_currentDock != DockZone.None)
    {
        if (isSync)
        {
            DockedThickness = syncedThickness;
        }
        else
        {
            Vector2 mouseDiff = GetGlobalMousePosition() - _resizeStartMouse;
            float delta = WindowManager.MouseDeltaToDock(mouseDiff, _currentDock);
            DockedThickness = Mathf.Max(_resizeStartDockedThickness + delta, MinimumPanelSize.X);
            
            WindowManager.OnDockResize(this, _currentDock, @event, direction, DockedThickness);
        }

        WindowManager.RefreshZoneLayout(_currentDock);
    }
    else
    {
        Vector2 mouseDiff = GetGlobalMousePosition() - _resizeStartMouse;
        Vector2 newSize = _resizeStartSize;
        Vector2 newPos = _resizeStartPosition;

        if (_currentDirection.X != 0)
        {
            float deltaX = mouseDiff.X * _currentDirection.X;
            float targetWidth = Mathf.Max(_resizeStartSize.X + deltaX, MinimumPanelSize.X);
            if (_currentDirection.X < 0) newPos.X -= targetWidth - _resizeStartSize.X;
            newSize.X = targetWidth;
        }

        if (_currentDirection.Y != 0)
        {
            float deltaY = mouseDiff.Y * _currentDirection.Y;
            float targetHeight = Mathf.Max(_resizeStartSize.Y + deltaY, MinimumPanelSize.Y);
            if (_currentDirection.Y < 0) newPos.Y -= targetHeight - _resizeStartSize.Y;
            newSize.Y = targetHeight;
        }

        Size = newSize;
        Position = newPos.ClampToCanvas(Size, _canvas);
    }

    _userResized = true;
}


    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton { Pressed: true })
        {
            MoveToFront();
            EventBus.Instance.EmitSignal(EventBus.SignalName.PanelFocusRequested, PanelTitle);
        }
    }

    private void _OnTitleBarInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left } mb)
        {
            if (mb.Pressed)
            {
                _dragging = true;
                _dragStartMouse = _canvas.GetLocalMousePosition();
                _dragOffset = _canvas.GetLocalMousePosition() - Position;
                MoveToFront();
            }
            else
            {
                _dragging = false;
                // WindowManager.HidePreview();
                WindowManager.ResetHover();

                if (_currentDock == DockZone.None)
                {
                    if (_dragCandidateZone != DockZone.None)
                        DockTo(_dragCandidateZone);
                    else
                        Position = Position.SnapVector().ClampToCanvas(Size, _canvas);

                    _dragCandidateZone = DockZone.None;
                }
            }
        }

        if (@event is InputEventMouseMotion && _dragging)
        {
            if (_currentDock != DockZone.None)
            {
                float dist = _canvas.GetLocalMousePosition().DistanceTo(_dragStartMouse);
                if (dist > UndockThreshold)
                {
                    Undock();
                    _dragOffset = new Vector2(Size.X * 0.5f, 14f);
                    Position = (_canvas.GetLocalMousePosition() - _dragOffset)
                        .ClampToCanvas(Size, _canvas);
                }
            }
            else
            {
                var localMousePos = _canvas.GetLocalMousePosition();
                Vector2 targetPos = localMousePos - _dragOffset;
                Position = targetPos.ClampToCanvas(Size, _canvas);
                _dragCandidateZone = WindowManager.UpdatePreview(this, Position, Size, localMousePos, DockedThickness);
            }
        }
    }

    private void _OnCollapsePressed()
    {
        _collapsed = !_collapsed;
        if (_collapsed)
        {
            _expandedHeight = Size.Y;
            CustomMinimumSize = new Vector2(Size.X, 28);
            _contentRoot.Visible = false;
            _collapseButton.Text = s_expandIcon;
            _handlesContainer.Visible = false;
            _marginContainer.Visible = false;
            Size = new Vector2(Size.X, 28);
        }
        else
        {
            CustomMinimumSize = MinimumPanelSize;
            Size = new Vector2(Size.X, _expandedHeight);
            _contentRoot.Visible = true;
            _marginContainer.Visible = true;
            _collapseButton.Text = s_collapseIcon;
            _handlesContainer.Visible = true;
        }
    }


    private void _OnPanelFocusRequested(string panelId)
    {
    }

    public void SetTitle(string title)
    {
        // PanelTitle = title;
        if (_titleLabel != null)
            _titleLabel.Text = title;
    }

    private void _FitToContents()
    {
        if (_layout == null || _userResized)
            return;

        var minSize = _layout.GetCombinedMinimumSize();

        minSize.X = Mathf.Max(minSize.X, MinimumPanelSize.X);
        minSize.Y = Mathf.Max(minSize.Y, MinimumPanelSize.Y);

        Size = minSize;

        _contentRoot.ExpandAndFillHV();
        Position = Position.ClampToCanvas(Size, _canvas);
    }


    public void DockTo(DockZone zone)
    {
        if (zone == DockZone.None)
        {
            Undock();
            return;
        }
        _currentDock = zone;
        _userResized = false;
        _collapsed = false;
        _contentRoot.Visible = true;
        _btnRow.Visible = false; // collapsing conflicts with docked layout?
        _handlesContainer.Visible = true;
        WindowManager.Dock(this, zone);
    }

    public void Undock()
    {
        if (_currentDock == DockZone.None) return;
        _currentDock = DockZone.None;
        _btnRow.Visible = true;
        WindowManager.Undock(this);
    }

    public void ApplyDockedLayout()
    {
        if (_currentDock == DockZone.None) return;
        var rect = WindowManager.GetDockedRect(this);
        Position = rect.Position;
        Size = rect.Size;
        CustomMinimumSize = Vector2.Zero;
    }

    public void SaveFloatingRect()
    {
        PreDockPosition = Position;
        PreDockSize = Size;
    }

    public void RestoreFloatingRect()
    {
        Size = PreDockSize;
        Position = GetGlobalMousePosition() - (Size * 0.5f);
    }


}