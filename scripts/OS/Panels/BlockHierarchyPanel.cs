using System;
using Godot;
using System.Collections.Generic;


public partial class BlockHierarchyPanel : FloatingPanel
{
    public override string PanelTitle => "Block Hierarchy";
    
    [Export] public MainCanvas _mainCanvas;
    
    [Export] public Tree _tree;
    [Export] public LineEdit _searchBox;
    [Export] public VBoxContainer _Container;
    [Export] public CheckButton _roomNamesToggle;
    
    private static readonly NodePath PathMainCanvas = "../..";
    private static readonly NodePath PathContainer = "Layout/MarginContainer/ContentRoot/Container";
    private static readonly NodePath PathRoomNamesToggle = "Layout/MarginContainer/ContentRoot/Container/HBoxContainer/RoomNamesToggle";
    private static readonly NodePath PathTreeView = "Layout/MarginContainer/ContentRoot/Container/TreeView";
    private static readonly NodePath PathSearchBox = "Layout/MarginContainer/ContentRoot/Container/SearchBox";
    
    private bool hasFocus = false;
    
    public override void AssignNodes()
    {
        base.AssignNodes();
        // _mainCanvas = GetParent<Control>().GetParent<MainCanvas>();
        _mainCanvas = GetNode<MainCanvas>(PathMainCanvas);
        _roomNamesToggle = GetNode<CheckButton>(PathRoomNamesToggle);
        
        // var parentNode = GetParent().GetParent();
        // _mainCanvas = vbox as MainCanvas;
        // if (parentNode is VBoxContainer vbox)
        // {
        //     _mainCanvas = vbox as MainCanvas;
        // }
        _Container = GetNode<VBoxContainer>(PathContainer);
        _tree = GetNode<Tree>(PathTreeView);
        _searchBox = GetNode<LineEdit>(PathSearchBox);
    }

    protected override void OnReady()
    {
        // Listen for hex clicks coming back
        _searchBox.TextChanged += _OnSearchChanged;
        _tree.ItemSelected += _OnItemSelected;
        _tree.GuiInput += TreeOnGuiInput;

        _roomNamesToggle.Toggled += OnRoomNamesToggled;
        // EventBus.Instance.HexOffsetSelected += _OnHexOffsetSelected;
    }
    
    private bool showRoomNames = false;

    private void OnRoomNamesToggled(bool state)
    {
        showRoomNames = state;
        ToggleTreeRoomNames(state);
    }
    private void ToggleTreeRoomNames(bool enabled)
    {
        for (int i = 0; i < _lflfItems.Count; i++)
        {
            var item = _lflfItems[i];
            var block = item.GetMetadata(0).As<ScummBlock>();
            if (enabled)
            {
                if(!block.GetMetadataItem(ScummMeta.LFLF.roomNo, out Variant roomNo)) return;
                if(!block.GetMetadataItem(ScummMeta.LFLF.roomName, out Variant roomName)) return;
               
                item.SetText(0, $"{roomNo}.{roomName}");
            }
            else
            {
                item.SetText(0, block.TagName);
            }
        }
    }
    
    private void TreeOnGuiInput(InputEvent @event)
    {
        if (@event is InputEventKey { Echo: false, Keycode: Key.Shift } keyevent)
            _tree.SelectMode = keyevent.Pressed ? Tree.SelectModeEnum.Multi : Tree.SelectModeEnum.Single;
        
        if (!FocusManager.IsFocused(_tree)) return;
        if (@event is InputEventMouse mouseEvent)
        {
            if (mouseEvent.ButtonMask == MouseButtonMask.Right)
                _CopySelectedToClipboard();
        }
        
        if (@event is InputEventKey keyEvent && keyEvent.Pressed)
        {
            TreeItem selected = _tree.GetSelected();
            
            // Specifically looking for the Right Arrow
            if (selected != null && keyEvent.Keycode == Key.Right)
            {
                if (selected.Collapsed)
                    selected.Collapsed = false;
                AcceptEvent();
            }
            else if (selected != null && keyEvent.Keycode == Key.Left)
            {
                selected.Collapsed = true;
                AcceptEvent();
            }
            if (selected != null && keyEvent.Keycode == Key.Down)
            {
                TreeItem next = selected.GetNextVisible();
                if (next != null)
                {
                    next.Select(0); // Select the first column
                    _tree.EnsureCursorIsVisible(); // Scroll to it if off-screen
                }
                AcceptEvent();
            }
            else if (selected != null && keyEvent.Keycode == Key.Up)
            {
                TreeItem prev = selected.GetPrevVisible();
                if (prev != null)
                {
                    prev.Select(0);
                    _tree.EnsureCursorIsVisible();
                }
                AcceptEvent();
            }
            
        }
    }
    
    private void _CopySelectedToClipboard()
    {
        var selectedItems = new System.Collections.Generic.List<TreeItem>();
        TreeItem current = _tree.GetNextSelected(null);
    
        while (current != null)
        {
            selectedItems.Add(current);
            current = _tree.GetNextSelected(current);
        }

        if (selectedItems.Count == 0) return;

        // 1. Find the "Selection Root"
        // We assume the first item in the selection list is the topmost 
        // because GetNextSelected walks the tree in order.
        TreeItem selectionRoot = selectedItems[0];
        // We want the path to be relative to the root's PARENT 
        // so that the root's name is included in the path.
        TreeItem relativeTo = selectionRoot.GetParent();

        var paths = new System.Collections.Generic.List<string>();

        foreach (var item in selectedItems)
        {
            var pathParts = new System.Collections.Generic.List<string>();
            TreeItem temp = item;

            // 2. Walk up only until we hit the relativeTo node
            while (temp != null && temp != relativeTo)
            {
                pathParts.Insert(0, temp.GetText(0));
                temp = temp.GetParent();
            }

            paths.Add(string.Join("/", pathParts));
        }

        // 3. Finalize
        string finalOutput = string.Join("\n", paths);
        DisplayServer.ClipboardSet(finalOutput);
        GD.Print($"Copied {paths.Count} selection-relative paths.");
    }

   

    
    /// <summary>
    /// Call this after parsing SCUMM file to populate the tree.
    /// </summary>
    public void LoadBlocks(ScummBlock root)
    {
        _tree.Clear();
        _lflfItems.Clear();
        var rootItem = _tree.CreateItem();
        _PopulateItem(root, rootItem);
        
        var first = FindFirstRealBlock(root);
        if (first == null)
        {
            GD.Print($"[AUTOSELECT] first block not found");
            return;
        }
        
        // TreeItem child = rootItem.GetFirstChild();
        // int count = 0;
        // while (child != null)
        // {
        //     if(count > 3)
        //         child.Collapsed = true;
        //     else if (count == 3)
        //     {
        //         child.Select(0);
        //     }
        //     
        //     child = child.GetNext();
        //     count++;
        // }
        
        
          
        // GD.Print($"[AUTOSELECT] {first.Tag}");
        EventBus.Instance.EmitSignal(EventBus.SignalName.BlockSelected, first);
    }

    private List<TreeItem> _lflfItems = new();

    private void _PopulateItem(ScummBlock block, TreeItem parent)
    {
        var item = _tree.CreateItem(parent);
        item.SetText(0, block.TagName);
        item.SetText(1, _FormatSize(block.Size));
        item.SetTooltipText(0, block.DisplayName);
        item.SetMetadata(0, block);

        if (TagColors.TryGetValue(block.Tag, out var color))
        {
            item.SetCustomColor(0, color);
        }
        // if (parent != _tree.GetRoot()) 
        // {
        if(parent != _tree.GetRoot() && block.TagName != "LECF")
            item.Collapsed = true;
        // }
        if(block.TagName == "LFLF")
            _lflfItems.Add(item);

        // Bold containers
        // if (block.IsContainer)
        //     item.SetCustomFontSize(0, 14);

        foreach (var child in block.Children)
            _PopulateItem(child, item);
    }

    private void _OnItemSelected()
    {
        var selected = _tree.GetSelected();
        if (selected == null) return;

        var block = selected.GetMetadata(0).As<ScummBlock>();
        if (block == null) return;
        // GD.Print($"[SELECT] {block.Tag} @0x{block.Offset:X}");

        EventBus.Instance.EmitSignal(EventBus.SignalName.BlockSelected, block);
    }

    private void _OnHexOffsetSelected(int offset, int length)
    {
        // Walk the tree to find and select the block owning this offset
        _SelectBlockAtOffset(_tree.GetRoot(), offset);
    }

    private bool _SelectBlockAtOffset(TreeItem item, int offset)
    {
        if (item == null) return false;

        var block = item.GetMetadata(0).As<ScummBlock>();
        if (block != null && offset >= block.Offset && offset < block.Offset + block.Size)
        {
            item.Select(0);
            item.UncollapseTree();
            _tree.ScrollToItem(item);

            // Try to find a more specific child match first
            var child = item.GetFirstChild();
            while (child != null)
            {
                if (_SelectBlockAtOffset(child, offset)) return true;
                child = child.GetNext();
            }
            return true;
        }

        var next = item.GetFirstChild();
        while (next != null)
        {
            if (_SelectBlockAtOffset(next, offset)) return true;
            next = next.GetNext();
        }
        return false;
    }

    private void _OnSearchChanged(string query)
    {
        _FilterTree(_tree.GetRoot(), query.ToLower());
    }

    private bool _FilterTree(TreeItem item, string query)
    {
        if (item == null) return false;

        bool anyChildVisible = false;
        var child = item.GetFirstChild();
        while (child != null)
        {
            anyChildVisible |= _FilterTree(child, query);
            child = child.GetNext();
        }

        bool selfMatch = string.IsNullOrEmpty(query) || item.GetText(0).ToLower().Contains(query);
        item.Visible = selfMatch || anyChildVisible;
        return item.Visible;
    }

   

    private static string _FormatSize(int bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0f:F1} KB";
        return $"{bytes / (1024.0f * 1024):F1} MB";
    }
    
    private ScummBlock FindFirstRealBlock(ScummBlock root)
    {
        foreach (var child in root.Children)
        {
            if (child.Tag == ScummTag.LFLF || child.Tag == ScummTag.ROOM)
                return child;
        }
        return root.Children.Count > 0 ? root.Children[0] : null;
    }
    
    
    // Tag -> display color
    private static readonly Dictionary<uint, Color> TagColors = new()
    {
        { ScummTag.LECF, new("e8c07d") },
        { ScummTag.LFLF, new("e8c07d") },
        { ScummTag.ROOM, new("7dcfe8") },
        { ScummTag.RMHD, new("7dcfe8") },
        { ScummTag.RMIM, new("7dcfe8") },
        { ScummTag.BMAP, new("7de8a0") },
        { ScummTag.AKOS, new("e8817d") },
        { ScummTag.AKCD, new("e8817d") },
        { ScummTag.AKPL, new("e8817d") },
        { ScummTag.CHAR, new("c87de8") },
        { ScummTag.SCRP, new("e8d77d") },
        { ScummTag.OBCD, new("d0d0d0") },
        { ScummTag.OBIM, new("d0d0d0") },
    };
}
