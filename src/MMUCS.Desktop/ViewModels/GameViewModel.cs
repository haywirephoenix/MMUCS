using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MMUCS.Core.Diagnostics;
using MMUCS.Core.Imaging;
using MMUCS.Core.Scumm;
using MMUCS.Desktop.Rendering;

namespace MMUCS.Desktop.ViewModels;

/// <summary>
/// The game window (testing ground): a 16:9 canvas with Play / Stop. While playing, a click captures
/// the mouse and the game draws its own cursor, the system-cursor-icon object (the one var290 names
/// when nothing is held) from the Icons room: state 1 normally, state 2 over a simulated hotspot. Holding
/// the button on the hotspot opens the <see cref="VerbCoin"/>, and I toggles the <see cref="Inventory"/>.
/// The game draws on a 640×480 page; that page sits in the middle of the canvas (<see cref="PageToGame"/>).
/// </summary>
public sealed partial class GameViewModel : ObservableObject
{
    /// <summary>The object image the game uses as its cursor when nothing is held (object 105).</summary>
    public const string CursorObjectName = "system-cursor-icon";

    /// <summary>The canvas in game units: the game's 480 lines high, 16:9 wide.</summary>
    public const double GameHeight = 480, GameWidth = GameHeight * 16 / 9;

    /// <summary><see cref="GameWidth"/> × <see cref="GameHeight"/>, for the canvas.</summary>
    public static Size CanvasSize { get; } = new(GameWidth, GameHeight);

    /// <summary>The 4:3 screen the game draws on (its scripts' coordinates), and where its top left sits on the canvas.</summary>
    public static Size PageSize { get; } = new(640, 480);
    public static Point PageOrigin { get; } = new((GameWidth - PageSize.Width) / 2, (GameHeight - PageSize.Height) / 2);

    /// <summary>A point on the game's 640×480 page in game units on the canvas.</summary>
    public static Point PageToGame(Point page) => new(page.X + PageOrigin.X, page.Y + PageOrigin.Y);

    private readonly ScummBlock _root;
    private readonly List<RgbaImage> _cursorStates = [];
    private readonly Dictionary<RgbaImage, Bitmap> _cursorBitmaps = [];
    private GameObjectImages _pressedItem; // an item the mouse went down on: it's picked up when the button comes up

    public GameViewModel(ScummBlock root)
    {
        _root = root;
        Inventory = new Inventory(PageToGame);
        Inventory.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not (nameof(Inventory.IsOpen) or nameof(Inventory.PageStart))) return;
            // The inventory covers the room: its hotspots stop counting, and an open coin goes.
            if (Inventory.IsOpen) Coin.Hide();
            else _pressedItem = null;
            OnPropertyChanged(nameof(IsOverHotspot));
            UpdateCursorState();
        };
        Status = root == null ? "Open the game first: the cursor comes from its Icons room." : "Loading the cursor…";
        _ = LoadCursorAsync();
        _ = LoadCoinAsync();
        _ = LoadInventoryAsync();
    }

    /// <summary>The inventory, toggled by I while playing.</summary>
    public Inventory Inventory { get; }

    /// <summary>The verb coin, shown while the mouse is held on a hotspot.</summary>
    public VerbCoin Coin { get; } = new();

    /// <summary>A stand-in hotspot, so hovering can be tried before rooms and their objects are playable.</summary>
    public Rect Hotspot { get; } = new(GameWidth / 2 - 110, GameHeight / 2 - 70, 220, 140);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Hint))]
    [NotifyCanExecuteChangedFor(nameof(PlayCommand), nameof(StopCommand))]
    public partial bool IsPlaying { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Hint))]
    public partial bool IsMouseCaptured { get; set; }

    /// <summary>The inventory item the cursor is (null for the system cursor), picked up with a click in the inventory.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CursorFrame), nameof(CursorImage), nameof(CursorHotspot))]
    public partial GameObjectImages HeldItem { get; set; }

    /// <summary>The cursor's state: 1 normally, 2 over a hotspot, or over a page arrow or another item in the inventory.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CursorFrame), nameof(CursorImage), nameof(CursorHotspot))]
    public partial int CursorState { get; set; } = 1;

    /// <summary>The game cursor's position in game units, on the canvas.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOverHotspot))]
    public partial Point CursorPosition { get; set; } = new(GameWidth / 2, GameHeight / 2);

    [ObservableProperty] public partial string Status { get; set; }

    /// <summary>Whether the cursor is on the hotspot (never while the inventory covers the room).</summary>
    public bool IsOverHotspot => !Inventory.IsOpen && Hotspot.Contains(CursorPosition);

    /// <summary>How many states (images) the cursor object has; 0 before it loads.</summary>
    public int CursorStateCount => _cursorStates.Count;

    /// <summary>The cursor's image for the current state: the held item's, or system-cursor-icon's (null before it loads).</summary>
    public RgbaImage CursorFrame => HeldItem is { } item ? item.State(CursorState) ?? item.State(1)
        : _cursorStates.Count == 0 ? null : _cursorStates[Math.Clamp(CursorState, 1, _cursorStates.Count) - 1];

    /// <summary><see cref="CursorFrame"/> as a bitmap for the canvas (made on first use, then kept).</summary>
    public Bitmap CursorImage
    {
        get
        {
            if (CursorFrame is not { } frame) return null;
            if (!_cursorBitmaps.TryGetValue(frame, out var bitmap)) _cursorBitmaps[frame] = bitmap = frame.ToBitmap();
            return bitmap;
        }
    }

    /// <summary>Where the cursor image is held, from its top left: its centre (a stand-in until the scripts' hotspot is read).</summary>
    public Point CursorHotspot => CursorFrame is { } i ? new Point(i.Width / 2.0, i.Height / 2.0) : default;

    /// <summary>The line drawn over the canvas: what to do next.</summary>
    public string Hint => !IsPlaying ? "Press Play to start."
        : IsMouseCaptured ? null
        : "Click the canvas to capture the mouse (Esc lets it go). I or a right-click opens the inventory.";

    [RelayCommand(CanExecute = nameof(CanPlay))]
    private void Play() => IsPlaying = true;
    private bool CanPlay() => !IsPlaying;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        IsPlaying = false;
        IsMouseCaptured = false;
        Inventory.IsOpen = false;
        HeldItem = null;
        CursorPosition = new Point(GameWidth / 2, GameHeight / 2);
    }
    private bool CanStop() => IsPlaying;

    /// <summary>Moves the game cursor (game units, clamped to the canvas) and picks its state from what's under it.</summary>
    public void MoveCursor(Point position)
    {
        CursorPosition = new Point(Math.Clamp(position.X, 0, GameWidth), Math.Clamp(position.Y, 0, GameHeight));
        // Holding an item, the cursor leaving the inventory's area closes it (global 50).
        if (Inventory.IsOpen && HeldItem != null && !Inventory.InArea(CursorOnPage)) Inventory.IsOpen = false;
        Coin.PointAt(CursorPosition);
        UpdateCursorState();
    }

    /// <summary>The cursor on the game's 640×480 page.</summary>
    public Point CursorOnPage => new(CursorPosition.X - PageOrigin.X, CursorPosition.Y - PageOrigin.Y);

    /// <summary>What's under the cursor in the open inventory.</summary>
    public InventoryHit InventoryHit => Inventory.IsOpen ? Inventory.HitTest(CursorOnPage) : new(InventoryHitKind.Outside);

    /// <summary>
    /// State 2 while the mouse is captured and the cursor is over a hotspot, or in the inventory over a page
    /// arrow or an item other than the one held (global 39); never while the verb coin is open.
    /// </summary>
    private void UpdateCursorState()
    {
        var hit = InventoryHit;
        bool target = IsOverHotspot
            || hit.Kind is InventoryHitKind.NextPage or InventoryHitKind.PreviousPage
            || hit.Kind == InventoryHitKind.Item && !ReferenceEquals(hit.Item, HeldItem);
        CursorState = IsMouseCaptured && target && !Coin.IsShown ? 2 : 1;
    }

    /// <summary>
    /// The mouse went down. In the inventory (global 34): outside its area closes it, an arrow turns the page,
    /// an item is picked up when the button comes up, empty space puts a held item back. In the room: on a
    /// hotspot, the verb coin opens where the cursor is (not while an item is held: that would use it there).
    /// </summary>
    public void PressCursor()
    {
        if (!IsMouseCaptured) return;
        if (Inventory.IsOpen)
        {
            var hit = InventoryHit;
            switch (hit.Kind)
            {
                case InventoryHitKind.Outside: Inventory.IsOpen = false; break;
                case InventoryHitKind.NextPage: Inventory.NextPage(); break;
                case InventoryHitKind.PreviousPage: Inventory.PreviousPage(); break;
                case InventoryHitKind.Empty: HeldItem = null; break;
                // With an item held, clicking another would use one with the other: not played yet.
                case InventoryHitKind.Item when HeldItem == null: _pressedItem = hit.Item; break;
            }
        }
        else if (HeldItem == null && IsOverHotspot) Coin.Show(CursorPosition, CanvasSize);
        UpdateCursorState();
    }

    /// <summary>The mouse came up: the verb coin goes, and an item pressed in the inventory becomes the cursor (global 35).</summary>
    public void ReleaseCursor()
    {
        Coin.Hide();
        if (_pressedItem != null && Inventory.IsOpen) HeldItem = _pressedItem;
        _pressedItem = null;
        UpdateCursorState();
    }

    /// <summary>A right-click (global 33): puts a held item back, or else opens or shuts the inventory.</summary>
    public void RightClick()
    {
        if (!IsMouseCaptured) return;
        if (HeldItem != null) HeldItem = null;
        else Inventory.Toggle();
        UpdateCursorState();
    }

    /// <summary>The I key (handle-key, global 38): puts a held item back and opens or shuts the inventory, while playing.</summary>
    public void ToggleInventory()
    {
        if (!IsPlaying) return;
        HeldItem = null;
        Inventory.Toggle();
        UpdateCursorState();
    }

    /// <summary>Steps the game's animations (the window calls it <see cref="VerbCoin.FramesPerSecond"/> times a second).</summary>
    public void Tick() => Coin.Tick();

    partial void OnIsMouseCapturedChanged(bool value)
    {
        if (value) return;
        CursorState = 1;
        _pressedItem = null;
        Coin.Hide();
    }

    private async Task LoadCoinAsync()
    {
        try
        {
            if (!await Coin.LoadAsync(_root) && _root != null) Log.Warn($"Game window: no costume {VerbCoin.CostumeNumber} in room {VerbCoin.RoomNumber} for the verb coin");
        }
        catch (Exception ex) { Log.Warn($"Game window verb coin: {ex.Message}"); }
    }

    private async Task LoadInventoryAsync()
    {
        try
        {
            if (!await Inventory.LoadAsync(_root) && _root != null) Log.Warn($"Game window: no {Inventory.BackgroundName} for the inventory");
        }
        catch (Exception ex) { Log.Warn($"Game window inventory: {ex.Message}"); }
    }

    private async Task LoadCursorAsync()
    {
        if (_root == null) return;
        try
        {
            if (await GameObjects.LoadAsync(_root, CursorObjectName) is not { } cursor)
            {
                Status = $"No {CursorObjectName} object image in this game.";
                return;
            }
            var (room, obj) = (cursor.Room, cursor.Object);
            _cursorStates.AddRange(cursor.States);
            OnPropertyChanged(nameof(CursorStateCount));
            OnPropertyChanged(nameof(CursorFrame));
            OnPropertyChanged(nameof(CursorImage));
            OnPropertyChanged(nameof(CursorHotspot));
            Status = $"{CursorObjectName} from room {room.RoomNo} ({room.Name}): {_cursorStates.Count} state{(_cursorStates.Count == 1 ? "" : "s")}, "
                     + $"{obj.Width}×{obj.Height}";
        }
        catch (Exception ex)
        {
            Log.Warn($"Game window cursor: {ex.Message}");
            Status = $"The cursor didn't load: {ex.Message}";
        }
    }
}
