using Godot;

[GlobalClass]
public partial class LayoutSettings : Resource
{
    [Export] public Godot.Collections.Dictionary<string, Rect2> PanelRects { get; set; } = new();
    [Export] public Godot.Collections.Dictionary<string, bool> PanelVisibility { get; set; } = new();
}