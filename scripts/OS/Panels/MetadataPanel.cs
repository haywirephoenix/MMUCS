using System;
using Godot;

public partial class MetadataPanel : FloatingPanel
{
    public override string PanelTitle => "Metadata";
    
    [Export] public Label _blockPath;
    [Export] public GridContainer _propertyGrid;
    [Export] public GridContainer _metagrid;
    [Export] public TextureRect _imagePreview;
    [Export] public Label _noPreviewLabel;
    [Export] public VBoxContainer _vbox;
    
    private static readonly NodePath VBoxPath = "Layout/MarginContainer/ContentRoot/ScrollContainer/VBox";
    private static readonly NodePath PathBlockPath = "Layout/MarginContainer/ContentRoot/ScrollContainer/VBox/MarginContainer/BlockPath";
    private static readonly NodePath PathPropertyGrid = "Layout/MarginContainer/ContentRoot/ScrollContainer/VBox/PropertyGrid";
    private static readonly NodePath PathMetaGrid = "Layout/MarginContainer/ContentRoot/ScrollContainer/VBox/MetaGrid";
    private static readonly NodePath PathimagePreview = "Layout/MarginContainer/ContentRoot/ScrollContainer/VBox/imagePreview";
    private static readonly NodePath PathnoPreviewLabel = "Layout/MarginContainer/ContentRoot/ScrollContainer/VBox/noPreviewLabel";


    protected override void OnReady()
    {
        GuiInput += TreeOnGuiInput;
        
        EventBus.Instance.AssetSelected += _OnAssetSelected;
    }
    
    private void TreeOnGuiInput(InputEvent @event)
    {
        if (@event is InputEventMouse mouseEvent)
        {
            if (mouseEvent.ButtonMask == MouseButtonMask.Right)
                _CopyGridsToClipboard();
        }
    }
    
    private void _CopyGridsToClipboard()
    {
        var lines = new System.Collections.Generic.List<string>();

        void ProcessGrid(GridContainer grid, string sectionHeader)
        {
            var children = grid.GetChildren();
            if (children.Count == 0) return;

            lines.Add($"--- {sectionHeader} ---");
        
            for (int i = 0; i < children.Count; i += 2)
            {
                if (children[i] is not Label keyLabel || children[i + 1] is not Label valueLabel)
                    continue;
                
                string key = keyLabel.Text.PadRight(20);
                string value = valueLabel.Text;
                lines.Add($"{key}: {value}");
            }
            lines.Add(""); 
        }

        ProcessGrid(_propertyGrid, "PROPERTIES");
        ProcessGrid(_metagrid, "METADATA");

        if (lines.Count > 0)
        {
            string finalOutput = string.Join("\n", lines);
            DisplayServer.ClipboardSet(finalOutput.TrimEnd());
            StatusBar.SetStatus("Grid data copied to clipboard.");
        }
    }
    
    public override void AssignNodes()
    {
        base.AssignNodes();
        
        _vbox = GetNode<VBoxContainer>(VBoxPath);;
        _blockPath = GetNode<Label>(PathBlockPath);
        _propertyGrid = GetNode<GridContainer>(PathPropertyGrid);
        _imagePreview = GetNode<TextureRect>(PathimagePreview);
        _noPreviewLabel = GetNode<Label>(PathnoPreviewLabel);
        _metagrid = GetNode<GridContainer>(PathMetaGrid);
    }
    
    protected override void _OnBlockSelected(ScummBlock obimBlock)
    {
        // GD.Print($"[META] {block.TagName}");
        
        SetTitle($"Properties — {obimBlock.TagName}");
        _blockPath.Text = obimBlock.FullPath;

        _ClearGrid();

        if (!string.IsNullOrEmpty(obimBlock.FullName))
        {
            _AddRow("Name", obimBlock.FullName);
            _AddRow("Description", obimBlock.Description);
        }
        
        _AddRow("Tag", obimBlock.TagName);
        _AddRow("Offset", $"0x{obimBlock.Offset:X8}  ({obimBlock.Offset})");
        _AddRow("Size", $"{obimBlock.Size} bytes");
        _AddRow("Data offset", $"0x{obimBlock.DataOffset:X8}");
        _AddRow("Children", obimBlock.Children.Count.ToString());

        // Decoded metadata
        foreach (var (vKey, vValue) in obimBlock.GetMetaDataDict())
        {
            string label = "";
            
            if (obimBlock.MetaSchema != null && vKey.VariantType == Variant.Type.Int)
            {
                label = Enum.GetName(obimBlock.MetaSchema, vKey.AsInt32()) ?? $"Unknown({vKey})";
            }
            else
            {
                label = vKey.AsString();
            }
    
            _AddRow(label, vValue.ToString(), true);
        }

        // AddExtraData(block);
        
        // bool hasPreview = block.TagName is "BMAP" or "AKOS" or "CHAR" or "AKPL";
        //_noPreviewLabel.Visible = !hasPreview;
        //_imagePreview.Visible = false;
    }

    private void _OnAssetSelected(string assetId, string assetType)
    {
        SetTitle($"Properties — {assetType}");
        _blockPath.Text = $"{assetType}: {assetId}";

        _ClearGrid();
        _AddRow("ID", assetId);
        _AddRow("Type", assetType);
    }
    
    public void SetPreviewImage(Image image)
    {
        if (image == null)
        {
            _imagePreview.Visible = false;
            _noPreviewLabel.Visible = true;
            return;
        }

        _imagePreview.Texture = ImageTexture.CreateFromImage(image);
        _imagePreview.Visible = true;
        _noPreviewLabel.Visible = false;
    }

    /// <summary>
    /// Add extra decoded properties to the panel from outside.
    /// Call after BlockSelected is emitted with your decoded results.
    /// </summary>
    public void AddProperty(string key, string value)
    {
        _AddRow(key, value);
    }

    private void _AddRow(string key, string value, bool isMeta = false)
    {
        var grid = isMeta ? _metagrid : _propertyGrid;
        var keyLabel = new Label();
        keyLabel.Text = key;
        keyLabel.AddThemeColorOverride("font_color", new Color("aaaaaa"));
        keyLabel.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        keyLabel.AddThemeFontSizeOverride("font_size", 13);
        //keyLabel.ContextMenuEnabled = true;
        //keyLabel.SelectionEnabled = true;
        grid.AddChild(keyLabel);

        var valueLabel = new Label();
        valueLabel.Text = value;
        valueLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        valueLabel.SizeFlagsHorizontal = SizeFlags.Fill;
        valueLabel.CustomMinimumSize = new Vector2(200, 10);
        //valueLabel.ContextMenuEnabled = true;
        //valueLabel.SelectionEnabled = true;
        valueLabel.AddThemeFontSizeOverride("font_size", 13);
        grid.AddChild(valueLabel);
    }

    private void _ClearGrid()
    {
        foreach (Node child in _propertyGrid.GetChildren())
            child.QueueFree();
        
        foreach (Node child in _metagrid.GetChildren())
            child.QueueFree();
    }
}
