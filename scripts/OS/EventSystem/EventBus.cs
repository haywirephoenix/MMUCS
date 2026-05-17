using Godot;

public partial class EventBus : Node
{
    public static EventBus Instance { get; private set; }

    // --- Block selection (Tree -> Hex + Metadata) ---
    [Signal] public delegate void BlockSelectedEventHandler(ScummBlock block);

    // --- Hex range selected (Hex -> Tree) ---
    [Signal] public delegate void HexOffsetSelectedEventHandler(int offset, int length);

    // --- Room loaded (any source -> RoomPreview + Tree) ---
    [Signal] public delegate void RoomLoadedEventHandler(string roomPath);

    // --- Asset selected in room preview (Preview -> Metadata) ---
    [Signal] public delegate void AssetSelectedEventHandler(string assetId, string assetType);

    // --- Panel focus request ---
    [Signal] public delegate void PanelFocusRequestedEventHandler(string panelId);
    
    [Signal] public delegate void FileSelectedEventHandler();
    
    [Signal] public delegate void FileParsedEventHandler(ScummBlock root);
    
    // --- Settings: Requests (UI → ThemeManager) ---
    [Signal] public delegate void WallpaperChangeRequestedEventHandler(string path);
    [Signal] public delegate void WallpaperModeChangeRequestedEventHandler(int mode); // cast to enum in handler
    [Signal] public delegate void WallpaperColorChangeRequestedEventHandler(Color color);
    [Signal] public delegate void GlassChangeRequestedEventHandler(bool enabled);
    [Signal] public delegate void GlassChangeSystemRequestedEventHandler(bool enabled);
    [Signal] public delegate void WindowAnimationsChangeRequestedEventHandler(bool enabled);
    [Signal] public delegate void UIScaleChangeRequestedEventHandler(float scale);
    [Signal] public delegate void HiDPIChangeRequestedEventHandler(bool enabled);

    // --- Settings: Confirmations (ThemeManager → UI) ---
    // Only emit these when the applied state differs from what the UI assumed.
    // OptionsPanel uses these to sync controls without causing feedback loops.
    [Signal] public delegate void GlassStateChangedEventHandler(bool enabled);
    [Signal] public delegate void WallpaperModeAppliedEventHandler(int mode);
    
    public override void _Ready()
    {
        Instance = this;
    }
}
