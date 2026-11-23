using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MMUCS.Core.Diagnostics;
using MMUCS.Core.Imaging;
using MMUCS.Core.Scumm;

namespace MMUCS.Runtime;

/// <summary>
/// The game as it runs, for now a testing ground: a 16:9 canvas the game's 640×480 page sits in the middle
/// of (<see cref="PageToGame"/>), the game's own cursor (system-cursor-icon, the object var290 names when
/// nothing is held: state 1 normally, state 2 over a hotspot), a stand-in hotspot whose held press opens
/// the <see cref="VerbCoin"/>, and the <see cref="Inventory"/>. Input comes in through the methods, time
/// through <see cref="Tick"/> (one jiffy, 1/60 s, as the engine counts); <see cref="Changed"/> says when to
/// look again. Positions are in game units on the canvas unless they say page.
/// </summary>
public sealed class GameState
{
    /// <summary>The object image the game uses as its cursor when nothing is held (object 105).</summary>
    public const string CursorObjectName = "system-cursor-icon";

    /// <summary>The canvas in game units: the game's 480 lines high, 16:9 wide.</summary>
    public const double CanvasHeight = 480, CanvasWidth = CanvasHeight * 16 / 9;
    public static readonly GameSize CanvasSize = new(CanvasWidth, CanvasHeight);

    /// <summary>The 4:3 screen the game draws on (its scripts' coordinates), and where its top left sits on the canvas.</summary>
    public static readonly GameSize PageSize = new(640, 480);
    public static readonly GamePoint PageOrigin = new((CanvasWidth - PageSize.Width) / 2, (CanvasHeight - PageSize.Height) / 2);

    /// <summary>A point on the game's 640×480 page in game units on the canvas.</summary>
    public static GamePoint PageToGame(GamePoint page) => page + PageOrigin;

    /// <summary>How long the button must stay down on an inventory item before its verb coin opens: var304, 13 jiffies (global 1:29, 34).</summary>
    public const int ItemHoldJiffies = 13;

    /// <summary>Where the hover label goes on the 640×480 page, and how it's drawn: centred, wrapped, charset 1, colour 19 (global 39).</summary>
    public static readonly GamePoint LabelAt = new(320, 400);
    public const int LabelCharset = 1, LabelColor = 19;

    private readonly List<RgbaImage> _cursorStates = [];
    private GameObjectImages _pressedItem; // an item the mouse went down on: picked up when the button comes up
    private int _heldJiffies; // how long it's been down on it

    public GameState()
    {
        Inventory = new Inventory(PageToGame);
    }

    /// <summary>Something that's shown changed (call it after loading, input or a tick that changed anything).</summary>
    public event Action Changed;

    public Inventory Inventory { get; }

    /// <summary>The verb coin, shown while the mouse is held on a hotspot or an inventory item.</summary>
    public VerbCoin Coin { get; } = new();

    /// <summary>A stand-in hotspot, so hovering can be tried before rooms and their objects are playable.</summary>
    public GameRect Hotspot { get; } = new(CanvasWidth / 2 - 110, CanvasHeight / 2 - 70, 220, 140);

    public bool IsPlaying { get; private set; }

    /// <summary>Whether the game has the mouse (its cursor shows and input counts); the window captures it.</summary>
    public bool IsMouseCaptured { get; private set; }

    /// <summary>The inventory item the cursor is (null for the system cursor), picked up with a click in the inventory.</summary>
    public GameObjectImages HeldItem { get; private set; }

    /// <summary>The cursor's state: 1 normally, 2 over a hotspot, or over a page arrow or another item in the inventory.</summary>
    public int CursorState { get; private set; } = 1;

    /// <summary>The game cursor's position on the canvas.</summary>
    public GamePoint CursorPosition { get; private set; } = new(CanvasWidth / 2, CanvasHeight / 2);

    /// <summary>The cursor on the game's 640×480 page.</summary>
    public GamePoint CursorOnPage => CursorPosition - PageOrigin;

    /// <summary>What loaded, or what didn't.</summary>
    public string Status { get; private set; } = "Open the game first: the cursor comes from its Icons room.";

    /// <summary>
    /// The line under the inventory naming what the cursor is over (global 39, with object labels on): an
    /// item's name, or "use X with Y" / "give X to Y" while an item is held. Null when there's nothing to say.
    /// </summary>
    public string Label { get; private set; }

    /// <summary>The palette labels are coloured from (256 RGB triplets): the icons room's, until rooms are played.</summary>
    public byte[] LabelPalette { get; private set; }

    /// <summary>Whether the cursor is on the hotspot (never while the inventory covers the room).</summary>
    public bool IsOverHotspot => !Inventory.IsOpen && Hotspot.Contains(CursorPosition);

    /// <summary>How many states (images) the system cursor has; 0 before it loads.</summary>
    public int CursorStateCount => _cursorStates.Count;

    /// <summary>The cursor's image for the current state: the held item's, or system-cursor-icon's (null before it loads).</summary>
    public RgbaImage CursorFrame => HeldItem is { } item ? item.State(CursorState) ?? item.State(1)
        : _cursorStates.Count == 0 ? null : _cursorStates[Math.Clamp(CursorState, 1, _cursorStates.Count) - 1];

    /// <summary>Where the cursor image is held, from its top left: its centre (a stand-in until the scripts' hotspot is read).</summary>
    public GamePoint CursorHotspot => CursorFrame is { } i ? new GamePoint(i.Width / 2.0, i.Height / 2.0) : default;

    /// <summary>What's under the cursor in the open inventory.</summary>
    public InventoryHit InventoryHit => Inventory.IsOpen ? Inventory.HitTest(CursorOnPage) : new(InventoryHitKind.Outside);

    /// <summary>Loads the cursor, the verb coin and the inventory from the game.</summary>
    public async Task LoadAsync(ScummBlock root)
    {
        if (root == null) return;
        Status = "Loading the cursor…";
        Changed?.Invoke();
        await Load("cursor", async () =>
        {
            if (await GameObjects.LoadAsync(root, CursorObjectName) is not { } cursor)
            {
                Status = $"No {CursorObjectName} object image in this game.";
                return;
            }
            _cursorStates.AddRange(cursor.States);
            Status = $"{CursorObjectName} from room {cursor.Room.RoomNo} ({cursor.Room.Name}): {_cursorStates.Count} state{(_cursorStates.Count == 1 ? "" : "s")}, "
                     + $"{cursor.Object.Width}×{cursor.Object.Height}";
        });
        await Load("verb coin", async () =>
        {
            if (!await Coin.LoadAsync(root)) Log.Warn($"Game: no costume {VerbCoin.CostumeNumber} in room {VerbCoin.RoomNumber} for the verb coin");
        });
        await Load("inventory", async () =>
        {
            if (!await Inventory.LoadAsync(root)) Log.Warn($"Game: no {Inventory.BackgroundName} for the inventory");
            LabelPalette = ObimCache.ExtractPaletteContext(Inventory.Background?.Room.Room, 0).rawBytes;
        });
    }

    private async Task Load(string what, Func<Task> load)
    {
        try { await load(); }
        catch (Exception ex)
        {
            Log.Warn($"Game {what}: {ex.Message}");
            if (what == "cursor") Status = $"The cursor didn't load: {ex.Message}";
        }
        Changed?.Invoke();
    }

    public void Play()
    {
        if (IsPlaying) return;
        IsPlaying = true;
        Changed?.Invoke();
    }

    /// <summary>Stops the game: the mouse goes, the inventory shuts, a held item is put back and the cursor recentres.</summary>
    public void Stop()
    {
        IsPlaying = false;
        SetMouseCaptured(false);
        Inventory.IsOpen = false;
        HeldItem = null;
        CursorPosition = new GamePoint(CanvasWidth / 2, CanvasHeight / 2);
        Update();
    }

    /// <summary>The window captured the mouse (only while playing) or let it go: letting go drops a press and the coin.</summary>
    public void SetMouseCaptured(bool captured)
    {
        captured &= IsPlaying;
        if (captured == IsMouseCaptured) return;
        IsMouseCaptured = captured;
        if (!captured)
        {
            _pressedItem = null;
            Coin.Hide();
        }
        Update();
    }

    /// <summary>Moves the game cursor (clamped to the canvas) and picks its state from what's under it.</summary>
    public void MoveCursor(GamePoint position)
    {
        CursorPosition = new GamePoint(Math.Clamp(position.X, 0, CanvasWidth), Math.Clamp(position.Y, 0, CanvasHeight));
        // Holding an item, the cursor leaving the inventory's area closes it (global 50).
        if (Inventory.IsOpen && HeldItem != null && !Inventory.InArea(CursorOnPage)) SetInventoryOpen(false);
        Coin.PointAt(CursorPosition);
        Update();
    }

    /// <summary>
    /// The mouse went down. In the inventory (global 34): outside its area closes it, an arrow turns the page,
    /// an item is picked up when the button comes up (or its verb coin opens if it's still down after
    /// <see cref="ItemHoldJiffies"/>), empty space puts a held item back. In the room: on a hotspot, the verb
    /// coin opens where the cursor is (not while an item is held: that would use it there).
    /// </summary>
    public void PressCursor()
    {
        if (!IsMouseCaptured) return;
        if (Inventory.IsOpen)
        {
            var hit = InventoryHit;
            switch (hit.Kind)
            {
                case InventoryHitKind.Outside: SetInventoryOpen(false); break;
                case InventoryHitKind.NextPage: Inventory.NextPage(); break;
                case InventoryHitKind.PreviousPage: Inventory.PreviousPage(); break;
                case InventoryHitKind.Empty: HeldItem = null; break;
                // With an item held, clicking another would use one with the other: not played yet.
                case InventoryHitKind.Item when HeldItem == null:
                    _pressedItem = hit.Item;
                    _heldJiffies = 0;
                    break;
            }
        }
        else if (HeldItem == null && IsOverHotspot) Coin.Show(CursorPosition, CanvasSize);
        Update();
    }

    /// <summary>The mouse came up: the verb coin goes, and an item pressed in the inventory becomes the cursor (global 35).</summary>
    public void ReleaseCursor()
    {
        Coin.Hide();
        if (_pressedItem != null && Inventory.IsOpen) HeldItem = _pressedItem;
        _pressedItem = null;
        Update();
    }

    /// <summary>A right-click (global 33): puts a held item back, or else opens or shuts the inventory.</summary>
    public void RightClick()
    {
        if (!IsMouseCaptured) return;
        if (HeldItem != null) HeldItem = null;
        else SetInventoryOpen(!Inventory.IsOpen);
        Update();
    }

    /// <summary>The I key (handle-key, global 38): puts a held item back and opens or shuts the inventory, while playing.</summary>
    public void ToggleInventory()
    {
        if (!IsPlaying) return;
        HeldItem = null;
        SetInventoryOpen(!Inventory.IsOpen);
        Update();
    }

    /// <summary>One jiffy (1/60 s) passes: the coin animates, and a press held on an inventory item opens its verb coin.</summary>
    public void Tick()
    {
        Coin.Tick();
        if (_pressedItem != null && ++_heldJiffies >= ItemHoldJiffies) OpenItemCoin(_pressedItem);
        Changed?.Invoke();
    }

    /// <summary>
    /// Still down on the item: its verb coin opens (global 34 → 43) with the coin's origin at the item's slot
    /// + (38, −30) (global 66), kept within 60–580 × 100–410 of the page, and the item isn't picked up.
    /// </summary>
    private void OpenItemCoin(GameObjectImages item)
    {
        _pressedItem = null;
        if (!Inventory.IsOpen || !IsMouseCaptured) return;
        int slot = Inventory.PageItems.FirstOrDefault(p => ReferenceEquals(p.Item, item)).Slot;
        var at = Inventory.ItemAt(slot);
        Coin.ShowAtOrigin(PageToGame(new GamePoint(Math.Clamp(at.X + 38, 60, 580), Math.Clamp(at.Y - 30, 100, 410))));
        Update();
    }

    /// <summary>Opens or shuts the inventory (if it loaded). It covers the room: an open coin goes, and so does a press on an item when it shuts.</summary>
    private void SetInventoryOpen(bool open)
    {
        if (open && !Inventory.IsLoaded) return;
        Inventory.IsOpen = open;
        if (open) Coin.Hide();
        else _pressedItem = null;
    }

    /// <summary>
    /// Works out what follows from where the cursor is, then says so. The cursor shows state 2 while the mouse is
    /// captured and it's over a hotspot, or in the inventory over a page arrow or an item other than the one
    /// held (global 39); never while the verb coin is open. The label names the item under it.
    /// </summary>
    private void Update()
    {
        var hit = InventoryHit;
        bool overItem = hit.Kind == InventoryHitKind.Item && !ReferenceEquals(hit.Item, HeldItem);
        bool target = IsOverHotspot || hit.Kind is InventoryHitKind.NextPage or InventoryHitKind.PreviousPage || overItem;
        CursorState = IsMouseCaptured && target && !Coin.IsShown ? 2 : 1;
        Label = IsMouseCaptured && !Coin.IsShown && overItem
            ? HeldItem == null ? Inventory.NameOf(hit.Item)
            : Inventory.IsGivenTo(hit.Item) ? $"give {Inventory.NameOf(HeldItem)} to {Inventory.NameOf(hit.Item)}"
            : $"use {Inventory.NameOf(HeldItem)} with {Inventory.NameOf(hit.Item)}"
            : null;
        Changed?.Invoke();
    }
}
