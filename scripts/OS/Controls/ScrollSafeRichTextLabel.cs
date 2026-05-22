using Godot;
using System;

public partial class ScrollSafeRichTextLabel : RichTextLabel
{
    public override void _Ready()
    {
        // Ensure standard mouse selection properties are checked
        SelectionEnabled = true;
    }

    public override void _GuiInput(InputEvent @event)
    {
        // Catch mouse wheel scrolling before the internal control logic handles it
        if (@event is InputEventMouseButton mouseButtonEvent)
        {
            if (mouseButtonEvent.ButtonIndex == MouseButton.WheelUp || 
                mouseButtonEvent.ButtonIndex == MouseButton.WheelDown)
            {
                // If text is currently highlighted, we manually scroll and eat the input
                if (GetSelectionFrom() != GetSelectionTo())
                {
                    ScrollBar vScroll = GetVScrollBar();
                    if (vScroll != null)
                    {
                        // Calculate scroll step based on direction
                        double scrollAmount = vScroll.Page / 4.0; // Adjust speed multiplier here
                        if (mouseButtonEvent.ButtonIndex == MouseButton.WheelUp)
                        {
                            vScroll.Value -= scrollAmount;
                        }
                        else
                        {
                            vScroll.Value += scrollAmount;
                        }

                        // Accept the event so the RichTextLabel logic never deselects the text
                        AcceptEvent();
                    }
                }
            }
        }
    }
}