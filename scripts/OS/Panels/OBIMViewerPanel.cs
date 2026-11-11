using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;

public partial class OBIMViewerPanel : FloatingPanel
{
    [Export] public OBIMViewerContent Content;
    public override string PanelTitle => "OBIM Viewer";
    public override bool IsClassic => true;
    protected override void OnReady() => Content.OnReady();
    protected override void OnExit() => Content.OnExit();
    protected override void _OnBlockSelected(ScummBlock block) => Content.OnBlockSelected(block);
    public override void AssignNodes()
    {
        base.AssignNodes();
        Content.ParentPanel = this;
        Content.AssignNodes();
    }
}