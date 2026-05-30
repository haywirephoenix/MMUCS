using System;
using Godot;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;


public partial class AkosViewerPanel : FloatingPanel
{
    public override string PanelTitle => "AKOS Viewer";
    public override bool IsClassic => true;
    
    [Export] public AkosViewerContent Content;
    protected override void OnReady() => Content.OnReady();
    protected override void OnExit() => Content.OnExit();
    protected override void _OnBlockSelected(ScummBlock block) 
        => Content.OnBlockSelected(block);
    
    public override void AssignNodes()
    {
        base.AssignNodes();
        Content.AssignNodes();
    }


    
}