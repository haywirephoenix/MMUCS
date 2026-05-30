using Godot;

public partial class UnifiedPreviewPanel : FloatingPanel
{
    public override string PanelTitle => "Preview";
    [Export] public RoomViewerContent RoomContent;
    [Export] public AkosViewerContent AkosContent;
    [Export] public OBIMViewerContent ObimContent;

    private Control _roomTab;
    private Control _akosTab;
    private Control _obimTab;
    private Control _activeTab;

    private const string s_spcDashSpc = " - ";
    
    protected override void OnReady()
    {
        RoomContent.OnReady();
        AkosContent.OnReady();
        ObimContent.OnReady();
        
        _roomTab = RoomContent;
        _akosTab = AkosContent;
        _obimTab = ObimContent;
        
        EventBus.Instance.RoomSelected += OnRoomSelected;
        EventBus.Instance.AkosSelected += OnAkosSelected;
        EventBus.Instance.ObimSelected += OnObimSelected;
        
        RoomContent.Visible = false;
        AkosContent.Visible = false;
        ObimContent.Visible = false;
        
        
        RoomContent.ParentPanel.PanelTitleChanged += OnPanelTitleChanged;
        AkosContent.ParentPanel.PanelTitleChanged += OnPanelTitleChanged;
        ObimContent.ParentPanel.PanelTitleChanged += OnPanelTitleChanged;
    }
    protected override void _OnBlockSelected(ScummBlock block)
    {
        RoomContent.OnBlockSelected(block);
        AkosContent.OnBlockSelected(block);
        ObimContent.OnBlockSelected(block);
    }
    protected override void OnExit()
    {
        EventBus.Instance.RoomSelected -= OnRoomSelected;
        EventBus.Instance.AkosSelected -= OnAkosSelected;
        EventBus.Instance.ObimSelected -= OnObimSelected;
        
        RoomContent.ParentPanel.PanelTitleChanged -= OnPanelTitleChanged;
        AkosContent.ParentPanel.PanelTitleChanged -= OnPanelTitleChanged;
        ObimContent.ParentPanel.PanelTitleChanged -= OnPanelTitleChanged;
    }
    public override void AssignNodes()
    {
        base.AssignNodes();
        RoomContent.AssignNodes();
        AkosContent.AssignNodes();
        ObimContent.AssignNodes();
    }
    private void OnPanelTitleChanged(string newTitle) => CallDeferred(nameof(SetTitleDeferred), newTitle);
    private void SetTitleDeferred(string newTitle)
    {
        _titleLabel.Text = newTitle;
        // if(newTitle == PanelTitle) return;
        // _titleLabel.Text = PanelTitle+s_spcDashSpc+newTitle;
    }
    
    private void OnRoomSelected() => ShowTab(_roomTab);
    private void OnAkosSelected() => ShowTab(_akosTab);
    private void OnObimSelected() => ShowTab(_obimTab);
    
    private void ShowTab(Control tab)
    {
        // if (_activeTab != null && _activeTab == tab) return;
        _activeTab?.Hide();
        _activeTab = tab;
        _activeTab?.Show();
    }
    
}
