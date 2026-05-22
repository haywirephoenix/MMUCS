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
    [Export] public CheckButton _objectNamesToggle;

    private readonly List<TreeItem> _lflfItems = new();
    private readonly List<TreeItem> _obimItems = new();
    private readonly List<TreeItem> _filteredMatches = new();

    private bool showRoomNames = false;
    private bool showObjectNames = false;

    private string _currentQuery = string.Empty;
    private uint? _currentTargetTag = null;

    private static readonly NodePath PathMainCanvas = "../..";
    private static readonly NodePath PathContainer = "Layout/MarginContainer/ContentRoot/Container";
    private static readonly NodePath PathRoomNamesToggle = "Layout/MarginContainer/ContentRoot/Container/HBoxContainer/RoomNamesToggle";
    private static readonly NodePath PathObjectNamesToggle = "Layout/MarginContainer/ContentRoot/Container/HBoxContainer/ObjectNamesToggle";
    private static readonly NodePath PathTreeView = "Layout/MarginContainer/ContentRoot/Container/TreeView";
    private static readonly NodePath PathSearchBox = "Layout/MarginContainer/ContentRoot/Container/SearchBox";


    public override void AssignNodes()
    {
        base.AssignNodes();
        _mainCanvas = GetNode<MainCanvas>(PathMainCanvas);
        _roomNamesToggle = GetNode<CheckButton>(PathRoomNamesToggle);
        _objectNamesToggle = GetNode<CheckButton>(PathObjectNamesToggle);

        _Container = GetNode<VBoxContainer>(PathContainer);
        _tree = GetNode<Tree>(PathTreeView);
        _searchBox = GetNode<LineEdit>(PathSearchBox);
    }

    protected override void OnReady()
    {
        _searchBox.TextChanged += _OnSearchChanged;
        _tree.ItemSelected += _OnItemSelected;
        _tree.GuiInput += TreeOnGuiInput;

        _roomNamesToggle.Toggled += OnRoomNamesToggled;
        _objectNamesToggle.Toggled += ObjectNamesToggleOnToggled;
    }
    private void ObjectNamesToggleOnToggled(bool toggledOn)
    {
        showObjectNames = toggledOn;
        ToggleTreeObjectNames(toggledOn);
    }

    private void OnRoomNamesToggled(bool toggledOn)
    {
        showRoomNames = toggledOn;
        ToggleTreeRoomNames(toggledOn);
    }
    private void ToggleTreeRoomNames(bool enabled)
    {
        for (int i = 0; i < _lflfItems.Count; i++)
        {
            var item = _lflfItems[i];
            var block = item.GetMetadata(0).As<ScummBlock>();
            if (enabled)
            {
                if (!block.GetMetadataItem(ScummMeta.LFLF.roomNo, out Variant roomNo)) return;
                if (!block.GetMetadataItem(ScummMeta.LFLF.roomName, out Variant roomName)) return;

                item.SetText(0, $"{roomNo}.{roomName}");
            }
            else
            {
                item.SetText(0, block.TagName);
            }
        }
    }

    private void ToggleTreeObjectNames(bool enabled)
    {
        for (int i = 0; i < _obimItems.Count; i++)
        {
            var item = _obimItems[i];
            var block = item.GetMetadata(0).As<ScummBlock>();
            if (enabled)
            {
                if (!block.GetMetadataItem(ScummMeta.OBIM.name, out Variant obimName)) return;

                item.SetText(0, $"{obimName}");
            }
            else
            {
                item.SetText(0, block.TagName);
            }
        }
    }

    private void TreeOnGuiInput(InputEvent @event)
    {
        if (@event is InputEventKey { Echo: false, Keycode: Key.Shift } keyEvent)
        {
            _tree.SelectMode = keyEvent.Pressed ? Tree.SelectModeEnum.Multi : Tree.SelectModeEnum.Single;
            return;
        }

        if (!FocusManager.IsFocused(_tree)) return;

        if (@event is InputEventMouse mouseEvent && mouseEvent.ButtonMask == MouseButtonMask.Right)
        {
            _CopySelectedToClipboard();
            return;
        }

        if (@event is InputEventKey kEvent && kEvent.Pressed)
            _HandleKeyboardNavigation(kEvent);

    }

    private void _HandleKeyboardNavigation(InputEventKey keyEvent)
    {
        TreeItem selected = _tree.GetSelected();
        if (selected == null) return;

        string activeQuery = _currentQuery; 
        uint? activeTag = _currentTargetTag; 
        bool isSearching = !string.IsNullOrEmpty(activeQuery) || activeTag.HasValue;

        switch (keyEvent.Keycode)
        {
            case Key.Right:
                if (selected.Collapsed) selected.Collapsed = false;
                AcceptEvent();
                break;

            case Key.Left:
                selected.Collapsed = true;
                AcceptEvent();
                break;

            case Key.Down:
                TreeItem nextMatch = GetNextFilteredMatch(selected, activeQuery, activeTag);
            
                TreeItem nextFallback = isSearching 
                    ? selected.GetNextVisible() 
                    : _GetNextFoldAwareItem(selected);

                _NavigateToTarget(selected, nextMatch, nextFallback);
                AcceptEvent();
                break;

            case Key.Up:
                TreeItem prevMatch = GetPrevFilteredMatch(selected, activeQuery, activeTag);
            
                TreeItem prevFallback = isSearching 
                    ? selected.GetPrevVisible() 
                    : _GetPrevFoldAwareItem(selected);

                _NavigateToTarget(selected, prevMatch, prevFallback);
                AcceptEvent();
                break;
        }
    }
    
    private TreeItem _GetNextFoldAwareItem(TreeItem item)
    {
        if (item == null) return null;

        if (!item.Collapsed && item.GetFirstChild() != null)
            return item.GetFirstChild();

        if (item.GetNext() != null)
            return item.GetNext();

        TreeItem parent = item.GetParent();
        while (parent != null)
        {
            if (parent.GetNext() != null)
                return parent.GetNext();
            parent = parent.GetParent();
        }

        return null;
    }
    
    private TreeItem _GetPrevFoldAwareItem(TreeItem item)
    {
        if (item == null) return null;

        TreeItem prevSibling = item.GetPrev();
        if (prevSibling != null)
        {
            TreeItem lastChild = prevSibling;
            while (!lastChild.Collapsed && lastChild.GetFirstChild() != null)
            {
                TreeItem child = lastChild.GetFirstChild();
                while (child.GetNext() != null)
                {
                    child = child.GetNext();
                }
                lastChild = child;
            }
            return lastChild;
        }

        TreeItem parent = item.GetParent();
        if (parent != _tree.GetRoot())
        {
            return parent;
        }

        return null;
    }

    private TreeItem GetNextFilteredMatch(TreeItem current, string query, uint? targetTag)
    {
        if (string.IsNullOrEmpty(query) && !targetTag.HasValue) return null;

        TreeItem next = _GetNextLogicalItem(current);
        while (next != null)
        {
            if (IsActualMatch(next, query, targetTag))
                return next;
        
            next = _GetNextLogicalItem(next);
        }
        return null;
    }

    private TreeItem GetPrevFilteredMatch(TreeItem current, string query, uint? targetTag)
    {
        if (string.IsNullOrEmpty(query) && !targetTag.HasValue) return null;

        TreeItem prev = _GetPrevLogicalItem(current);
        while (prev != null)
        {
            if (IsActualMatch(prev, query, targetTag))
                return prev;
        
            prev = _GetPrevLogicalItem(prev);
        }
        return null;
    }

    private TreeItem _GetNextLogicalItem(TreeItem item)
    {
        if (item == null) return null;

        if (item.GetFirstChild() != null)
            return item.GetFirstChild();

        if (item.GetNext() != null)
            return item.GetNext();

        TreeItem parent = item.GetParent();
        while (parent != null)
        {
            if (parent.GetNext() != null)
                return parent.GetNext();
            parent = parent.GetParent();
        }

        return null;
    }

    private TreeItem _GetPrevLogicalItem(TreeItem item)
    {
        if (item == null) return null;

        TreeItem prevSibling = item.GetPrev();
        if (prevSibling != null)
        {
            TreeItem lastChild = prevSibling;
            while (lastChild.GetFirstChild() != null)
            {
                TreeItem child = lastChild.GetFirstChild();
                while (child.GetNext() != null)
                {
                    child = child.GetNext();
                }
                lastChild = child;
            }
            return lastChild;
        }

        TreeItem parent = item.GetParent();
        if (parent != _tree.GetRoot())
        {
            return parent;
        }

        return null;
    }

    private bool IsActualMatch(TreeItem item, string query, uint? targetTag)
    {
        if (string.IsNullOrEmpty(query) && !targetTag.HasValue)
            return true;

        var block = item.GetMetadata(0).As<ScummBlock>();
        if (block != null)
        {
            if (targetTag.HasValue && block.Tag == targetTag.Value)
                return true;

            if (!targetTag.HasValue && block.TagName != null && block.TagName.ToLower().Contains(query))
                return true;
        }

        return item.GetText(0).ToLower().Contains(query);
    }

    private void _NavigateToTarget(TreeItem current, TreeItem filteredTarget, TreeItem fallbackTarget)
    {
        TreeItem finalTarget = filteredTarget ?? fallbackTarget;

        if (finalTarget != null)
        {
            if (filteredTarget != null)
            {
                _RevealPathToItem(finalTarget);
            }

            finalTarget.Select(0);
            _tree.EnsureCursorIsVisible();
        }
    }

    private void _RevealPathToItem(TreeItem item)
    {
        if (item == null) return;

        TreeItem parent = item.GetParent();
        while (parent != null)
        {
            if (parent.Collapsed)
            {
                parent.Collapsed = false;
            }
            parent = parent.GetParent();
        }
    }

    private TreeItem _FindNextMatch(TreeItem startItem)
    {
        if (_filteredMatches.Count == 0) return null;

        TreeItem walker = startItem.GetNextVisible();
        while (walker != null)
        {
            if (_filteredMatches.Contains(walker)) return walker;
            walker = walker.GetNextVisible();
        }
        return null;
    }

    private TreeItem _FindPrevMatch(TreeItem startItem)
    {
        if (_filteredMatches.Count == 0) return null;

        TreeItem walker = startItem.GetPrevVisible();
        while (walker != null)
        {
            if (_filteredMatches.Contains(walker)) return walker;
            walker = walker.GetPrevVisible();
        }
        return null;
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

        TreeItem selectionRoot = selectedItems[0];
        TreeItem relativeTo = selectionRoot.GetParent();

        var paths = new System.Collections.Generic.List<string>();

        foreach (var item in selectedItems)
        {
            var pathParts = new System.Collections.Generic.List<string>();
            TreeItem temp = item;

            while (temp != null && temp != relativeTo)
            {
                pathParts.Insert(0, temp.GetText(0));
                temp = temp.GetParent();
            }

            paths.Add(string.Join("/", pathParts));
        }

        string finalOutput = string.Join("\n", paths);
        DisplayServer.ClipboardSet(finalOutput);
        GD.Print($"Copied {paths.Count} selection-relative paths.");
    }




    public void LoadBlocks(ScummBlock root)
    {
        _tree.Clear();
        _lflfItems.Clear();
        _obimItems.Clear();
        var rootItem = _tree.CreateItem();
        _PopulateItem(root, rootItem);

        var first = FindFirstRealBlock(root);
        if (first == null)
        {
            GD.Print($"[AUTOSELECT] first block not found");
            return;
        }



        EventBus.Instance.EmitSignal(EventBus.SignalName.BlockSelected, first);
    }



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
        if (parent != _tree.GetRoot() && block.Tag != ScummTag.LECF)
            item.Collapsed = true;
        if (block.Tag == ScummTag.LFLF)
            _lflfItems.Add(item);

        if (block.Tag == ScummTag.OBIM)
            _obimItems.Add(item);

        foreach (var child in block.Children)
            _PopulateItem(child, item);
    }

    private void _OnItemSelected()
    {
        var selected = _tree.GetSelected();
        if (selected == null) return;

        var block = selected.GetMetadata(0).As<ScummBlock>();
        if (block == null) return;

        EventBus.Instance.EmitSignal(EventBus.SignalName.BlockSelected, block);
    }

    private void _OnHexOffsetSelected(int offset, int length)
    {
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
        string cleanQuery = query.Trim();
        uint? targetTag = null;

        if (cleanQuery.Length == 4)
        {
            try
            {
                targetTag = ScummTag.FromString(cleanQuery.ToUpperInvariant());
            }
            catch (ArgumentException)
            {
                targetTag = null;
            }
        }

        _currentQuery = cleanQuery.ToLower();
        _currentTargetTag = targetTag;

        _FilterTree(_tree.GetRoot(), _currentQuery, _currentTargetTag);
    }

    private bool _FilterTree(TreeItem item, string query, uint? targetTag, bool ancestorMatched = false)
    {
        if (item == null) return false;

        if (item == _tree.GetRoot())
            _filteredMatches.Clear();

        bool selfMatch = false;

        if (string.IsNullOrEmpty(query) && !targetTag.HasValue)
        {
            selfMatch = true;
        }
        else
        {
            var block = item.GetMetadata(0).As<ScummBlock>();
            if (block != null)
            {
                if (targetTag.HasValue)
                {
                    if (block.Tag == targetTag.Value) selfMatch = true;
                }
                else
                {
                    if (block.TagName != null && block.TagName.ToLower().Contains(query)) selfMatch = true;
                }
            }

            if (!selfMatch && item.GetText(0).ToLower().Contains(query))
                selfMatch = true;
        }

        if (selfMatch)
            _filteredMatches.Add(item);

        bool isThisItemVisible = selfMatch || ancestorMatched;

        bool anyChildVisible = false;
        var child = item.GetFirstChild();
        while (child != null)
        {
            anyChildVisible |= _FilterTree(child, query, targetTag, isThisItemVisible);
            child = child.GetNext();
        }

        item.Visible = isThisItemVisible || anyChildVisible;

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


    private static readonly Dictionary<uint, Color> TagColors = new()
    {
        {
            ScummTag.LECF, new("e8c07d")
        },
        {
            ScummTag.LFLF, new("e8c07d")
        },
        {
            ScummTag.ROOM, new("7dcfe8")
        },
        {
            ScummTag.RMHD, new("7dcfe8")
        },
        {
            ScummTag.RMIM, new("7dcfe8")
        },
        {
            ScummTag.BMAP, new("7de8a0")
        },
        {
            ScummTag.AKOS, new("e8817d")
        },
        {
            ScummTag.AKCD, new("e8817d")
        },
        {
            ScummTag.AKPL, new("e8817d")
        },
        {
            ScummTag.CHAR, new("c87de8")
        },
        {
            ScummTag.SCRP, new("e8d77d")
        },
        {
            ScummTag.OBCD, new("d0d0d0")
        },
        {
            ScummTag.OBIM, new("d0d0d0")
        },
    };
}