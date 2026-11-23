using MMUCS.Core.Scumm;
using MMUCS.Runtime;
using MMUCS.Runtime.Scripts;
using SkiaSharp;
using Xunit;

namespace MMUCS.Player.Tests;

/// <summary>
/// The game run by its scripts, drawn headless as the game window draws it: the room the scripts entered,
/// with the objects they show, the cursor image they chose on the pointer, and a click on the page reaching
/// them. Needs the game (MMUCS_GAME); MMUCS_FRAMES=folder saves the frames as PNGs.
/// </summary>
public class ScriptedViewTests
{
    private static string Game => Environment.GetEnvironmentVariable("MMUCS_GAME") is { } g && File.Exists(g) ? g : null;

    [Fact]
    public void Canvas_positions_reach_the_scripts_on_the_page()
    {
        var game = new ScriptedGame();
        game.Load(TinyGame());
        game.MoveCursor(GameState.PageToGame(new GamePoint(320, 224)));
        Assert.Equal((320, 224), game.Loop.Mouse);
        Assert.True(game.IsCursorOnPage);
        game.MoveCursor(new GamePoint(10, 10)); // the canvas's left side, off the page
        Assert.False(game.IsCursorOnPage);
    }

    [Fact]
    public async Task The_difficulty_room_shows_with_the_game_s_cursor_and_a_click_moves_on()
    {
        if (Game == null) return;
        var root = ScummProject.Load(ScummProject.FindRelatedFiles(Game), null);
        var game = new ScriptedGame();
        game.Load(root);
        var view = new ScriptedView(game);
        game.Play();
        view.Capture();
        await view.Building;

        game.SetMouseCaptured(true);
        game.MoveCursor(GameState.PageToGame(new GamePoint(320, 224)));
        view.Capture();
        await view.CursorLoading;
        var frame = view.Capture();
        Assert.Equal(87, game.CurrentRoom);
        Assert.NotNull(frame.Room);
        var cursor = Assert.NotNull(frame.Cursor);
        Assert.Equal((105, 1), (game.Vm.CursorImage.Object, game.Vm.CursorImage.Image));
        using (var picture = Draw(frame, "scripted-easyhard"))
        {
            Assert.Equal(SKColors.Black, picture.GetPixel(50, 240)); // beside the 640 room
            Assert.NotEqual(SKColors.Black, picture.GetPixel(426, 60));
        }

        // Over the button the cursor loop (local 2007) switches to the cursor's second image a frame later.
        for (int i = 0; i < 30; i++) game.Tick();
        view.Capture();
        await view.CursorLoading;
        Assert.Equal(2, game.Vm.CursorImage.Image);
        Assert.NotNull(view.Capture().Cursor);

        game.Press(MouseButton.Left);
        for (int i = 0; i < 6; i++) game.Tick();
        game.Release(MouseButton.Left);
        for (int i = 0; i < 60 * 5 && game.CurrentRoom == 87; i++) game.Tick();
        Assert.Equal(2, game.CurrentRoom);
        view.Capture();
        await view.Building;
        using var thx = Draw(view.Capture(), "scripted-thx");
        Assert.Equal(0, game.Faults);
    }

    /// <summary>A root with nothing in it: enough for the input mapping, which needs no game data.</summary>
    private static ScummBlock TinyGame() => new() { Tag = 0, TagName = "empty" };

    private static SKBitmap Draw(GameFrame frame, string name)
    {
        var bitmap = new SKBitmap(853, 480);
        using (var canvas = new SKCanvas(bitmap)) frame.Render(canvas, new SKRect(0, 0, 853, 480));
        if (Environment.GetEnvironmentVariable("MMUCS_FRAMES") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            using var data = SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(Path.Combine(dir, $"{name}.png"), data.ToArray());
        }
        return bitmap;
    }
}
