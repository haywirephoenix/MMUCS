using MMUCS.Core.Scumm;
using MMUCS.Runtime;
using Xunit;

namespace MMUCS.Runtime.Tests;

/// <summary>
/// The game as it runs, driven as the window drives it (input, then jiffies): the cursor and its hotspot
/// state, the verb coin, and the inventory with its pages, items, labels and ways of closing. Needs the
/// game (MMUCS_GAME).
/// </summary>
public class GameStateTests
{
    private static string Game => Environment.GetEnvironmentVariable("MMUCS_GAME") is { } g && File.Exists(g) ? g : null;

    [Fact]
    public async Task Cursor_coin_and_inventory_play_as_the_scripts_say()
    {
        if (Game == null) return;
        var root = ScummProject.Load(ScummProject.FindRelatedFiles(Game), null);
        var vm = new GameState();
        int changes = 0;
        vm.Changed += () => changes++;
        await vm.LoadAsync(root);
        Assert.True(changes > 0);

        Assert.True(vm.CursorFrame != null, vm.Status);
        Assert.Contains(GameState.CursorObjectName, vm.Status);
        Assert.Equal(2, vm.CursorStateCount);
        var normal = vm.CursorFrame;

        vm.SetMouseCaptured(true);
        Assert.False(vm.IsMouseCaptured); // not until it's playing
        vm.Play();
        vm.SetMouseCaptured(true);

        vm.MoveCursor(new GamePoint(5, 5));
        Assert.Equal(1, vm.CursorState);
        vm.MoveCursor(vm.Hotspot.Center);
        Assert.Equal(2, vm.CursorState);
        Assert.NotSame(normal, vm.CursorFrame);

        // Holding on the hotspot opens the verb coin above the cursor, looping anim 6; its zones play 7, 8 and 9.
        Assert.True(vm.Coin.IsLoaded);
        var press = vm.CursorPosition;
        vm.PressCursor();
        Assert.True(vm.Coin.IsShown);
        Assert.Equal(VerbCoin.IdleAnim, vm.Coin.Anim);
        var face = vm.Coin.Face;
        Assert.Equal(press + VerbCoin.SpawnOffset, face.Center);
        Assert.Equal(CoinZone.None, vm.Coin.Zone); // it opens with the cursor below the zones
        Assert.Equal(1, vm.CursorState); // hotspots don't count while the coin is open
        vm.MoveCursor(vm.Hotspot.Center);
        Assert.Equal(1, vm.CursorState);
        Assert.NotNull(vm.Coin.Frame);
        for (int i = 0; i < VerbCoin.JiffiesPerFrame; i++) vm.Tick();
        Assert.Equal(1, vm.Coin.Step); // a frame every 6 jiffies
        for (int i = 0; i < 92 * VerbCoin.JiffiesPerFrame; i++) vm.Tick();
        Assert.Equal(0, vm.Coin.Step); // a 93-frame loop
        vm.MoveCursor(new GamePoint(face.X + face.Width / 6, face.Center.Y));
        Assert.Equal((CoinZone.Left, VerbCoin.LeftAnim, 0), (vm.Coin.Zone, vm.Coin.Anim, vm.Coin.Step));
        vm.MoveCursor(face.Center);
        Assert.Equal((CoinZone.Middle, VerbCoin.MiddleAnim), (vm.Coin.Zone, vm.Coin.Anim));
        vm.MoveCursor(new GamePoint(face.Right - face.Width / 6, face.Center.Y));
        Assert.Equal((CoinZone.Right, VerbCoin.RightAnim), (vm.Coin.Zone, vm.Coin.Anim));
        Assert.Equal(face, vm.Coin.Face); // it stays where it opened
        vm.MoveCursor(new GamePoint(face.Center.X, face.Top + 5)); // on the coin, above the zones
        Assert.Equal((CoinZone.None, VerbCoin.IdleAnim, 0), (vm.Coin.Zone, vm.Coin.Anim, vm.Coin.Step));
        vm.MoveCursor(face.Center);
        vm.MoveCursor(new GamePoint(face.Center.X, face.Bottom - 5)); // and below them
        Assert.Equal((CoinZone.None, VerbCoin.IdleAnim), (vm.Coin.Zone, vm.Coin.Anim));
        vm.MoveCursor(vm.Hotspot.Center);
        vm.ReleaseCursor();
        Assert.False(vm.Coin.IsShown);
        Assert.Equal(2, vm.CursorState); // back over the hotspot once it's gone
        Assert.Null(vm.Coin.Frame);

        vm.MoveCursor(new GamePoint(-50, 9999)); // clamped to the canvas
        Assert.Equal(new GamePoint(0, GameState.CanvasHeight), vm.CursorPosition);
        Assert.Equal(1, vm.CursorState);

        // I toggles the inventory: its background (640×472) at 0,0 of the 4:3 page, centred on the 16:9 canvas.
        Assert.True(vm.Inventory.IsLoaded);
        Assert.Equal(new GamePoint((GameState.CanvasWidth - 640) / 2, 0), GameState.PageOrigin);
        void At(double x, double y) => vm.MoveCursor(GameState.PageToGame(new GamePoint(x, y)));
        var inv = vm.Inventory;
        int count = inv.Items.Count;
        Assert.True(count > 2 * Inventory.PageSize, $"{count} items"); // every *-icon-object in the icons room
        vm.MoveCursor(vm.Hotspot.Center);
        vm.ToggleInventory();
        Assert.True(inv.IsOpen);
        Assert.False(vm.IsOverHotspot); // the inventory covers the room
        vm.PressCursor();
        Assert.False(vm.Coin.IsShown);
        // Background, the next-page arrow (no previous page yet), then 15 items on a 5×3 grid.
        Assert.Equal(1 + 1 + Inventory.PageSize, inv.Blasts.Count);
        Assert.Equal(new GameRect(GameState.PageOrigin, new GameSize(640, 472)), inv.Blasts[0].Bounds);
        Assert.Equal(GameState.PageToGame(Inventory.NextPageArrowAt), inv.Blasts[1].Bounds.TopLeft);
        Assert.Equal(GameState.PageToGame(Inventory.ItemAt(7)), inv.Blasts[2 + 7].Bounds.TopLeft);
        Assert.Equal(new GamePoint(100 + 2 * 90, 180 + 65), Inventory.ItemAt(7));

        // The arrows page by 15, and light the cursor up.
        At(Inventory.NextPageArrowAt.X + 40, Inventory.NextPageArrowAt.Y + 20);
        Assert.Equal(2, vm.CursorState);
        vm.PressCursor();
        vm.ReleaseCursor();
        Assert.Equal(16, inv.PageStart);
        Assert.True(inv.HasPreviousPage);
        Assert.Equal(1 + 2 + Inventory.PageSize, inv.Blasts.Count);
        At(Inventory.PreviousPageArrowAt.X + 40, Inventory.PreviousPageArrowAt.Y + 20);
        vm.PressCursor();
        vm.ReleaseCursor();
        Assert.Equal(1, inv.PageStart);
        Assert.False(inv.HasPreviousPage);

        // A click on an item makes it the cursor (when the button comes up); over another item it shows state 2.
        var first = inv.Items[0];
        At(Inventory.ItemAt(0).X + 20, Inventory.ItemAt(0).Y + 20);
        Assert.Equal(2, vm.CursorState);
        vm.PressCursor();
        Assert.Null(vm.HeldItem);
        vm.ReleaseCursor();
        Assert.Same(first, vm.HeldItem);
        Assert.Equal((1, first.State(1)), (vm.CursorState, vm.CursorFrame)); // over itself
        At(Inventory.ItemAt(1).X + 20, Inventory.ItemAt(1).Y + 20);
        Assert.Equal((2, first.State(2)), (vm.CursorState, vm.CursorFrame));
        // A click on empty space puts it back.
        At(320, 120);
        Assert.Equal(InventoryHitKind.Empty, vm.InventoryHit.Kind);
        vm.PressCursor();
        vm.ReleaseCursor();
        Assert.Null(vm.HeldItem);
        Assert.Same(normal, vm.CursorFrame);

        // Holding an item, leaving the inventory's area closes it; the item stays the cursor.
        At(Inventory.ItemAt(0).X + 20, Inventory.ItemAt(0).Y + 20);
        vm.PressCursor();
        vm.ReleaseCursor();
        At(300, 300);
        Assert.True(inv.IsOpen);
        At(30, 300);
        Assert.False(inv.IsOpen);
        Assert.Same(first, vm.HeldItem);
        vm.MoveCursor(vm.Hotspot.Center);
        vm.PressCursor(); // an item held: no verb coin
        Assert.False(vm.Coin.IsShown);
        vm.ReleaseCursor();
        // A right-click puts it back, and then opens and shuts the inventory.
        vm.RightClick();
        Assert.Null(vm.HeldItem);
        vm.RightClick();
        Assert.True(inv.IsOpen);
        // With nothing held, moving out doesn't close it, but a click outside does.
        At(30, 300);
        Assert.True(inv.IsOpen);
        vm.PressCursor();
        vm.ReleaseCursor();
        Assert.False(inv.IsOpen);
        // Items are named after the inventory object their icon stands for (its verb 93's icon-item), and the
        // hovered one is named under the inventory, in the chosen font.
        var arm = inv.Items.Single(i => i.Object.Name == "skeleton-arm-icon-object");
        Assert.Equal((336, "skeleton arm"), (inv.ItemIdOf(arm), inv.NameOf(arm)));
        Assert.All(inv.Items, i => Assert.False(string.IsNullOrEmpty(inv.NameOf(i))));
        vm.RightClick();
        Assert.True(inv.IsOpen);
        At(Inventory.ItemAt(0).X + 20, Inventory.ItemAt(0).Y + 20);
        Assert.Equal(inv.NameOf(first), vm.Label);
        Assert.NotNull(vm.LabelPalette);
        At(300, 120);
        Assert.Null(vm.Label);
        At(Inventory.ItemAt(0).X + 20, Inventory.ItemAt(0).Y + 20);
        vm.PressCursor();
        vm.ReleaseCursor();
        Assert.Null(vm.Label); // over the item held
        var second = inv.Items[1];
        At(Inventory.ItemAt(1).X + 20, Inventory.ItemAt(1).Y + 20);
        string verb = inv.IsGivenTo(second) ? "give" : "use", prep = verb == "give" ? "to" : "with";
        Assert.Equal($"{verb} {inv.NameOf(first)} {prep} {inv.NameOf(second)}", vm.Label);
        vm.RightClick(); // put it back

        // Holding the button on an item opens its verb coin above its slot (global 66) and doesn't pick it up.
        At(Inventory.ItemAt(6).X + 20, Inventory.ItemAt(6).Y + 20);
        vm.PressCursor();
        Assert.False(vm.Coin.IsShown);
        for (int i = 1; i < GameState.ItemHoldJiffies; i++) vm.Tick();
        Assert.False(vm.Coin.IsShown); // 12 jiffies: not yet
        vm.Tick();
        Assert.True(vm.Coin.IsShown);
        Assert.Null(vm.Label);
        Assert.Equal(1, vm.CursorState);
        var origin = GameState.PageToGame(Inventory.ItemAt(6) + new GamePoint(38, -30));
        Assert.Equal(origin, vm.Coin.Origin);
        vm.ReleaseCursor();
        Assert.False(vm.Coin.IsShown);
        Assert.Null(vm.HeldItem);
        Assert.True(inv.IsOpen);
        // A press that comes up first still picks the item up, and no coin follows.
        vm.PressCursor();
        vm.ReleaseCursor();
        Assert.Same(inv.Items[6], vm.HeldItem);
        for (int i = 0; i < 2 * GameState.ItemHoldJiffies; i++) vm.Tick();
        Assert.False(vm.Coin.IsShown);
        vm.RightClick();
        vm.RightClick();
        Assert.False(inv.IsOpen);

        // I puts a held item back too.
        vm.ToggleInventory();
        At(Inventory.ItemAt(0).X + 20, Inventory.ItemAt(0).Y + 20);
        vm.PressCursor();
        vm.ReleaseCursor();
        vm.ToggleInventory();
        Assert.False(inv.IsOpen);
        Assert.Null(vm.HeldItem);
        Assert.Empty(inv.Blasts);
        vm.MoveCursor(vm.Hotspot.Center);
        Assert.Equal(2, vm.CursorState);

        vm.ToggleInventory();
        vm.Stop();
        Assert.False(vm.Inventory.IsOpen);
        vm.ToggleInventory(); // only while playing
        Assert.False(vm.Inventory.IsOpen);
        Assert.False(vm.IsMouseCaptured);
        Assert.Equal(1, vm.CursorState);
    }
}
