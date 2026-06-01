using System;
using System.Text;
using Godot;

public partial class MetadataPanel : FloatingPanel
{
    public override string PanelTitle => "Metadata";
    
    [Export] public Label _blockPath;
    [Export] public TextureRect _imagePreview;
    [Export] public Label _noPreviewLabel;
    [Export] public VBoxContainer _vbox;
    
    [Export] public RichTextLabel _metadataDisplay;
    
    private const string vBoxPath = "Layout/MarginContainer/ContentRoot/ScrollContainer/VBox";
    
    private static readonly NodePath VBoxPath = vBoxPath;
    private static readonly NodePath PathBlockPath = vBoxPath+"/BlockPath";
    private static readonly NodePath PathMetadataDisplay = vBoxPath+"/MetadataDisplay";
    private static readonly NodePath PathimagePreview = vBoxPath+"/imagePreview";
    private static readonly NodePath PathnoPreviewLabel = vBoxPath+"/noPreviewLabel";

    private System.Collections.Generic.List<(string Key, string Value)> _properties = new();
    private System.Collections.Generic.List<(string Key, string Value)> _metadata = new();

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

        void ProcessSection(System.Collections.Generic.List<(string Key, string Value)> section, string sectionHeader)
        {
            if (section.Count == 0) return;

            lines.Add(sectionHeader);
            foreach (var row in section)
            {
                string key = row.Key.PadRight(20);
                lines.Add($"{key}: {row.Value}");
            }
            lines.Add(""); 
        }

        ProcessSection(_properties, "Properties");
        ProcessSection(_metadata, "Metadata");

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
        
        _vbox = GetNode<VBoxContainer>(VBoxPath);
        _blockPath = GetNode<Label>(PathBlockPath);
        _imagePreview = GetNode<TextureRect>(PathimagePreview);
        _noPreviewLabel = GetNode<Label>(PathnoPreviewLabel);
        
        _metadataDisplay = GetNode<RichTextLabel>(PathMetadataDisplay);
        
        _metadataDisplay.BbcodeEnabled = true;
        _metadataDisplay.SelectionEnabled = true;
        _metadataDisplay.ContextMenuEnabled = true;
        _metadataDisplay.FitContent = true;
        _metadataDisplay.ScrollActive = false;
        _metadataDisplay.AddThemeFontSizeOverride("font_size", 13);
    }
    
    protected override void _OnBlockSelected(ScummBlock block)
    {
        SetTitle($"Metadata — {block.TagName}");
        _blockPath.Text = block.FullPath;

        _ClearGrid();

        if (!string.IsNullOrEmpty(block.FullName))
        {
            _AddRow("Name", block.FullName);
            _AddRow("Description", block.Description);
        }
        
        _AddRow("Tag", block.TagName);
        _AddRow("Offset", $"0x{block.Offset:X8}  ({block.Offset})");
        _AddRow("Size", $"{block.Size} bytes");
        _AddRow("Data offset", $"0x{block.DataOffset:X8}");
        _AddRow("Children", block.Children.Count.ToString());

        foreach (var (vKey, vValue) in block.GetMetaDataDict())
        {
            string label = "";
            if (block.MetaSchema != null && vKey.VariantType == Variant.Type.Int)
            {
                label = Enum.GetName(block.MetaSchema, vKey.AsInt32()) ?? $"Unknown({vKey})";
            }
            else
            {
                label = vKey.AsString();
            }
    
            _AddRow(label, vValue.ToString(), true);
        }

        RenderText();
    }

    private void _OnAssetSelected(string assetId, string assetType)
    {
        SetTitle($"Properties — {assetType}");
        _blockPath.Text = $"{assetType}: {assetId}";

        _ClearGrid();
        _AddRow("ID", assetId);
        _AddRow("Type", assetType);
        
        RenderText();
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

    public void AddProperty(string key, string value)
    {
        _AddRow(key, value);
        RenderText();
    }

    private void _AddRow(string key, string value, bool isMeta = false)
    {
        if (isMeta)
            _metadata.Add((key, value));
        else
            _properties.Add((key, value));
    }

    private void _ClearGrid()
    {
        _properties.Clear();
        _metadata.Clear();
        _metadataDisplay.Text = "";
    }


    private void RenderText()
    {
        StringBuilder sb = new StringBuilder();

        void AppendSection(System.Collections.Generic.List<(string Key, string Value)> section, string title)
        {
            if (section.Count == 0) return;

            sb.AppendLine($"[b][color=gray]{title}[/color][/b]\n");
            
            sb.AppendLine("[table=2, 1, 2]"); 
            
            foreach (var row in section)
            {
                sb.AppendLine($"[cell][color=aaaaaa]{row.Key}[/color][/cell]");
                sb.AppendLine($"[cell]{row.Value}[/cell]");
            }
            
            sb.AppendLine("[/table]");
            sb.AppendLine();
        }

        AppendSection(_properties, "Properties");
        AppendSection(_metadata, "Metadata");

        _metadataDisplay.Text = sb.ToString();
    }
}