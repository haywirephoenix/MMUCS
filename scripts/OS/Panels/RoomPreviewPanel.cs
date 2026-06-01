using Godot;

/// <summary>
/// Visual room preview. Renders background strips, overlays hotspot rects,
/// object bounding boxes, and actor positions. Click to select assets.
/// </summary>
///

public partial class RoomPreviewPanel : FloatingPanel
{
    [Export] public RoomViewerContent Content;
    public override string PanelTitle => "Room Preview";
    public override bool IsClassic => true;
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
