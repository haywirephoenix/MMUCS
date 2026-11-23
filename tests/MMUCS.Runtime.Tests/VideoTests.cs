using MMUCS.Core.Scumm;
using MMUCS.Runtime.Scripts;
using Xunit;

namespace MMUCS.Runtime.Tests;

/// <summary>
/// start-video stops everything until the video is over, as ScummVM's engine does: the engine thread parks
/// inside the instruction, the host plays the video, and the frame carries on when it ends or is skipped (a
/// skip also skips the cutscene it was in). The game tests need MMUCS_GAME.
/// </summary>
public class VideoTests
{
    private static string Game => Environment.GetEnvironmentVariable("MMUCS_GAME") is { } g && File.Exists(g) ? g : null;

    [Fact]
    public void The_engine_thread_parks_in_the_middle_of_a_step_and_carries_on_when_resumed()
    {
        using var engine = new EngineThread();
        var log = new List<string>();
        Assert.True(engine.Run(() => log.Add("one step")));
        Assert.False(engine.Run(() =>
        {
            log.Add("before");
            engine.Park();
            log.Add("after");
        }));
        Assert.True(engine.IsParked);
        Assert.Equal(["one step", "before"], log);
        Assert.True(engine.Resume());
        Assert.Equal(["one step", "before", "after"], log);
        Assert.Throws<InvalidOperationException>(() => engine.Park()); // only on its own thread
        var error = Assert.Throws<InvalidOperationException>(() => engine.Run(() => throw new InvalidOperationException("boom")));
        Assert.Equal("boom", error.Message);
    }

    /// <summary>Boots, clicks Mega-Monkey, and ticks until <paramref name="until"/> or <paramref name="seconds"/> pass.</summary>
    private static ScriptedGame ClickThrough(string gameFile, Func<ScriptedGame, bool> until, int seconds)
    {
        var root = ScummProject.Load(ScummProject.FindRelatedFiles(Game), null);
        var game = new ScriptedGame();
        game.Load(root, gameFile);
        game.Play();
        game.SetMouseCaptured(true);
        game.MoveCursor(GameState.PageToGame(new GamePoint(320, 224)));
        for (int i = 0; i < 60; i++) game.Tick();
        game.Press(MouseButton.Left);
        for (int i = 0; i < 6; i++) game.Tick();
        game.Release(MouseButton.Left);
        for (int i = 0; i < 60 * seconds && !until(game); i++) game.Tick();
        return game;
    }

    [Fact]
    public void Without_the_videos_the_scripts_wait_in_start_video_and_go_on()
    {
        if (Game == null) return;
        using var game = ClickThrough(null, g => g.CurrentRoom == 9, 60);
        Assert.Equal(9, game.CurrentRoom); // thx, logo (the video, skipped), chapter1, cannon
        Assert.Equal(0, game.Faults);
        Assert.False(game.IsVideoPlaying);
    }

    [Fact]
    public void The_opening_plays_to_its_end_before_the_chapter_card()
    {
        if (Game == null) return;
        // To the opening: the scripts enter room 1 and start opening.san, then wait.
        using var game = ClickThrough(Game, g => g.IsVideoPlaying, 40);
        Assert.Equal(("opening.san", 1), (game.VideoName, game.CurrentRoom));
        long frames = game.Loop.Frames;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (game.Video == null && DateTime.UtcNow < deadline)
        {
            game.Tick(); // it loads on a worker; the clock starts once it's in
            Thread.Sleep(10);
        }
        Assert.NotNull(game.Video);

        // Ten seconds in: no frames ran, still room 1.
        for (int i = 0; i < 600; i++) game.Tick();
        Assert.Equal((frames, 1), (game.Loop.Frames, game.CurrentRoom));
        Assert.Equal(10.0, game.VideoTime.TotalSeconds, 1); // no sound output here: the video keeps time by jiffies
        Assert.Contains("video opening.san", game.Status);

        // Played to its end (by jiffies: its length in seconds × 60), then the chapter card.
        int left = (int)Math.Ceiling((game.Video.Duration - game.VideoTime).TotalSeconds * 60) + 1;
        for (int i = 0; i < left && game.IsVideoPlaying; i++) game.Tick();
        Assert.False(game.IsVideoPlaying);
        for (int i = 0; i < 60 && game.CurrentRoom == 1; i++) game.Tick();
        Assert.Equal(4, game.CurrentRoom);
        Assert.Equal(0, game.Faults);
    }

    [Fact]
    public void Skipping_the_opening_goes_on_to_the_chapter_card_at_once()
    {
        if (Game == null) return;
        using var game = ClickThrough(Game, g => g.IsVideoPlaying, 40);
        game.SkipVideo();
        Assert.False(game.IsVideoPlaying);
        for (int i = 0; i < 30 && game.CurrentRoom == 1; i++) game.Tick();
        Assert.Equal(4, game.CurrentRoom);
        Assert.Equal(0, game.Faults);
    }
}
