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
    public void A_cutscene_frame_converts_through_its_palette_and_says_when_it_changed()
    {
        if (Game == null) return;
        var item = MediaLibrary.Scan(Game).First(i => i.Id == "video/finale");
        var video = new SmushPlayer(MediaLibrary.LoadVideo(item));
        video.ShowFrame(300);
        Assert.True(video.PictureChanged);
        var pixels = new uint[video.Video.Width * video.Video.Height];
        video.CopyBgra(pixels);
        Assert.False(video.PictureChanged);

        var f = video.Current;
        for (int i = 0; i < pixels.Length; i += 997)
        {
            int c = f.Pixels[i] * 3;
            uint expected = 0xFF000000u | ((uint)f.Palette[c] << 16) | ((uint)f.Palette[c + 1] << 8) | f.Palette[c + 2];
            Assert.Equal(expected, pixels[i]);
        }

        // Going back decodes again from the start, so the picture counts as changed.
        video.ShowFrame(10);
        Assert.True(video.PictureChanged);
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

    [Fact]
    public void Scanning_a_kind_at_a_time_gives_the_whole_library_in_its_order()
    {
        if (Game == null) return;
        var whole = MediaLibrary.Scan(Game);
        foreach (var kind in new[] { MediaKind.Cutscene, MediaKind.Music, MediaKind.Voice })
        {
            var one = MediaLibrary.Scan(Game, kind);
            Assert.All(one, i => Assert.Equal(kind, i.Kind));
            Assert.Equal(whole.Where(i => i.Kind == kind).Select(i => i.Id), one.Select(i => i.Id));
        }
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

public class VoiceLineTests
{
    private static string Game => Environment.GetEnvironmentVariable("MMUCS_GAME") is { } g && File.Exists(g) ? g : null;

    [Theory]
    [InlineData("BCCB301", "BC", "CB", 301)]
    [InlineData("ADGT1017", "AD", "GT", 1017)]
    [InlineData("PU_CP124", "PU", "CP", 124)]
    [InlineData("b2gt301", "B2", "GT", 301)]
    public void A_speech_code_splits_into_scene_speaker_and_number(string code, string scene, string speaker, int number)
        => Assert.Equal((scene, speaker, number), VoiceLines.Split(code));

    [Fact]
    public void Script_strings_give_the_code_and_the_text_without_control_codes()
    {
        var bytes = new byte[] { 1, 2 }.Concat(System.Text.Encoding.ASCII.GetBytes("/BCCB301/IT'S EL"))
            .Concat(new byte[] { 0xFF, 0x03 }).Concat(System.Text.Encoding.ASCII.GetBytes("POLLO!")).Concat(new byte[] { 0, 9 }).ToArray();
        Assert.Equal(new[] { ("BCCB301", "IT'S EL POLLO!") }, VoiceLines.Strings(bytes));
    }

    [Fact]
    public void Speech_gets_its_text_scene_and_speaker_and_sorts_by_line_number()
    {
        if (Game == null) return;
        var root = MMUCS.Core.Scumm.ScummProject.Load(MMUCS.Core.Scumm.ScummProject.FindRelatedFiles(Game));
        var speech = MediaLibrary.Scan(Game, root).Where(i => i.Kind == MediaKind.Voice).ToList();

        var pollo = speech.Single(i => i.Id == "voice/bccb301");
        Assert.Equal("IT'S EL POLLO DIABLO!", pollo.Title);
        Assert.Equal(("BC", "CB", 301), (pollo.Scene, pollo.Speaker, pollo.Number));
        Assert.Equal("bchclub", pollo.SceneName);
        Assert.Equal("Guybrush Threepwood", speech.First(i => i.Speaker == "GT").SpeakerName);
        Assert.True(speech.Count(i => !string.IsNullOrEmpty(i.Title)) >= 8073);

        // Within a scene and speaker, line numbers run in numeric order.
        foreach (var group in speech.GroupBy(i => (i.Scene, i.Speaker)))
        {
            var numbers = group.Select(i => i.Number).ToList();
            Assert.Equal(numbers.OrderBy(n => n), numbers);
        }
        Assert.Equal(speech.Select(i => i.Scene).Distinct().OrderBy(s => s, StringComparer.Ordinal), speech.Select(i => i.Scene).Distinct());
    }
}

public class CutsceneAudioTests
{
    private static string Game => Environment.GetEnvironmentVariable("MMUCS_GAME") is { } g && File.Exists(g) ? g : null;

    [Fact]
    public void Every_cutscene_sound_parses_exactly_and_lasts_as_long_as_its_picture()
    {
        if (Game == null) return;
        foreach (var file in MMUCS.Core.Smush.SmushVideo.FindAll(Game))
        {
            var video = MMUCS.Core.Smush.SmushVideo.Load(file);
            var sound = MMUCS.Core.Smush.SmushAudio.Decode(video);
            Assert.NotNull(sound);
            Assert.Equal(0, sound.LeftoverBytes);
            Assert.Equal(sound.Packets * 1024L, sound.FrameCount);
            double picture = video.Frames.Count / (double)video.Header.FrameRate;
            Assert.InRange(sound.Duration.TotalSeconds - picture, -1.0, 1.0);
        }
    }

    [Fact]
    public void A_pcm_source_reads_on_from_where_it_was_sought()
    {
        var pcm = new byte[400]; // 100 stereo frames, 16-bit
        for (int i = 0; i < 200; i++) BitConverter.TryWriteBytes(pcm.AsSpan(i * 2), (short)(i * 100));
        var src = new PcmSource(pcm, 22050, 2);
        src.Seek(50);
        var buf = new float[20];
        Assert.Equal(10, src.Read(buf));
        Assert.Equal(100 * 100 / 32768f, buf[0], 5);
        Assert.Equal(60, src.Frame);
        src.Seek(95);
        Assert.Equal(5, src.Read(buf));
        Assert.True(src.Ended);
    }
}
