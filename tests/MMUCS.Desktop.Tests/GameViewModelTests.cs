using System;
using System.IO;
using MMUCS.Core.Scumm;
using MMUCS.Desktop.ViewModels;
using Xunit;

namespace MMUCS.Desktop.Tests;

/// <summary>The game window's view model: the cursor object loads, hovering the stand-in hotspot switches its state, and
/// holding on it opens the verb coin.
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

            vm.MoveCursor(vm.Hotspot.Center);
            vm.StopCommand.Execute(null);
            Assert.False(vm.IsMouseCaptured);
            Assert.Equal(1, vm.CursorState);
        });
    }
}
