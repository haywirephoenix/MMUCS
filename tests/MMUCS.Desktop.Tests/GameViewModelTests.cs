using System;
using System.IO;
using System.Linq;
using MMUCS.Core.Scumm;
using MMUCS.Desktop.ViewModels;
using MMUCS.Runtime;
using Xunit;

namespace MMUCS.Desktop.Tests;

/// <summary>
/// The game window's view of the running game (the game itself is tested in MMUCS.Runtime.Tests): input goes
/// through to it, and what it shows comes back as canvas geometry, the inventory's images and the hover label
/// drawn in the game's fonts. Needs the game (MMUCS_GAME).
/// </summary>
[Collection(GameProjectCollection.Name)]
public class GameViewModelTests
{
    private static string Game => Environment.GetEnvironmentVariable("MMUCS_GAME") is { } g && File.Exists(g) ? g : null;

    [Fact]
    public void The_canvas_gets_the_game_inventory_and_label()
    {
        if (Game == null) return;
        var root = ScummProject.Load(ScummProject.FindRelatedFiles(Game), null);
        MediaPlayerViewModelTests.UiThread.Run(async ui =>
        {
            var vm = new GameViewModel(root, Game);
            await vm.Loading;
            Assert.True(vm.Game.Inventory.IsLoaded && vm.Game.Coin.IsLoaded && vm.FontInfo != null);
            Assert.Equal(new Avalonia.Point(GameState.PageOrigin.X, 0), GameViewModel.PageOrigin);
            Assert.NotNull(vm.CursorFrame);
            Assert.Contains(GameState.CursorObjectName, vm.Status);

            Assert.True(vm.PlayCommand.CanExecute(null));
            vm.PlayCommand.Execute(null);
            Assert.True(vm.IsPlaying);
            Assert.False(vm.PlayCommand.CanExecute(null));
            vm.IsMouseCaptured = true;
            Assert.True(vm.Game.IsMouseCaptured);
            Assert.Null(vm.Hint);

            // The coin comes back as a frame and its bounds on the canvas.
            vm.MoveCursor(vm.Hotspot.Center);
            Assert.True(vm.IsOverHotspot);
            vm.PressCursor();
            Assert.NotNull(vm.CoinFrame);
            Assert.False(vm.CoinBounds.Width == 0);
            vm.ReleaseCursor();
            Assert.Null(vm.CoinFrame);

            // The inventory: background, next-page arrow and a page of items; hovering one names it in the game's font.
            vm.ToggleInventory();
            Assert.Equal(1 + 1 + Inventory.PageSize, vm.Blasts.Count);
            Assert.Equal(new Avalonia.Rect(GameViewModel.PageOrigin, new Avalonia.Size(640, 472)), vm.Blasts[0].Bounds);
            var slot = GameState.PageToGame(Inventory.ItemAt(0));
            vm.MoveCursor(new Avalonia.Point(slot.X + 20, slot.Y + 20));
            Assert.Equal(vm.Game.Inventory.NameOf(vm.Game.Inventory.Items[0]), vm.Label);
            Assert.NotNull(vm.LabelOverlay);
            vm.MoveCursor(vm.Hotspot.Center);
            Assert.Null(vm.LabelOverlay);

            // Game time: the window's jiffies reach the game.
            vm.MoveCursor(new Avalonia.Point(slot.X + 20, slot.Y + 20));
            vm.PressCursor();
            for (int i = 0; i < GameState.ItemHoldJiffies; i++) vm.Tick();
            Assert.NotNull(vm.CoinFrame);
            vm.ReleaseCursor();

            vm.StopCommand.Execute(null);
            Assert.False(vm.IsPlaying);
            Assert.False(vm.IsMouseCaptured);
            Assert.Empty(vm.Blasts);
        });
    }
}

/// <summary>Room text colours (blast-text): value 1 takes the text's colour, the rest are palette indices as they are.</summary>
public class RoomTextColorTests
{
    [Fact]
    public void Value_1_is_the_colour_and_the_rest_stay()
    {
        Assert.Equal(19, MMUCS.Core.Fonts.TextRasterizer.MapValue(1, 0, 19, MMUCS.Core.Fonts.TextColorMode.Room));
        Assert.Equal(225, MMUCS.Core.Fonts.TextRasterizer.MapValue(225, 0, 19, MMUCS.Core.Fonts.TextColorMode.Room));
        Assert.Equal(-1, MMUCS.Core.Fonts.TextRasterizer.MapValue(0, 0, 19, MMUCS.Core.Fonts.TextColorMode.Room));
        // Cutscene text keeps its own rule: font 1's ink (1) isn't the colour code's negation, so it stays 1.
        Assert.Equal(1, MMUCS.Core.Fonts.TextRasterizer.MapValue(1, 0, 19));
    }
}
