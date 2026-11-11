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
    [Export] public PopupMenu _layoutMenu;
    
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
        _layoutMenu.IdPressed += LayoutMenuOnIdPressed;
        _layoutMenu.AboutToPopup += LayoutMenuOnAboutToPopup;

        _shadowTexture.Visible = !IsNativeMenu();
        
        CallDeferred(nameof(ClearDialogPanel));
    }
    private void LayoutMenuOnAboutToPopup()
    {
        UpdateLayoutMenuCheckmarks((int)LayoutModes.CurrentELayoutMode);
    }
    private void LayoutMenuOnIdPressed(long id)
    {
        switch (id)
        {
            case 0: 
            case 1: 
                OnLayoutMenuPressed((int)id); 
                break;
            case 99: 
                OnResetLayoutMenuPressed(); 
                break;
        }
    }

    private void OnLayoutMenuPressed(int selectedId)
    {
        // if (LayoutModes.GetELayoutMode() == (LayoutModes.ELayoutMode)selectedId) return;
        var eLayoutMode = (LayoutModes.ELayoutMode)selectedId;
    
        LayoutModes.SetLayoutMode(eLayoutMode);
        
        WindowManager.ApplyLayoutTemplate(LayoutModes.LayoutModeToString(eLayoutMode), true);

        UpdateLayoutMenuCheckmarks(selectedId);
    }

    private void UpdateLayoutMenuCheckmarks(int activeId)
    {

        for (int i = 0; i < _layoutMenu.ItemCount; i++)
        {
            int itemId = _layoutMenu.GetItemId(i);

            if (itemId == 99) continue;
            
            _layoutMenu.SetItemChecked(i, itemId == activeId);
            
        }
    }

    private void OnResetLayoutMenuPressed()
    {
        ConfigManager.ResetPanelLayouts();
        WindowManager.ResetWindowLayout();
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
            SetPanelVisible((int)id, !target.IsOpen);
       
        UpdateViewMenuChecks();
    }

    private void _OnToolsMenuPressed(long id)
    {
        // GD.Print($"Tools action: {id}");
        
        switch (id)
        {
            case 21: 
                FloatingPanel options = _mainCanvas.GetPanelByID(Consts.OPTS_PNL_ID);
                SetPanelVisible(options, true); 
            break;
            case 34: 
                FloatingPanel export = _mainCanvas.GetPanelByID(Consts.EXPORTER_PNL_ID);
                SetPanelVisible(export, true);
            break;
        }
    }

    private void SetPanelVisible(int id, bool visible = true, bool animate = true)
    {
        _mainCanvas.OpenPanel((int)id,visible, animate);
    }
    private void SetPanelVisible(FloatingPanel panel, bool visible = true, bool animate = true)
    {
        _mainCanvas.OpenPanel(panel,visible, animate);
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
        _viewMenu.Clear();

        bool classicMode = LayoutModes.IsClassicMode();
    
        for (int i = 0; i <= Consts.MAIN_PANEL_MAX_INDEX; i++)
        {
            var panel = _mainCanvas.GetPanelByID(i);
            
            if (panel == null) continue;
            
            bool shouldHide = (!classicMode && panel.IsClassic) || (classicMode && panel.PanelId == "Preview");
            
            if (shouldHide) continue; 
                
            _viewMenu.AddCheckItem(panel.Name, i); 
                
            int currentIdx = _viewMenu.GetItemIndex(i);

            bool disable = false;
            
            _viewMenu.SetItemChecked(currentIdx, panel.IsOpen);
            _viewMenu.SetItemDisabled(currentIdx, disable);
        }
    }
    
}