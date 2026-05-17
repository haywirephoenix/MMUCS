using System.Collections.Generic;
using Godot;


public partial class WindowManager : Node
{
    public readonly struct PanelLayout
    {
        public readonly Vector2 Position;
        public readonly Vector2 Size;
        public readonly bool Visible;

        public PanelLayout(Vector2 position, Vector2 size, bool visible)
        {
            Position = position;
            Size = size;
            Visible = visible;
        }
    }
    
    private static WindowManager Instance;
    
    
    private static readonly List<FloatingPanel> _registeredPanels = new();

    public static void RegisterPanel(FloatingPanel panel)
    {
        _registeredPanels.Add(panel);
    
        if (!ConfigManager.TryLoadPanelLayout(panel.PanelId, out PanelLayout layout)) return;

        // var pos = ClampToCanvas(layout.Position, layout.Size, panel.GetParent<Control>());
        panel.CallDeferred(Control.MethodName.SetPosition, layout.Position);
        panel.CallDeferred(Control.MethodName.SetSize, layout.Size);
        panel.Visible = layout.Visible;
    }

    public static void SaveLayout() => ConfigManager.SavePanelLayouts(_registeredPanels);

    private static Vector2 ClampToCanvas(Vector2 pos, Vector2 size, Control canvas)
    {
        if (canvas == null) return pos;
    
        var canvasSize = canvas.Size;
    
        pos.X = Mathf.Clamp(pos.X, 0, Mathf.Max(0, canvasSize.X - size.X));
        pos.Y = Mathf.Clamp(pos.Y, 0, Mathf.Max(0, canvasSize.Y - size.Y));
    
        return pos;
    }
    
    public static void UnregisterPanel(FloatingPanel panel)
    {
        _registeredPanels.Remove(panel);
    }
    

    
}