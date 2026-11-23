using System;
using System.IO;
using MMUCS.Core.Scumm;
using MMUCS.Desktop.ViewModels;
using Xunit;

namespace MMUCS.Desktop.Tests;

/// <summary>The game window's view model: the cursor object loads, hovering the stand-in hotspot switches its state, and
/// holding on it opens the verb coin, and I toggles the inventory, where items are picked up and pages turned.
/// Needs the game (MMUCS_GAME).</summary>
[Collection(GameProjectCollection.Name)]
public class GameViewModelTests
{
    private static string Game => Environment.GetEnvironmentVariable("MMUCS_GAME") is { } g && File.Exists(g) ? g : null;

    [Fact]
    public void Cursor_switches_over_the_hotspot_and_holding_opens_the_verb_coin()
    {
        if (Game == null) return;
        var root = ScummProject.Load(ScummProject.FindRelatedFiles(Game), null);
        MediaPlayerViewModelTests.UiThread.Run(async ui =>
        {
            var vm = new GameViewModel(root);
            await ui.Until(() => vm.CursorFrame != null || !vm.Status.StartsWith("Loading"), "the cursor to load");
            Assert.True(vm.CursorFrame != null, vm.Status);
            Assert.Contains(GameViewModel.CursorObjectName, vm.Status);
            Assert.Equal(2, vm.CursorStateCount);
            var normal = vm.CursorFrame;

            Assert.True(vm.PlayCommand.CanExecute(null));
            vm.PlayCommand.Execute(null);
            vm.IsMouseCaptured = true;

            vm.MoveCursor(new Avalonia.Point(5, 5));
            Assert.Equal(1, vm.CursorState);
            vm.MoveCursor(vm.Hotspot.Center);
            Assert.Equal(2, vm.CursorState);
            Assert.NotSame(normal, vm.CursorFrame);

            // Holding on the hotspot opens the verb coin above the cursor, looping anim 6; its zones play 7, 8 and 9.
            await ui.Until(() => vm.Coin.IsLoaded, "the verb coin to load");
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
            for (int i = 0; i < 93; i++) vm.Tick();
            Assert.Equal(0, vm.Coin.Step); // a 93-frame loop
            vm.MoveCursor(new Avalonia.Point(face.X + face.Width / 6, face.Center.Y));
            Assert.Equal((CoinZone.Left, VerbCoin.LeftAnim, 0), (vm.Coin.Zone, vm.Coin.Anim, vm.Coin.Step));
            vm.MoveCursor(face.Center);
            Assert.Equal((CoinZone.Middle, VerbCoin.MiddleAnim), (vm.Coin.Zone, vm.Coin.Anim));
            vm.MoveCursor(new Avalonia.Point(face.Right - face.Width / 6, face.Center.Y));
            Assert.Equal((CoinZone.Right, VerbCoin.RightAnim), (vm.Coin.Zone, vm.Coin.Anim));
            Assert.Equal(face, vm.Coin.Face); // it stays where it opened
            vm.MoveCursor(new Avalonia.Point(face.Center.X, face.Top + 5)); // on the coin, above the zones
            Assert.Equal((CoinZone.None, VerbCoin.IdleAnim, 0), (vm.Coin.Zone, vm.Coin.Anim, vm.Coin.Step));
            vm.MoveCursor(face.Center);
            vm.MoveCursor(new Avalonia.Point(face.Center.X, face.Bottom - 5)); // and below them
            Assert.Equal((CoinZone.None, VerbCoin.IdleAnim), (vm.Coin.Zone, vm.Coin.Anim));
            vm.MoveCursor(vm.Hotspot.Center);
            vm.ReleaseCursor();
            Assert.False(vm.Coin.IsShown);
            Assert.Equal(2, vm.CursorState); // back over the hotspot once it's gone
            Assert.Null(vm.Coin.Frame);

            vm.MoveCursor(new Avalonia.Point(-50, 9999)); // clamped to the canvas
            Assert.Equal(new Avalonia.Point(0, GameViewModel.GameHeight), vm.CursorPosition);
            Assert.Equal(1, vm.CursorState);

            // I toggles the inventory: its background (640×472) at 0,0 of the 4:3 page, centred on the 16:9 canvas.
            await ui.Until(() => vm.Inventory.IsLoaded, "the inventory to load");
            Assert.Equal(new Avalonia.Point((GameViewModel.GameWidth - 640) / 2, 0), GameViewModel.PageOrigin);
            void At(double x, double y) => vm.MoveCursor(GameViewModel.PageToGame(new Avalonia.Point(x, y)));
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
            Assert.Equal(new Avalonia.Rect(GameViewModel.PageOrigin, new Avalonia.Size(640, 472)), inv.Blasts[0].Bounds);
            Assert.Equal(GameViewModel.PageToGame(Inventory.NextPageArrowAt), inv.Blasts[1].Bounds.TopLeft);
            Assert.Equal(GameViewModel.PageToGame(Inventory.ItemAt(7)), inv.Blasts[2 + 7].Bounds.TopLeft);
            Assert.Equal(new Avalonia.Point(100 + 2 * 90, 180 + 65), Inventory.ItemAt(7));

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
            vm.StopCommand.Execute(null);
            Assert.False(vm.Inventory.IsOpen);
            vm.ToggleInventory(); // only while playing
            Assert.False(vm.Inventory.IsOpen);
            Assert.False(vm.IsMouseCaptured);
            Assert.Equal(1, vm.CursorState);
        });
    }
}
