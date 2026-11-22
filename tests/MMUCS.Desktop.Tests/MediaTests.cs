using System;
using System.IO;
using System.Linq;
using System.Threading;
using MMUCS.Core.Media;
using Xunit;

namespace MMUCS.Desktop.Tests;

/// <summary>Core.Media pieces behind the media player window.</summary>
public class MediaTests
{
    /// <summary>The game's COMI.LA0 from MMUCS_GAME; tests that need the game do nothing without it.</summary>
    private static string Game => Environment.GetEnvironmentVariable("MMUCS_GAME") is { } g && File.Exists(g) ? g : null;

    [Fact]
    public void A_paused_voice_keeps_its_place()
    {
        var mixer = new Mixer(100);
        var samples = Enumerable.Range(0, 400).Select(i => i / 1000f).ToArray();
        var voice = mixer.Play(new BufferSource(samples, 100, 2), MixGroup.Music);
        var block = new float[20];

        mixer.Read(block);
        float last = block[^1];
        voice.Paused = true;
        mixer.Read(block);
        Assert.All(block, s => Assert.Equal(0f, s));
        voice.Paused = false;
        mixer.Read(block);
        Assert.Equal(last + 0.001f, block[0], 5);
    }

    [Fact]
    public void The_media_clock_pauses_and_seeks_against_the_played_clock()
    {
        var mixer = new Mixer(1000);
        using var output = new NullAudioOutput(mixer);
        var clock = new MediaClock(output);
        output.Start();

        clock.Seek(TimeSpan.FromSeconds(10));
        Assert.Equal(TimeSpan.FromSeconds(10), clock.Position);
        clock.Play();
        Thread.Sleep(150);
        var running = clock.Position;
        Assert.InRange(running.TotalSeconds, 10.05, 10.5);
        clock.Pause();
        var paused = clock.Position;
        Thread.Sleep(80);
        Assert.Equal(paused, clock.Position);
    }

    [Fact]
    public void A_cutscene_frame_is_the_same_whichever_way_it_is_reached()
    {
        if (Game == null) return;
        var item = MediaLibrary.Scan(Game).First(i => i.Id == "video/finale");
        var direct = new SmushPlayer(MediaLibrary.LoadVideo(item));
        direct.ShowFrame(300);
        var expected = direct.Current.Pixels.ToArray();

        var roundabout = new SmushPlayer(MediaLibrary.LoadVideo(item));
        roundabout.ShowFrame(420);
        roundabout.ShowFrame(300);
        Assert.Equal(300, roundabout.CurrentIndex);
        Assert.Equal(expected, roundabout.Current.Pixels);
    }

    [Fact]
    public void The_library_lists_every_cutscene_track_and_line_once()
    {
        if (Game == null) return;
        var items = MediaLibrary.Scan(Game);
        Assert.Equal(15, items.Count(i => i.Kind == MediaKind.Cutscene));
        Assert.Equal("video/opening", items.First(i => i.Kind == MediaKind.Cutscene).Id);
        Assert.Equal("video/finale", items.Last(i => i.Kind == MediaKind.Cutscene).Id);
        Assert.Equal(129, items.Count(i => i.Kind == MediaKind.Music));
        Assert.Equal(8076, items.Count(i => i.Kind == MediaKind.Voice));
        Assert.Equal(items.Count, items.Select(i => i.Id).Distinct().Count());
        var credits = items.Single(i => i.Id == "music/1098");
        Assert.Equal("Credits1", credits.Title);
        Assert.Equal(7682624 / 22050.0, credits.Duration.TotalSeconds, 2);
    }
}

public class CutsceneOrderTests
{
    [Fact]
    public void The_built_in_order_lists_every_cutscene_once_from_opening_to_finale()
    {
        Assert.True(CutsceneOrder.TryGet("video/opening", out int first, out var opening));
        Assert.True(CutsceneOrder.TryGet("video/finale", out int last, out _));
        Assert.Equal(0, first);
        Assert.Equal(14, last);
        Assert.Equal(1, opening.Disc);
        foreach (var id in new[] { "bbsan", "curserng", "fg010gp", "kis030", "lavaride", "liftcrse", "moreslaw", "newboots", "sb010", "sb020", "sinkshp", "wrecksan", "zap010" })
            Assert.True(CutsceneOrder.TryGet("video/" + id, out int o, out _) && o > first && o < last, id);
        Assert.False(CutsceneOrder.TryGet("video/nope", out _, out _));
    }
}
