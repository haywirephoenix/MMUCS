using Godot;
public partial class GlobalInput : Node
{
    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true } key)
        {   
            /* //used for debugging
           if (key.Keycode == Key.Period)
           {
               ScummDecoders.LastStripOverride++;
               StatusBar.SetStatus($"Override offset to {ScummDecoders.LastStripOverride}");
               OnRoomSelected(force:true);
           }
           else if (key.Keycode == Key.Comma)
           {
                   ScummDecoders.LastStripOverride--;
                   StatusBar.SetStatus($"Override offset to {ScummDecoders.LastStripOverride}");
               OnRoomSelected(force:true);
           }
           */
        }
    }
}