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
    
    public static Vector2 ClampToCanvas(this Vector2 localPosition, Vector2 size, Control canvas)
    {
        if (canvas == null) return localPosition;

        Rect2 canvasRect = canvas.GetRect();
        
        float minX = 0f;
        float maxX = canvasRect.Size.X - size.X;
        float minY = 0f;
        float maxY = canvasRect.Size.Y - size.Y;

        if (size.X > canvasRect.Size.X) maxX = minX;
        if (size.Y > canvasRect.Size.Y) maxY = minY;

        localPosition.X = Mathf.Clamp(localPosition.X, minX, maxX);
        localPosition.Y = Mathf.Clamp(localPosition.Y, minY, maxY);

        return localPosition;
    }
}