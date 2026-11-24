using System;
using System.IO;
using MMUCS.Core.Scumm;
using MMUCS.Desktop.ViewModels;
using MMUCS.Runtime;
using Xunit;

namespace MMUCS.Desktop.Tests;

/// <summary>
/// The game window hosting the player (the game is tested in MMUCS.Runtime.Tests, its drawing in
/// MMUCS.Player.Tests): input goes through to the game, scripted or the stand-ins, time too, and a new frame
/// comes back for the canvas, with the room, the game's cursor and the hover label. Needs the game (MMUCS_GAME).
/// </summary>
public class GameViewModelTests
{
    private static string Game => Environment.GetEnvironmentVariable("MMUCS_GAME") is { } g && File.Exists(g) ? g : null;

    [Fact]
    public void The_scripts_boot_into_the_difficulty_room_and_a_click_starts_the_game()
    {
        if (Game == null) return;
        var root = ScummProject.Load(ScummProject.FindRelatedFiles(Game), null);
        MediaPlayerViewModelTests.UiThread.Run(async ui =>
        {
            var vm = new GameViewModel(root, Game);
            await vm.Loading;
            Assert.True(vm.RunScripts); // the default
            Assert.True(vm.SmoothCamera); // the default too (no settings here, so nothing is saved)
            Assert.True(vm.Scripted.SmoothCamera);
            vm.SmoothCamera = false; // the toolbar button
            Assert.False(vm.Scripted.SmoothCamera);
            vm.SmoothCamera = true;
            Assert.True(vm.Scripted.SmoothCamera);
            Assert.True(new Services.AppSettings().SmoothCamera); // new settings start with it on
            Assert.False(vm.Frame.IsRunning);

            vm.PlayCommand.Execute(null);
            Assert.True(vm.IsPlaying);
            Assert.StartsWith("room 87 easyhard", vm.Status);
            await vm.ScriptedView.Building; // the room's scene loads in the background
            Assert.NotNull(vm.Frame.Room);
            Assert.Equal(640, vm.Frame.Room.Width);

            // The game's own cursor (system-cursor-icon, chosen by the scripts) once the game has the mouse.
            vm.IsMouseCaptured = true;
            Assert.True(vm.Scripted.IsMouseCaptured);
            var button = GameState.PageToGame(new GamePoint(320, 224)); // easyhard-hard-button-object's middle
            vm.MoveCursor(new Avalonia.Point(button.X, button.Y));
            await vm.ScriptedView.CursorLoading;
            vm.Tick();
            Assert.NotNull(vm.Frame.Cursor);

            // Click Mega-Monkey: about 3.5 s later the game leaves for room 2.
            for (int i = 0; i < 60; i++) vm.Tick();
            vm.PressCursor();
            for (int i = 0; i < 6; i++) vm.Tick();
            vm.ReleaseCursor();
            for (int i = 0; i < 60 * 5 && vm.Scripted.CurrentRoom == 87; i++) vm.Tick();
            Assert.StartsWith("room 2 thx", vm.Status);
            Assert.Equal(0, vm.Scripted.Faults);

            await vm.ScriptedView.Building; // room 2's scene: let it land before the test's UI thread ends
            Assert.Same(vm.ScriptedView.Scene.Background, vm.Frame.Room.Background); // the scene, with the actors of the last frame

            // Pause keeps the game where it is: Play carries on in room 2.
            vm.PauseCommand.Execute(null);
            Assert.False(vm.IsPlaying || vm.IsMouseCaptured);
            Assert.True(vm.IsStarted && vm.StopCommand.CanExecute(null) && !vm.PauseCommand.CanExecute(null));
            Assert.StartsWith("Paused", vm.Hint);
            vm.PlayCommand.Execute(null);
            Assert.StartsWith("room 2 thx", vm.Status);

            // Stop ends it: paused or not, the state goes and Play boots afresh into room 87.
            vm.PauseCommand.Execute(null);
            vm.StopCommand.Execute(null);
            Assert.False(vm.IsPlaying || vm.IsStarted || vm.StopCommand.CanExecute(null));
            Assert.Equal(0, vm.Scripted.CurrentRoom);
            Assert.Equal("Press Play to boot the game.", vm.Hint);
            vm.PlayCommand.Execute(null);
            Assert.StartsWith("room 87 easyhard", vm.Status);
            await vm.ScriptedView.Building;

            // Switching to the stand-ins stops the game.
            vm.RunScripts = false;
            Assert.False(vm.IsPlaying || vm.IsMouseCaptured);
            await vm.ScriptedView.CursorLoading;
        });
    }

    [Fact]
    public void Input_reaches_the_stand_ins_and_frames_come_back()
    {
        if (Game == null) return;
        var root = ScummProject.Load(ScummProject.FindRelatedFiles(Game), null);
        MediaPlayerViewModelTests.UiThread.Run(async ui =>
        {
            var vm = new GameViewModel(root, Game) { RunScripts = false };
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
            Assert.False(vm.IsPlaying || vm.IsMouseCaptured || vm.IsStarted);
            Assert.Empty(vm.Frame.Blasts);
        });
    }
}
