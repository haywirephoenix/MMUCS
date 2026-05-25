using Godot;

public partial class MMenuBar : MenuBar
{
    
    private static MMenuBar Instance;
    
    [Export] public MainCanvas _mainCanvas;
    [Export] public TextureRect _shadowTexture;
    
    // [Export] public MenuBar _menuBar;
    [Export] public PopupMenu _fileMenu;
    [Export] public PopupMenu _toolsMenu;
    [Export] public PopupMenu _viewMenu;
    
    [Export] public FileDialog _openDialog;
    [Export] public Panel _openDialogPanel;

    public override void _EnterTree()
    {
        Instance = this;
    }

    public override void _Ready()
    {
        _openDialog.FileSelected += _OnFileSelected;
        _fileMenu.IdPressed += _OnFileMenuPressed;
        
        _viewMenu.AboutToPopup += UpdateViewMenuChecks;
        _viewMenu.IdPressed += _OnViewMenuPressed;
        _toolsMenu.IdPressed += _OnToolsMenuPressed;
        _shadowTexture.Visible = !IsNativeMenu();
        
        CallDeferred(nameof(ClearDialogPanel));
    }
    
    public static void OpenFileBrowser()
    {
        //todo: animate filedialog?
        Instance._openDialog.Popup();
    }

    private void ClearDialogPanel()
    {
        if (_openDialog == null)
        {
            GD.Print("No open dialog found");
            return;
        }
        _openDialogPanel = _openDialog.GetChild(0,true) as Panel;
        
        if (_openDialogPanel != null)
        {
            // _openDialogPanel.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
            // _openDialogPanel.Visible = false;
            var newMat = ThemeManager.Instance.WindowMaterial;
            //newMat.Shader = GD.Load<Shader>("res://shaders/blur_reveal.gdshader");
            _openDialogPanel.Material = newMat;
        }
    }
    
    private void _OnViewMenuPressed(long id)
    {
        FloatingPanel target = _mainCanvas.GetPanelByID(id);
        
        if (target != null)
            SetPanelVisible((int)id,!target.IsOpen);
           
        // target.Visible = !target.Visible;
        UpdateViewMenuChecks();
    }

    private void _OnToolsMenuPressed(long id)
    {
        // GD.Print($"Tools action: {id}");
        switch (id)
        {
            case 24: SetPanelVisible(Consts.OPTS_PNL_ID); break;
        }
    }

    private void SetPanelVisible(int id, bool visible = true, bool animate = false)
    {
        _mainCanvas.OpenPanel((int)id,visible, animate);
    }
   
    
    private void _OnFileMenuPressed(long id)
    {
        switch (id)
        {
            case 0: OpenFileBrowser(); break;
            case 2: _mainCanvas.GetTree().Quit(); break;
        }
    }
    
    private void _OnFileSelected(string path)
    {
        _mainCanvas.OnFileSelected(path);
    }
    
    private void UpdateViewMenuChecks()
    {
        for (int i = 0; i <= Consts.MAIN_PANEL_MAX_INDEX; i++)
        {
            var panel = _mainCanvas.GetPanelByID(i);
            if(panel != null)
                _viewMenu?.SetItemChecked(i, panel.IsOpen);
        }
    }
    
}