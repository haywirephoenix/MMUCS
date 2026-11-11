using System;
using Godot;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

public partial class AkosViewerContent : Control
{
    // Tab 2 – Chore / Sequence tree
    [Export] public Tree  _choreTree;
    [Export] public Label _sequenceDetail;
    
    // Chores Tab
    private static readonly NodePath PathChoreTree      = s_tabsPath + "/Chores/ChoresContainer/ChoreTree";
    private static readonly NodePath PathSequenceDetail = s_tabsPath + "/Chores/ChoresContainer/SequenceDetail";

    
    private void _ExpandAnimation(TreeItem animItem)
    {
        if (!IsInstanceValid(animItem)) return;

        var child = animItem.GetFirstChild();
        while (child != null)
        {
            var next = child.GetNext();
            child.Free();
            child = next;
        }

        int animIdx = animItem.GetMetadata(0).AsInt32();
        int dirs = _akos.DirectionCount;

        for (int d = 0; d < dirs; d++)
        {
            int choreIdx = animIdx * dirs + d;
            if (choreIdx >= _akos.ChoreOffsets.Length) continue;

            ushort seqOffset = _akos.ChoreOffsets[choreIdx];
            if (seqOffset == 0) continue;

            var dirItem = _choreTree.CreateItem(animItem);
            dirItem.SetText(0, _GetDirName(d));
            dirItem.Collapsed = true;

            var steps = _parser.DecodeSingleSequence(_akos, seqOffset);
            foreach (var step in steps)
            {
                var stepItem = _choreTree.CreateItem(dirItem);
                _FormatStepItem(stepItem, step);
            }
        }
    }
    
    private void _FormatStepItem(TreeItem item, AkosChoreStep step)
    {
        string label = step.Kind switch
        {
            AkosStepKind.DrawSingle => $"Draw cel {step.CelIndex}",
            AkosStepKind.DrawMany => $"DrawMany ({step.MultiCels.Count} cels)",
            AkosStepKind.End => "EndSeq",
            AkosStepKind.Empty => "EmptyCel",
            _ => step.OpcodeName ?? $"OP_{step.RawCode:X4}",
        };

        item.SetText(0, label);
        item.SetText(1, $"0x{step.Offset:X4}");

        if (!ChoreColors.TryGetValue(step.Kind, out Color stepColor))
            stepColor = new Color("ffcc88"); // Orange (Control Ops / Default)

        item.SetCustomColor(0, stepColor);

        if (step.Kind == AkosStepKind.DrawSingle)
            item.SetMetadata(0, step.CelIndex);
        else
            item.SetMetadata(0, -1);

    }
    
    private void _OnItemCollapsed(TreeItem item)
    {
        if (!item.Collapsed)
        {
            var root = _choreTree.GetRoot();
            // var sibling = root.GetChildren();
            // while (sibling != null)
            // {
            //     if (sibling != item) sibling.Collapsed = true;
            //     sibling = sibling.GetNext();
            // }

            if (item.GetParent() == root)
            {
                Callable.From(() => _ExpandAnimation(item)).CallDeferred();
            }
        }
    }
    
    private void _PopulateChoreTree()
    {
        _choreTree.Clear();
        if (_akos == null) return;

        var root = _choreTree.CreateItem();
        root.SetText(0, $"Costume ({_akos.AnimCount} anims)");

        // Only create the animation entries. Not decode yet.
        for (int i = 0; i < _akos.AnimCount; i++)
        {
            var animItem = _choreTree.CreateItem(root);
            animItem.SetText(0, $"Anim {i}");
            animItem.SetMetadata(0, i); // Store the AnimIndex
            animItem.Collapsed = true;

            var placeholder = _choreTree.CreateItem(animItem);
            placeholder.SetText(0, "Loading...");
        }
    }
    
    private void _OnChoreItemSelected()
    {
        var item = _choreTree.GetSelected();
        if (item == null) return;

        var meta = item.GetMetadata(0);

        // Check if this item is a Step (3rd level deep)
        // Root (0) -> Anim (1) -> Dir (2) -> Step (3)
        int depth = 0;
        var temp = item;
        while (temp.GetParent() != null)
        {
            temp = temp.GetParent();
            depth++;
        }

        if (depth == 3 && meta.VariantType == Variant.Type.Int)
        {
            int celIdx = meta.AsInt32();
            // Only jump if it's a valid cel
            if (celIdx >= 0)
            {
                _celList.Select(celIdx);
                _OnCelSelected(celIdx);
                _tabs.CurrentTab = 1;
            }
        }
    }
    
    private string _GetDirName(int d)
    {
        string[] dirNames4 =
        {
            "Right", "Left", "Front", "Back"
        };
        string[] dirNames8 =
        {
            "Right", "UpRight", "Up", "UpLeft", "Left", "DownLeft", "Down", "DownRight"
        };
        var set = _akos.Header.HasManyDirections ? dirNames8 : dirNames4;
        return d < set.Length ? set[d] : $"Dir {d}";
    }
    
    private async void _LazyLoadChores()
    {
        // prevents double-loading if user clicks the tab multiple times
        if (_akos.Chores != null) return;

        _sequenceDetail.Text = "Parsing animation data...";
        _choreTree.Visible   = false;

        try
        {
            await Task.Run(() => _parser._DecodeChores(_akos));

            _PopulateChoreTree();
            _sequenceDetail.Text = "Select an animation to view steps.";
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[AKOS] Failed to lazy-load chores: {ex.Message}");
            _sequenceDetail.Text = "Error loading animation data.";
        }
        finally
        {
            _choreTree.Visible = true;
        }
    }
    
    private static readonly Dictionary<AkosStepKind, Color> ChoreColors = new()
    {
        { AkosStepKind.DrawSingle, new Color("88ccff") }, // Light Blue
        { AkosStepKind.DrawMany, new Color("88ffcc") }, // Seafoam
        { AkosStepKind.End, new Color("ff8888") }, // Soft Red
        { AkosStepKind.Empty, new Color("888888") } // Grey
    };
}