using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MMUCS.Core.Scumm;

namespace MMUCS.Desktop.ViewModels;

/// <summary>Tree node wrapping a <see cref="ScummBlock"/>. Children are materialised lazily.</summary>
public sealed partial class BlockNodeViewModel : ObservableObject
{
    public ScummBlock Block { get; }
    public BlockNodeViewModel Parent { get; }

    [ObservableProperty] private bool _isExpanded;

    private ObservableCollection<BlockNodeViewModel> _children;

    public BlockNodeViewModel(ScummBlock block, BlockNodeViewModel parent = null)
    {
        Block = block;
        Parent = parent;
    }

    public string Tag => Block.TagName ?? ScummTag.ToString(Block.Tag);

    public string Label
    {
        get
        {
            string name = Block.FullName;
            if (string.IsNullOrEmpty(name) || name == Tag) return Tag;
            return $"{Tag}  {name}";
        }
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
                foreach (var c in Block.Children)
                    _children.Add(new BlockNodeViewModel(c, this));
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
