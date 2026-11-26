using MMUCS.Core.Audio;
using MMUCS.Core.Media;
using MMUCS.Runtime.Sound;
using Xunit;

namespace MMUCS.Runtime.Tests;

/// <summary>
/// Music mods: a replacement track is built as an iMUS sound the game's player loops like its own, and the
/// mods beside the game decode to the MD5 their FLAC streams carry.
/// </summary>
public class MusicModTests
{
    private static string Game => Environment.GetEnvironmentVariable("MMUCS_GAME") is { } g && File.Exists(g) ? g : null;

    [Fact]
    public void BuiltTrackPlaysItsIntroThenLoopsAndMarksTheLoopsEnd()
    {
        // Ten frames counting up: an intro of 2, a loop of 2..8, a tail of 8..10 for the crossfade.
        var sound = ImusSound.Build(100, 1, 10,
            [new(0, 2), new(2, 6), new(8, 2)], [new(8, 2, 0, 0)], [new(0, "title"), new(8, "_end")], 10,
            (bytes, at) => { for (int i = 0; i < 10; i++) bytes[at + i * 2 + 1] = (byte)i; });
        Assert.Equal("title", sound.Title);
        Assert.Equal(10, sound.FrameCount);

        var player = new ImusPlayer(sound);
        var markers = new List<string>();
        player.EventRaised += e => { if (e.Kind == ImusEvent.EventKind.Marker) markers.Add(e.Text); };
        var buffer = new float[16];
        Assert.Equal(16, player.Read(buffer));
        int[] heard = [.. buffer.Select(s => (int)Math.Round(s * 128))];
        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7, 2, 3, 4, 5, 6, 7, 2, 3], heard);
        Assert.Equal(["title", "_end", "_end"], markers);
    }

    [Fact]
    public void MusicSaysWhichFileStandsInForTheGamesTrack()
    {
        var sound = ImusSound.Build(100, 1, 4, [new(0, 4)], [], [], 4, (_, _) => { }, "/mods/music/Album/01 - Theme.flac");
        var music = new MixerMusic(_ => sound);
        music.Start(new MusicCue(2, 1099, "stateMenu", 0, 0, 0, "1099-Menu"), 0);
        Assert.Null(music.Replacement); // still loading
        SpinWait.SpinUntil(() => { music.Update(); return music.IsLoaded; }, 5000);
        Assert.Equal("/mods/music/Album/01 - Theme.flac", music.Replacement);

        var own = ImusSound.Build(100, 1, 4, [new(0, 4)], [], [], 4, (_, _) => { });
        var game = new MixerMusic(_ => own);
        game.Start(new MusicCue(2, 1099, "stateMenu", 0, 0, 0, "1099-Menu"), 0);
        SpinWait.SpinUntil(() => { game.Update(); return game.IsLoaded; }, 5000);
        Assert.Null(game.Replacement);
    }

    [Fact]
    public void SwitchingModsOffReloadsThePlayingTrackAsTheGamesAndAReloadOfTheSameSoundCarriesOn()
    {
        var mod = ImusSound.Build(100, 1, 400, [new(0, 400)], [], [], 400, (_, _) => { }, "/mods/music/Album/01 - Theme.flac");
        var own = ImusSound.Build(100, 1, 400, [new(0, 400)], [], [], 400, (_, _) => { });
        bool mods = true;
        var music = new MixerMusic(_ => mods ? mod : own); // no mixer: the track doesn't move on by itself
        var cue = new MusicCue(2, 1099, "stateMenu", 0, 0, 0, "1099-Menu");
        music.Resume(new MusicPlace(cue, 0, 44));
        SpinWait.SpinUntil(() => { music.Update(); return music.IsLoaded; }, 5000);
        Assert.Equal(44, music.Place?.Frame);

        // The loader gives the same sound: the track carries on where it was.
        music.Reload();
        SpinWait.SpinUntil(() => { music.Update(); return music.IsLoaded && music.Place?.Frame == 44; }, 5000);
        Assert.Equal("/mods/music/Album/01 - Theme.flac", music.Replacement);
        Assert.Equal(44, music.Place?.Frame);

        // Mods off: the game's own track takes over, from its start.
        mods = false;
        music.Reload();
        SpinWait.SpinUntil(() => { music.Update(); return music.IsLoaded && music.Replacement == null; }, 5000);
        Assert.Null(music.Replacement);
        Assert.Equal(new MusicPlace(cue, 0, 0), music.Place);
    }

    [Fact]
    public void ModTrackLoopsOnlyWhereTheGamesOwnDoes()
    {
        if (Game == null || MusicTable.Load(Game) is not { } table) return;
        var mods = MusicMods.Load(Game);
        var load = MixerMusic.WithMods(Game, MixerMusic.FromBundles(Game));
        // 2105-Interlude1 ends by running out, and the scripts' room music waits for that; 1100-Hold1 loops.
        foreach (var (number, loops) in new[] { (2105, false), (1100, true) })
        {
            if (!mods.Tracks.TryGetValue(number, out var track) || track.Loop != null) continue;
            var cue = table.States.Concat(table.Sequences).First(c => c.FileNumber == number);
            var sound = load(cue);
            Assert.NotNull(sound.Source);
            Assert.Equal(loops, sound.Jumps.Count > 0);
        }
    }

    [Fact]
    public void ModsBesideTheGameDecodeToTheirMd5()
    {
        if (Game == null || MusicMods.Folder(Game) is not { } folder || !Directory.Exists(folder)) return;
        var mods = MusicMods.Load(Game);
        Assert.Empty(mods.Problems);
        foreach (var file in mods.Tracks.Values.Select(t => t.File).Distinct().Take(3))
            Assert.True(Flac.Open(file).Verify(), file);
        if (mods.Tracks.Values.FirstOrDefault() is { } track)
        {
            var sound = MusicMods.Build(track);
            Assert.Contains(sound.Markers, m => m.Text == "_end");
            Assert.True(sound.FrameCount > 0);
        }
    }
}
