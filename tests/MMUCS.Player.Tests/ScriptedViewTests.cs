using MMUCS.Core.Scumm;
using MMUCS.Player.Rooms;
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
        using var game = new ScriptedGame();
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
        using var game = new ScriptedGame();
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

    [Fact]
    public async Task A_script_s_video_shows_on_the_page_instead_of_the_room()
    {
        if (Game == null) return;
        var root = ScummProject.Load(ScummProject.FindRelatedFiles(Game), null);
        using var game = new ScriptedGame();
        game.Load(root, Game);
        var view = new ScriptedView(game);
        game.Play();
        game.SetMouseCaptured(true);
        game.MoveCursor(GameState.PageToGame(new GamePoint(320, 224)));
        for (int i = 0; i < 60; i++) game.Tick();
        game.Press(MouseButton.Left);
        for (int i = 0; i < 6; i++) game.Tick();
        game.Release(MouseButton.Left);
        for (int i = 0; i < 60 * 40 && !game.IsVideoPlaying; i++) game.Tick();
        Assert.Equal("opening.san", game.VideoName);

        // Black while it loads, then its frames, decoded in the background, ten seconds in.
        var loading = view.Capture();
        Assert.Null(loading.Cutscene);
        Assert.Null(loading.Cursor);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (game.Video == null && DateTime.UtcNow < deadline)
        {
            game.Tick();
            await Task.Delay(10);
        }
        for (int i = 0; i < 600; i++) game.Tick();
        view.Capture();
        await view.Decoding;
        var frame = view.Capture();
        var cutscene = Assert.IsType<CutsceneFrame>(frame.Cutscene);
        Assert.Equal((640, 480), (cutscene.Width, cutscene.Height));
        using (var picture = Draw(frame, "scripted-opening-10s"))
        {
            Assert.Equal(SKColors.Black, picture.GetPixel(50, 240)); // the 16:9 sides
            Assert.Contains(Enumerable.Range(0, 48), y => picture.GetPixel(426, y * 10) != SKColors.Black); // the video on the page
        }
        frame.Release();
        loading.Release();

        // Skipped: the room again.
        game.SkipVideo();
        for (int i = 0; i < 30 && game.CurrentRoom == 1; i++) game.Tick();
        var after = view.Capture();
        Assert.Null(after.Cutscene);
        Assert.Equal(4, game.CurrentRoom);
    }

    [Fact]
    public async Task The_first_scene_shows_the_actors_the_scripts_put_there()
    {
        if (Game == null) return;
        var root = ScummProject.Load(ScummProject.FindRelatedFiles(Game), null);
        using var game = new ScriptedGame();
        game.Load(root); // no videos: the opening is passed over
        var view = new ScriptedView(game);
        game.Play();
        game.SetMouseCaptured(true);
        game.MoveCursor(GameState.PageToGame(new GamePoint(320, 224)));
        for (int i = 0; i < 60; i++) game.Tick();
        game.Press(MouseButton.Left);
        for (int i = 0; i < 6; i++) game.Tick();
        game.Release(MouseButton.Left);
        for (int i = 0; i < 60 * 60 && game.CurrentRoom != 9; i++) game.Tick();
        for (int i = 0; i < 60 * 5; i++) game.Tick();
        Assert.Equal(9, game.CurrentRoom);
        view.Capture();
        await view.Building;

        var frame = view.Capture();
        var room = Assert.IsType<RoomScene>(frame.Room);
        Assert.Equal(game.Vm.Actors.Pictures.Count, room.Actors.Count);
        var ego = Assert.Single(room.Actors, a => a.Actor == 1);
        using (var pixels = SKBitmap.FromImage(ego.Image))
            Assert.Contains(pixels.Pixels, p => p.Alpha > 0); // Guybrush's costume drew something
        using var picture = Draw(frame, "scripted-cannon");
        Assert.Equal(0, game.Faults);
    }

    [Fact]
    public async Task A_talk_line_shows_on_the_page_in_the_game_font()
    {
        if (Game == null) return;
        var root = ScummProject.Load(ScummProject.FindRelatedFiles(Game), null);
        using var game = new ScriptedGame();
        game.Load(root, Game);
        var view = new ScriptedView(game) { Text = MMUCS.Player.Text.SubtitleRenderer.Create(game.Fonts, MMUCS.Player.Text.SubtitleFontKind.Game, null, true) };
        game.Play();
        game.SetMouseCaptured(true);
        game.MoveCursor(GameState.PageToGame(new GamePoint(320, 224)));
        for (int i = 0; i < 60; i++) game.Tick();
        game.Press(MouseButton.Left);
        for (int i = 0; i < 6; i++) game.Tick();
        game.Release(MouseButton.Left);
        for (int i = 0; i < 60 * 120 && (game.IsVideoPlaying || game.CurrentRoom != 9 || game.Vm.Subtitles.Count == 0); i++)
        {
            if (game.IsVideoPlaying) game.SkipVideo();
            game.Tick();
        }
        Assert.Single(game.Vm.Subtitles);
        for (int i = 0; i < 12; i++) game.Tick(); // a frame after it went up, so it's in what the frame drew
        view.Capture();
        await view.Building;

        var frame = view.Capture();
        var text = Assert.Single(frame.RoomTexts, t => t is { IsEmpty: false }); // over the talking actor, so it moves with the room
        Assert.DoesNotContain(frame.Texts, t => t is { IsEmpty: false });
        Assert.NotNull(frame.Room);
        using var picture = Draw(frame, "scripted-cannon-talk");
        Assert.Equal(0, game.Faults);
    }

    [Fact]
    public async Task The_inventory_and_verb_coin_show_over_the_room()
    {
        if (Game == null) return;
        var root = ScummProject.Load(ScummProject.FindRelatedFiles(Game), null);
        using var game = new ScriptedGame();
        game.Load(root, Game);
        var view = new ScriptedView(game);
        game.Play();
        game.SetMouseCaptured(true);
        game.MoveCursor(GameState.PageToGame(new GamePoint(320, 224)));
        for (int i = 0; i < 60; i++) game.Tick();
        game.Press(MouseButton.Left);
        for (int i = 0; i < 6; i++) game.Tick();
        game.Release(MouseButton.Left);
        for (int i = 0; i < 60 * 200 && game.Loop.Jiffies < 60 * 140; i++)
        {
            if (game.IsVideoPlaying) game.SkipVideo();
            game.Tick();
        }
        Assert.Equal(9, game.CurrentRoom);

        // The inventory: its blasted images load in the background, then the frame has them on the page.
        game.MoveCursor(GameState.PageToGame(new GamePoint(320, 300)));
        game.Press(MouseButton.Right);
        for (int i = 0; i < 6; i++) game.Tick();
        game.Release(MouseButton.Right);
        for (int i = 0; i < 30; i++) game.Tick();
        var frame = view.Capture();
        for (int i = 0; i < 100 && frame.Blasts.Count < game.Vm.BlastObjects.Count; i++)
        {
            await view.CursorLoading;
            await view.Building;
            await Task.Delay(20);
            frame = view.Capture();
        }
        Assert.Equal(game.Vm.BlastObjects.Count, frame.Blasts.Count);
        Assert.True(frame.Blasts.Count >= 2);
        Assert.Equal(GameState.PageOrigin.X, frame.Blasts[0].Bounds.X, 3); // inventory-bg-object at the page's 0,0
        using (Draw(frame, "scripted-inventory")) { }

        // The verb coin: actor 50, drawn over the blasts.
        game.Press(MouseButton.Right);
        for (int i = 0; i < 6; i++) game.Tick();
        game.Release(MouseButton.Right);
        for (int i = 0; i < 30; i++) game.Tick();
        game.MoveCursor(GameState.PageToGame(new GamePoint(152, 268)));
        game.Press(MouseButton.Left);
        for (int i = 0; i < 40; i++) game.Tick();
        frame = view.Capture();
        var upper = Assert.IsType<MMUCS.Player.Rooms.RoomScene>(frame.UpperActors);
        Assert.Equal(50, Assert.Single(upper.Actors).Actor);
        using (Draw(frame, "scripted-verb-coin")) { }
        game.Release(MouseButton.Left);
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
