using System.Collections.Generic;
using Godot;

public partial class FloatingPanel : Control
{
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
        // if (what == NotificationExitTree)
        //     WindowManager.UnregisterPanel(this);
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
        
        LoadLayout();

        SubscribeHandles();

        //CallDeferred(nameof(LoadLayout), this);
        _titleBar.GuiInput += _OnTitleBarInput;
        _closeButton.Pressed += OnCloseButtonPressed;
        _collapseButton.Pressed += _OnCollapsePressed;
        EventBus.Instance.FileParsed += OnFileParsed;
        EventBus.Instance.PanelFocusRequested += _OnPanelFocusRequested;
        
        // CallDeferred(nameof(_FitToContents));
    }
    
    private struct HandleData(string path, Vector2 direction)
    {
        public NodePath Path = path;
        public readonly Vector2 Direction = direction;
    }

    private static readonly HandleData[] Handles = 
    {
        new("ResizeHandles/HandleLeft",        new Vector2(-1,  0)),
        new("ResizeHandles/HandleRight",       new Vector2( 1,  0)),
        new("ResizeHandles/HandleTop",         new Vector2( 0, -1)),
        new("ResizeHandles/HandleBottom",      new Vector2( 0,  1)),
        new("ResizeHandles/HandleTopLeft",     new Vector2(-1, -1)),
        new("ResizeHandles/HandleTopRight",    new Vector2( 1, -1)),
        new("ResizeHandles/HandleBottomLeft",  new Vector2(-1,  1)),
        new("ResizeHandles/HandleBottomRight", new Vector2( 1,  1)),
    };

    private void SubscribeHandles()
    {
        foreach (var handleData in Handles)
        {
            if (GetNode<Control>(handleData.Path) is { } handle)
            {
                Vector2 capturedDir = handleData.Direction;
                handle.GuiInput += (ev) => _OnHandleInput(ev, capturedDir);
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
        // Basic Layout & Background
        _layout = GetNode<VBoxContainer>(PathLayout);
        _background = GetNode<Panel>(PathBackground);
        _handlesContainer = GetNode<Control>(PathResizeHandles);
    
        // Internal Containers
        _marginContainer = GetNode<MarginContainer>(PathMarginContainer);
        _contentRoot = GetNode<VBoxContainer>(PathContentRoot);
    
        // Title Bar Logic
        _titleBar = GetNode<Control>(PathTitleBar);
        _titleHBox = GetNode<HBoxContainer>(PathTitleHBox);
        _titleLabel = GetNode<Label>(PathTitleLabel);
    
        // Buttons
        _btnRow = GetNode<HBoxContainer>(PathButtonRow);
        _closeButton = GetNode<Button>(PathCloseButton);
        _collapseButton = GetNode<Button>(PathCollapseButton);

        // Parent Reference
        _canvas = GetParent<Control>();
    }
    
    

    protected virtual void LoadLayout()
    {
        // Config.LoadLayout(this);
        // WindowManager.RegisterPanel(this);
    }
    private void OnFileParsed(ScummBlock root)
    {
        // _FitToContents();
    }

    protected virtual void OnReady() { }
    

    private void _OnHandleInput(InputEvent @event, Vector2 direction)
    {
        if(FocusManager.CurrentResized != null && FocusManager.CurrentResized != this) return;
        if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
        {
            MoveToFront();
            bool mousePressed = mb.Pressed;
            _resizing = mousePressed;
            if (mousePressed)
            {
                FocusManager.CurrentResized = this;
                _currentDirection = direction;
                _resizeStartMouse = GetGlobalMousePosition();
                _resizeStartSize = Size;
                _resizeStartPosition = Position;
            }
            else
            {
                FocusManager.CurrentResized = null;
                Vector2 finalSize = Size.SnapVector();
            
                finalSize.X = Mathf.Max(finalSize.X, MinimumPanelSize.X);
                finalSize.Y = Mathf.Max(finalSize.Y, MinimumPanelSize.Y);
                
                if (_currentDirection.X < 0 || _currentDirection.Y < 0)
                {
                    Vector2 sizeDiff = finalSize - Size;
                    if (_currentDirection.X < 0) Position -= new Vector2(sizeDiff.X, 0);
                    if (_currentDirection.Y < 0) Position -= new Vector2(0, sizeDiff.Y);
                }
            
                Size = finalSize;
                Position = Position.SnapVector();
            }
        }

        if (@event is InputEventMouseMotion && _resizing)
        {
            Vector2 mouseDiff = GetGlobalMousePosition() - _resizeStartMouse;

            Vector2 newSize = _resizeStartSize;
            Vector2 newPos = _resizeStartPosition;

            if (_currentDirection.X != 0)
            {
                float deltaX = mouseDiff.X * _currentDirection.X;
                float targetWidth = Mathf.Max(_resizeStartSize.X + deltaX, MinimumPanelSize.X);

                if (_currentDirection.X < 0)
                {
                    float actualDelta = targetWidth - _resizeStartSize.X;
                    newPos.X -= actualDelta;
                }
                newSize.X = targetWidth;
            }

            if (_currentDirection.Y != 0)
            {
                float deltaY = mouseDiff.Y * _currentDirection.Y;
                float targetHeight = Mathf.Max(_resizeStartSize.Y + deltaY, MinimumPanelSize.Y);

                if (_currentDirection.Y < 0)
                {
                    float actualDelta = targetHeight - _resizeStartSize.Y;
                    newPos.Y -= actualDelta;
                }
                newSize.Y = targetHeight;
            }

            Size = newSize;
            Position = newPos;
            _userResized = true;
        }
    }

    private void _OnResizeHandleInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mb)
        {
            MoveToFront();

            if (mb.ButtonIndex == MouseButton.Left)
            {
                bool mousePressed = mb.Pressed;
                _resizing = mousePressed;

                if (mousePressed)
                {
                    _resizeStartMouse = GetGlobalMousePosition();
                    _resizeStartSize = Size;
                }
                else
                {
                    if (Mathf.Abs(Position.X) < 10) Position = new Vector2(0, Position.Y);
                }
                
            }

        }

        if (@event is InputEventMouseMotion && _resizing)
        {
            var delta = GetGlobalMousePosition() - _resizeStartMouse;

            var newSize = _resizeStartSize + delta;

            newSize.X = Mathf.Max(newSize.X, MinimumPanelSize.X);
            newSize.Y = Mathf.Max(newSize.Y, MinimumPanelSize.Y);

            Size = newSize;
            _userResized = true;
        }
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mb)
        {
            if (mb.Pressed)
            {
                MoveToFront();
                EventBus.Instance.EmitSignal(EventBus.SignalName.PanelFocusRequested, PanelTitle);
            }
        }
    }

    private void _OnTitleBarInput(InputEvent @event)
    {

        if (@event is InputEventMouseButton mb)
        {
            bool mousePressed = mb.Pressed;
            _dragging = mousePressed;
            if (mousePressed)   
            {
                GD.Print("title bar pressed");
                _dragOffset = GetGlobalMousePosition() - GlobalPosition;
                MoveToFront();
            }
            else
            {
                GlobalPosition = GlobalPosition.SnapVector();
            }
        }

        if (@event is InputEventMouseMotion && _dragging)
        {
            var canvasRect = _canvas.GetGlobalRect();
            var panelSize = Size;

            var minX = canvasRect.Position.X;
            var maxX = canvasRect.End.X - panelSize.X;

            var minY = canvasRect.Position.Y;
            var maxY = canvasRect.End.Y - panelSize.Y;

            if (minX > maxX)
            {
                float mid = (minX + maxX) * 0.5f;
                minX = maxX = mid;
            }

            if (minY > maxY)
            {
                float mid = (minY + maxY) * 0.5f;
                minY = maxY = mid;
            }

            var newGlobal = GetGlobalMousePosition() - _dragOffset;

            newGlobal.X = Mathf.Clamp(newGlobal.X, minX, maxX);
            newGlobal.Y = Mathf.Clamp(newGlobal.Y, minY, maxY);

            GlobalPosition = newGlobal;
        }
    }

    private void _OnCollapsePressed()
    {
        _collapsed = !_collapsed;
        if (_collapsed)
        {
            _expandedHeight = Size.Y;
            CustomMinimumSize = new Vector2(Size.X, 28);
            Size = new Vector2(Size.X, 28);
            _contentRoot.Visible = false;
            _collapseButton.Text = s_expandIcon;
            _handlesContainer.Visible = false;
        }
        else
        {
            CustomMinimumSize = MinimumPanelSize;
            Size = new Vector2(Size.X, _expandedHeight);
            _contentRoot.Visible = true;
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

    }


}