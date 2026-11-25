using MMUCS.Core.Audio;
using MMUCS.Core.Media;
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
