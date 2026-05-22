using Godot;

public partial class ShellManager : Node
{
    private static ShellManager Instance;
    public override void _EnterTree() => Instance = this;
    private static Window GetShellWindow() => Instance.GetWindow();
    private static readonly Vector2I MinWindowSize = new Vector2I(320, 240);
    public static void MoveToCenter() => GetShellWindow()?.MoveToCenter();
    public static void SetWindowMode(Window.ModeEnum mode) => GetShellWindow().Mode = mode;
    public static void SetAlwaysOnTop(bool onTop) => GetShellWindow().AlwaysOnTop = onTop;
    public static void SetBorderless(bool borderless) => GetShellWindow().Borderless = borderless;

    public static void MoveTo(Vector2I position)
    {
        var window = GetShellWindow();
        if (window == null) return;
        int currentScreen = DisplayServer.WindowGetCurrentScreen();
        Vector2I screenPos = DisplayServer.ScreenGetPosition(currentScreen);
        Vector2I screenSize = DisplayServer.ScreenGetSize(currentScreen);
        int clampedX = Mathf.Clamp(position.X, screenPos.X, screenPos.X + screenSize.X - 100);
        int clampedY = Mathf.Clamp(position.Y, screenPos.Y, screenPos.Y + screenSize.Y - 40);

        window.Position = new Vector2I(clampedX, clampedY);
    }

    public static void Resize(Vector2I size)
    {
        var window = GetShellWindow();
        if (window == null) return;

        int currentScreen = DisplayServer.WindowGetCurrentScreen();
        Vector2I screenSize = DisplayServer.ScreenGetSize(currentScreen);
        int clampedX = Mathf.Clamp(size.X, MinWindowSize.X, screenSize.X);
        int clampedY = Mathf.Clamp(size.Y, MinWindowSize.Y, screenSize.Y);

        window.Size = new Vector2I(clampedX, clampedY);
    }
    public static Godot.Collections.Dictionary<string, Variant> GetWindowState()
    {
        var window = GetShellWindow();
        if (window == null) return new();

        return new Godot.Collections.Dictionary<string, Variant>
        {
            { "position", window.Position },
            { "size", window.Size },
            { "mode", (int)window.Mode }
        };
    }
    
    public static void RestoreWindowState(Godot.Collections.Dictionary<string, Variant> state)
    {
        if (state.TryGetValue("mode", out var mode)) SetWindowMode((Window.ModeEnum)(int)mode);
        if (state.TryGetValue("size", out var size)) Resize((Vector2I)size);
        if (state.TryGetValue("position", out var position)) MoveTo((Vector2I)position);
    }
    
}