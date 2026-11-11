using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using MMUCS.Core.Scumm;

namespace MMUCS.Desktop.ViewModels;

/// <summary>Display options shared by every node of one tree.</summary>
public sealed partial class TreeOptions : ObservableObject
{
    [ObservableProperty] private bool _showRoomNames = true;
    [ObservableProperty] private bool _showObjectNames;
    [ObservableProperty] private bool _colorizeTags = true;

    /// <summary>Blocks that pass the current filter, or null when no filter is active.</summary>
    public HashSet<ScummBlock> VisibleBlocks { get; set; }
}

/// <summary>Tree node wrapping a <see cref="ScummBlock"/>. Children are materialised lazily.</summary>
public sealed partial class BlockNodeViewModel : ObservableObject
{
    public ScummBlock Block { get; }
    public BlockNodeViewModel Parent { get; }

    [ObservableProperty] private bool _isExpanded;

    private ObservableCollection<BlockNodeViewModel> _children;

    public TreeOptions Options { get; }

    public BlockNodeViewModel(ScummBlock block, TreeOptions options, BlockNodeViewModel parent = null)
    {
        Block = block;
        Options = options;
        Parent = parent;
    }

    /// <summary>
    /// Re-raises Label on this node and its already-materialised descendants. Called when a naming
    /// option changes (instead of per-node event subscriptions, which would leak on tree rebuilds).
    /// </summary>
    public void RefreshLabels()
    {
        if (Block.Tag is ScummTag.LFLF or ScummTag.OBIM) OnPropertyChanged(nameof(Label));
        if (_children == null) return;
        foreach (var c in _children) c.RefreshLabels();
    }

    private static readonly Dictionary<uint, IBrush> TagBrushes = new()
    {
        { ScummTag.LECF, Brush("#E8C07D") },
        { ScummTag.LFLF, Brush("#E8C07D") },
        { ScummTag.ROOM, Brush("#7DCFE8") },
        { ScummTag.RMHD, Brush("#7DCFE8") },
        { ScummTag.RMIM, Brush("#7DCFE8") },
        { ScummTag.BMAP, Brush("#7DE8A0") },
        { ScummTag.AKOS, Brush("#E8817D") },
        { ScummTag.AKCD, Brush("#E8817D") },
        { ScummTag.AKPL, Brush("#E8817D") },
        { ScummTag.CHAR, Brush("#C87DE8") },
        { ScummTag.SCRP, Brush("#E8D77D") },
        { ScummTag.OBCD, Brush("#D0D0D0") },
        { ScummTag.OBIM, Brush("#D0D0D0") },
    };

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex)).ToImmutable();

    /// <summary>Tag colour (same palette as the Godot tree); null for uncoloured tags.</summary>
    public IBrush TagBrush => TagBrushes.TryGetValue(Block.Tag, out var b) ? b : null;
    public bool HasTagColor => TagBrushes.ContainsKey(Block.Tag);

    public string Tag => Block.TagName ?? ScummTag.ToString(Block.Tag);

    public string Label => LabelFor(Block, Options);

    /// <summary>Display text for a block; also what the tree filter matches against.</summary>
    public static string LabelFor(ScummBlock block, TreeOptions options)
    {
        string tag = block.TagName ?? ScummTag.ToString(block.Tag);
        if (block.Tag == ScummTag.LFLF) return LflfLabel(block, tag, options?.ShowRoomNames == true);
        if (block.Tag == ScummTag.OBIM && options?.ShowObjectNames == true
            && block.GetMetaDataDict().TryGetValue(MetaValue.From(ScummMeta.OBIM.name), out var objName)
            && !string.IsNullOrEmpty((string)objName))
            return $"{tag}  {(string)objName}";
        string name = block.FullName;
        if (string.IsNullOrEmpty(name) || name == tag) return tag;
        return $"{tag}  {name}";
    }

    private static string LflfLabel(ScummBlock block, string tag, bool showRoomNames)
    {
        var meta = block.GetMetaDataDict();
        bool hasNo = meta.TryGetValue(MetaValue.From(ScummMeta.LFLF.roomNo), out var no);
        if (showRoomNames && hasNo)
        {
            return meta.TryGetValue(MetaValue.From(ScummMeta.LFLF.roomName), out var name)
                ? $"{(int)no}. {(string)name}"
                : $"{(int)no}. (room {(int)no})";
        }
        // The parser overwrites FullName with the room name, so use the tag's generic name here.
        string generic = ScummTag.TryGetMeta(block.Tag, out var tagMeta) ? tagMeta.FullName : null;
        return string.IsNullOrEmpty(generic) ? tag : $"{tag}  {generic}";
    }

    public string SizeText => FormatSize(Block.Size);

    public string ToolTip => $"{Block.FullName}\n{Block.Description}\n@0x{Block.Offset:X8}  {Block.Size:N0} bytes";

    public bool HasChildren => Block.Children.Count > 0;

    public ObservableCollection<BlockNodeViewModel> Children
    {
        get
        {
            if (_children == null)
            {
                _children = new ObservableCollection<BlockNodeViewModel>();
                var visible = Options?.VisibleBlocks;
                foreach (var c in Block.Children)
                    if (visible == null || visible.Contains(c))
                        _children.Add(new BlockNodeViewModel(c, Options, this));
            }
            return _children;
        }
    }

    public IEnumerable<BlockNodeViewModel> Ancestors()
    {
        for (var p = Parent; p != null; p = p.Parent) yield return p;
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1 << 20 => $"{bytes / (double)(1 << 20):0.0} MB",
        >= 1 << 10 => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes} B"
    };
}
