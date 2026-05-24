public static class SettingKeys
{
    public const string UserDir = "user://";
    public const string SettingsPath = "user://settings.cfg";
    public const string LayoutPath = "user://layout.cfg";
    public static class Shell
    {
        public const string Section = "Shell";
        public const string Mode = "mode";
        public const string Position = "position";
        public const string Size = "size";
        public const string ScreenCount = "screen_count";
    }

    public static class App
    {
        public const string Section = "App";
        public const string GuiScale = "gui_scale";
        public const string WindowAnimations = "window_animations";
        public const string HiDpi = "hidpi";
        public const string AutoLoad = "auto_load";
        public const string LastFile = "last_file";
        public const string FirstRun = "first_run";
    }

    public static class Theme
    {
        public const string Section = "Theme";
        public const string WallpaperMode = "wallpaper_mode";
        public const string WallpaperIndex = "wallpaper_index";
        public const string WallpaperPath = "wallpaper_path";
        public const string WallpaperBlur = "wallpaper_blur";
        public const string WallpaperColor = "wallpaper_color";
        public const string GlassTint = "glass_tint";
        public const string GlassEnabled = "glass_enabled";
        public const string FontPath = "font_path";
    }
    
    public static class Layout
    {
        public const string X = "x";
        public const string Y = "y";
        public const string Width = "w";
        public const string Height = "h";
        public const string IsOpen = "open";
    }
}