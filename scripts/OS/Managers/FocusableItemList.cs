using Godot;
public partial class FocusableItemList : ItemList
{
    public override void _EnterTree() => FocusManager.Register(this);

    public override void _ExitTree() => FocusManager.Unregister(this);
    
}