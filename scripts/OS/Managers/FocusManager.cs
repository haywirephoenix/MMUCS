using System.Collections.Generic;
using Godot;

public partial class FocusManager : Node
{
    [Export] public Control mainCanvas;
    
    private static Control _currentFocusOwner;
    public static FloatingPanel CurrentResized;
    private static List<Control> focusables = new();
    
    private static Control _contextualNext = null;
    
    public static void SetNextFocusOverride(Control target)
    {
        _contextualNext = target;
    }

    public static void Register(Control control) => focusables.Add(control);
    public static void Unregister(Control control) => focusables.Remove(control);

    public override void _Ready()
    {
        GetViewport().GuiFocusChanged += OnFocusChanged;
    }
    
    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey keyEvent && keyEvent.Pressed && !keyEvent.Echo && keyEvent.Keycode == Key.Tab)
        {
            FocusNextElement();
            GetViewport().SetInputAsHandled();
        }
    }

    private void OnFocusChanged(Control node)
    {
        _currentFocusOwner = node;
        // GD.Print(node.Name + " focused");
    }
    
    private void FocusNextElement()
    {
        if (_contextualNext != null && _contextualNext.IsVisibleInTree())
        {
            Control target = _contextualNext;
            _currentFocusOwner = _contextualNext;
            
            _contextualNext = null; 
            
            target.GrabFocus();
            return;
        }

        _contextualNext = null;

        if (focusables.Count == 0) return;

        int currentIndex = focusables.IndexOf(_currentFocusOwner);

        for (int i = 1; i <= focusables.Count; i++)
        {
            int nextIndex = (currentIndex + i) % focusables.Count;
            var target = focusables[nextIndex];

            if (target.IsVisibleInTree()) 
            {
                target.GrabFocus();
                return;
            }
        }
    }

    public static bool IsFocused(Control node) => _currentFocusOwner == node;
}