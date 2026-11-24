using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MMUCS.Core.Scumm;

namespace MMUCS.Runtime;

/// <summary>What's under a point of the open inventory.</summary>
public enum InventoryHitKind { Outside, Empty, NextPage, PreviousPage, Item }

/// <summary>What's under a point of the open inventory: its kind, and the item for <see cref="InventoryHitKind.Item"/>.</summary>
public readonly record struct InventoryHit(InventoryHitKind Kind, GameObjectImages Item = null);

/// <summary>
/// The inventory as the game draws it. The I key toggles it as handle-key (global
/// 38) does: open-inventory (46) when it's shut, close-inventory (51) when it's open. While it's open,
/// draw-inventory (47) super-blasts inventory-bg-object (114, room 3) at 0,0, the page arrows (116 at
/// 155,120 and 115 at 400,120) when there are pages either side, and up to 15 item icons in a 5×3 grid.
/// Those are positions on the game's 640×480 screen; <see cref="GameState.PageToGame"/> puts that
/// page in the middle of the 16:9 canvas. There's no ego to hold things yet, so the items are every
/// inventory icon in the icons room (the *-icon-object images), enough for several pages.
/// </summary>
public sealed class Inventory(Func<GamePoint, GamePoint> pageToGame)
{
    public const string BackgroundName = "inventory-bg-object";
    public const string NextPageArrowName = "increment-inventory-arrow", PreviousPageArrowName = "decrement-inventory-arrow";

    /// <summary>The end of an inventory icon's name: the icons room's item images.</summary>
    public const string ItemIconSuffix = "-icon-object";

    /// <summary>The page arrows' positions on the 640×480 screen (draw-inventory, global 47:15 and 47:21).</summary>
    public static readonly GamePoint PreviousPageArrowAt = new(155, 120), NextPageArrowAt = new(400, 120);

    /// <summary>Items per row and rows per page (draw-inventory's (4 + 1) * (2 + 1)).</summary>
    public const int Columns = 5, Rows = 3, PageSize = Columns * Rows;

    /// <summary>
    /// The inventory's area on the 640×480 screen (global 57): a click outside it closes the inventory
    /// (global 34), and so does the cursor leaving it while an item is held (global 50).
    /// </summary>
    public static bool InArea(GamePoint page) => page.X > 45 && page.X < 607 && page.Y > 100 && page.Y < 446;

    private readonly Func<GamePoint, GamePoint> _pageToGame = pageToGame;
    private readonly List<GameObjectImages> _items = [];
    private readonly Dictionary<GameObjectImages, (int Id, string Name, uint Classes)> _info = [];

    public GameObjectImages Background { get; private set; }
    public GameObjectImages NextPageArrow { get; private set; }
    public GameObjectImages PreviousPageArrow { get; private set; }

    /// <summary>What ego holds, in inventory order.</summary>
    public IReadOnlyList<GameObjectImages> Items => _items;

    /// <summary>The name an item shows when hovered: its inventory object's hover name (<see cref="GameObjects.IconItem"/>), else the icon's name.</summary>
    public string NameOf(GameObjectImages item) => item == null ? null : _info.TryGetValue(item, out var i) && i.Name != null ? i.Name : item.Object.Name;

    /// <summary>The inventory object an item's icon stands for (its icon-item), or -1.</summary>
    public int ItemIdOf(GameObjectImages item) => item != null && _info.TryGetValue(item, out var i) ? i.Id : -1;

    /// <summary>
    /// Whether a held item is given to <paramref name="item"/> rather than used with it: its object has class 23
    /// or 29 (the scripts' [151] / [157], global 39). Classes are the game's starting ones.
    /// </summary>
    public bool IsGivenTo(GameObjectImages item) => item != null && _info.TryGetValue(item, out var i) && (i.Classes & (1u << 22 | 1u << 28)) != 0;

    /// <summary>Whether the background loaded (the inventory can't open without it).</summary>
    public bool IsLoaded => Background?.State(1) != null;

    public bool IsOpen { get; set; }

    /// <summary>The first item on the page shown (inventory-page-start, from 1).</summary>
    public int PageStart { get; private set; } = 1;

    /// <summary>Whether items follow this page (draw-inventory shows the increment arrow, global 47:19).</summary>
    public bool HasNextPage => _items.Count - PageStart + 1 > PageSize;

    /// <summary>Whether items come before this page (the decrement arrow, global 47:13).</summary>
    public bool HasPreviousPage => PageStart > 1;

    /// <summary>Where item slot <paramref name="slot"/> (0–14) of the page goes on the 640×480 screen (draw-inventory, global 47:31-32).</summary>
    public static GamePoint ItemAt(int slot) => new(100 + slot % Columns * 90, 180 + slot / Columns * 65);

    /// <summary>The items on the page shown, with their slots.</summary>
    public IEnumerable<(int Slot, GameObjectImages Item)> PageItems =>
        _items.Skip(PageStart - 1).Take(PageSize).Select((item, slot) => (slot, item));

    /// <summary>What's drawn while the inventory is open, back to front, in game units (empty while it's shut).</summary>
    public IReadOnlyList<GameBlast> Blasts
    {
        get
        {
            var blasts = new List<GameBlast>();
            if (!IsOpen || !IsLoaded) return blasts;
            Add(blasts, Background, new GamePoint(0, 0));
            if (HasPreviousPage) Add(blasts, PreviousPageArrow, PreviousPageArrowAt);
            if (HasNextPage) Add(blasts, NextPageArrow, NextPageArrowAt);
            foreach (var (slot, item) in PageItems) Add(blasts, item, ItemAt(slot));
            return blasts;
        }
    }

    private void Add(List<GameBlast> blasts, GameObjectImages images, GamePoint page)
    {
        if (images?.State(1) is not { } image) return;
        blasts.Add(new GameBlast(image, new GameRect(_pageToGame(page), new GameSize(image.Width, image.Height))));
    }

    /// <summary>
    /// What's at <paramref name="page"/> (a point on the 640×480 screen): outside the area, a page arrow
    /// (global 59, 60), an item icon (the object under the point, kernel-get 216) or empty space.
    /// </summary>
    public InventoryHit HitTest(GamePoint page)
    {
        if (!InArea(page)) return new(InventoryHitKind.Outside);
        if (HasNextPage && Covers(NextPageArrow, NextPageArrowAt, page)) return new(InventoryHitKind.NextPage);
        if (HasPreviousPage && Covers(PreviousPageArrow, PreviousPageArrowAt, page)) return new(InventoryHitKind.PreviousPage);
        foreach (var (slot, item) in PageItems)
            if (Covers(item, ItemAt(slot), page)) return new(InventoryHitKind.Item, item);
        return new(InventoryHitKind.Empty);
    }

    private static bool Covers(GameObjectImages images, GamePoint at, GamePoint page) =>
        images?.State(1) is { } image && new GameRect(at, new GameSize(image.Width, image.Height)).Contains(page);

    /// <summary>The increment arrow: the next 15 items (global 34; the start stops at the last item).</summary>
    public void NextPage()
    {
        if (HasNextPage) PageStart = Math.Min(PageStart + PageSize, _items.Count);
    }

    /// <summary>The decrement arrow: the 15 items before (global 34; the start stops at 1).</summary>
    public void PreviousPage()
    {
        if (HasPreviousPage) PageStart = Math.Max(PageStart - PageSize, 1);
    }

    /// <summary>Opens it when shut, shuts it when open (the I key).</summary>
    public void Toggle()
    {
        if (IsOpen) IsOpen = false;
        else if (IsLoaded) IsOpen = true;
    }

    /// <summary>Loads the background, the arrows and the items; false when the game hasn't got the background.</summary>
    public async Task<bool> LoadAsync(ScummBlock root)
    {
        Background = await GameObjects.LoadAsync(root, BackgroundName);
        NextPageArrow = await GameObjects.LoadAsync(root, NextPageArrowName);
        PreviousPageArrow = await GameObjects.LoadAsync(root, PreviousPageArrowName);
        if (Background?.Room is { } icons)
        {
            var (names, classes) = await Task.Run(() => (GameObjects.HoverNames(root),
                RoomDataExport.ReadObjectTable(root).ToDictionary(o => o.Id, o => o.Classes)));
            foreach (var obj in icons.Objects.Where(o => o.Name?.EndsWith(ItemIconSuffix, StringComparison.Ordinal) == true))
            {
                if (await GameObjects.LoadAsync(icons, obj) is not { States.Count: > 0 } item) continue;
                _items.Add(item);
                int id = GameObjects.IconItem(icons, obj) ?? -1;
                _info[item] = (id, names.GetValueOrDefault(id), classes.GetValueOrDefault(id));
            }
        }
        return IsLoaded;
    }
}
