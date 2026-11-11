using Godot;

public partial class ExporterPanel : FloatingPanel
{
    public override string PanelTitle => "Export";
    [Export] public ExporterContent Content;
    
    protected override void OnReady() => Content.OnReady();
    protected override void OnExit()  => Content.OnExit();
    
    public override void AssignNodes()
    {
        base.AssignNodes();
        Content.ParentPanel = this;
        Content.AssignNodes();
    }
    
    
}
