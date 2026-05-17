using Godot;

public static class Consts
{
    public enum WallPaperModeEnum { Image, Color }
    
    public const int TREE_PNL_ID = 0;
    public const int HEX_PNL_ID =  1;
    public const int ROOM_PNL_ID = 2;
    public const int META_PNL_ID = 3;
    public const int AKOS_PNL_ID = 4;
    public const int OPTS_PNL_ID = 5;
    
    public const int MAIN_PANEL_COUNT = 5;
    public const int MAX_PANEL_COUNT = 6;
    
    public const string WALL_DIR = "res://wallpapers/";
    public const string WALL_DIR_BLURRED = "res://wallpapers/blurred/";
    
    public static Font GetRobotoFont()
    {
        return RobotoFont;
    }
    
    private static readonly Font RobotoFont = GD.Load<Font>("res://fonts/Roboto/Roboto-Regular.ttf");
    
    
}