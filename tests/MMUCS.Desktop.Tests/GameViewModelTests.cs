using System;
using System.IO;
using MMUCS.Core.Scumm;
using MMUCS.Desktop.ViewModels;
using MMUCS.Runtime;
using Xunit;

namespace MMUCS.Desktop.Tests;

/// <summary>
/// The game window hosting the player (the game is tested in MMUCS.Runtime.Tests, its drawing in
/// MMUCS.Player.Tests): input goes through to the game, time too, and a new frame comes back for the canvas,
/// with the hover label drawn in the game's fonts. Needs the game (MMUCS_GAME).
/// </summary>
[Collection(GameProjectCollection.Name)]
public class GameViewModelTests
{
    private static string Game => Environment.GetEnvironmentVariable("MMUCS_GAME") is { } g && File.Exists(g) ? g : null;

    [Fact]
    public void Input_reaches_the_game_and_frames_come_back()
    {
        if (Game == null) return;
        var root = ScummProject.Load(ScummProject.FindRelatedFiles(Game), null);
        MediaPlayerViewModelTests.UiThread.Run(async ui =>
        {
            var vm = new GameViewModel(root, Game);
            await vm.Loading;
            Assert.True(vm.Game.Inventory.IsLoaded && vm.Game.Coin.IsLoaded && vm.FontInfo != null);
            Assert.Contains(GameState.CursorObjectName, vm.Status);
            Assert.False(vm.Frame.IsRunning);
            Assert.Null(vm.Frame.Cursor); // not until the game has the mouse

            Assert.True(vm.PlayCommand.CanExecute(null));
            vm.PlayCommand.Execute(null);
            Assert.True(vm.IsPlaying && vm.Frame.IsRunning);
            Assert.False(vm.PlayCommand.CanExecute(null));
            vm.IsMouseCaptured = true;
            Assert.True(vm.Game.IsMouseCaptured);
            Assert.Null(vm.Hint);
            Assert.NotNull(vm.Frame.Cursor);

            // The coin.
            var hotspot = vm.Game.Hotspot.Center;
            vm.MoveCursor(new Avalonia.Point(hotspot.X, hotspot.Y));
            Assert.Equal(new Avalonia.Point(hotspot.X, hotspot.Y), vm.CursorPosition);
            Assert.True(vm.Frame.IsHotspotHovered);
            vm.PressCursor();
            Assert.NotNull(vm.Frame.Coin);
            vm.ReleaseCursor();
            Assert.Null(vm.Frame.Coin);

            // The inventory, and the label drawn in the game's font.
            vm.ToggleInventory();
            Assert.Equal(1 + 1 + Inventory.PageSize, vm.Frame.Blasts.Count);
            var slot = GameState.PageToGame(Inventory.ItemAt(0));
            vm.MoveCursor(new Avalonia.Point(slot.X + 20, slot.Y + 20));
            Assert.Equal(vm.Game.Inventory.NameOf(vm.Game.Inventory.Items[0]), vm.Frame.Label);
            Assert.NotNull(vm.Frame.LabelText);

            // Time: the window's jiffies reach the game.
            vm.PressCursor();
            for (int i = 0; i < GameState.ItemHoldJiffies; i++) vm.Tick();
            Assert.NotNull(vm.Frame.Coin);
            vm.ReleaseCursor();

            vm.StopCommand.Execute(null);
            Assert.False(vm.IsPlaying || vm.IsMouseCaptured);
            Assert.Empty(vm.Frame.Blasts);
        });
    }
}
