using System;
using System.IO;
using MMUCS.Core.Scumm;
using MMUCS.Desktop.ViewModels;
using Xunit;

namespace MMUCS.Desktop.Tests;

/// <summary>The game window's view model: the cursor object loads, and hovering the stand-in hotspot switches its state.
/// Needs the game (MMUCS_GAME).</summary>
[Collection(GameProjectCollection.Name)]
public class GameViewModelTests
{
    private static string Game => Environment.GetEnvironmentVariable("MMUCS_GAME") is { } g && File.Exists(g) ? g : null;

    [Fact]
    public void Cursor_loads_and_switches_to_state_2_over_the_hotspot()
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
