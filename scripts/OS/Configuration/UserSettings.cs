// using Godot;
//
// [GlobalClass]
// public partial class UserSettings : Resource
// {
//     // --- Shell Settings (The Godot Window) ---
//     // -- Persist shell window state
//     [Export] public Window.ModeEnum ShellMode { get; set; } = Window.ModeEnum.Windowed;
//     [Export] public Vector2I ShellPosition { get; set; } = new(-1, -1);
//     [Export] public Vector2I ShellSize { get; set; } = new(-1, -1);
//     [Export] public int ShellScreenCount { get; set; } = -1;
//     
//     // -- Godot Settings
//     [Export] public float GuiScale { get; set; } = 1.0f;
//     
//     // --- Theme Settings ---
//     [Export] public Consts.WallPaperModeEnum WallpaperMode { get; set; } = Consts.WallPaperModeEnum.Image;
//     [Export] public int WallpaperIndex { get; set; } = 0;
//     [Export] public string WallpaperName { get; set; } = "fractal1";
//     [Export] public string WallpaperBlurName { get; set; } = "fractal1_blurred";
//     [Export] public Color WallpaperColor { get; set; } = Colors.SlateGray;
//     [Export] public Color GlassTintColor { get; set; } = new(1.0f, 1.0f, 1.0f, 0.027f);
//     [Export] public bool GlassEnabled { get; set; } = true;
//     [Export] public Font MainFont { get; set; }
//     [Export] public bool WindowAnimations { get; set; } = true;
//     [Export] public bool HiDPIEnabled { get; set; } = true;
//     [Export] public bool AutoLoadLastFile { get; set; } = true;
//     [Export] public string LastFilePath { get; set; } = "";
//
// }