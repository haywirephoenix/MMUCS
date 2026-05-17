using Godot;


public static class LayoutUtils
{
    public static void ExpandAndFillHV(this Control control)
    {
        control.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        control.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
    }
    
    public static Vector2 SnapVector(this Vector2 value, int GridSize = 16)
    {
        return new Vector2(
            Mathf.Round(value.X / GridSize) * GridSize,
            Mathf.Round(value.Y / GridSize) * GridSize
        );
    }
}