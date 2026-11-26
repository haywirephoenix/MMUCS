using MMUCS.Core.Media;
using MMUCS.Runtime.Sound;
using Xunit;

namespace MMUCS.Runtime.Tests;

/// <summary>
/// The F1 menu's volume sliders as iMUSE's groups: sound-kludge 0x2000–0x2002 set them, kernel-get 221–223
/// read them back, and they set the mixer's groups; the music ducks under speech. No game needed.
/// </summary>
public class SoundGroupsTests
{
    [Fact]
    public void The_menu_s_volumes_are_read_back_and_set_the_mixer_s_groups()
    {
        var mixer = new Mixer();
        var groups = new SoundGroups { Mixer = mixer };
        Assert.Equal(127, groups.Get(0xDF));

        Assert.True(groups.Command([SoundGroups.SetEffectsCommand, 15]));
        Assert.True(groups.Command([SoundGroups.SetVoiceCommand, 53]));
        Assert.True(groups.Command([SoundGroups.SetMusicCommand, 69]));

        Assert.Equal(15, groups.Get(0xDD));
        Assert.Equal(53, groups.Get(0xDE));
        Assert.Equal(69, groups.Get(0xDF));
        Assert.Null(groups.Get(0xE0));
        Assert.Equal(15 / 127f, mixer.GetGroupVolume(MixGroup.Effects), 5);
        Assert.Equal(53 / 127f, mixer.GetGroupVolume(MixGroup.Voice), 5);
        Assert.Equal(69 / 127f, mixer.GetGroupVolume(MixGroup.Music), 5);
        Assert.Equal(1f, mixer.GetGroupVolume(MixGroup.Video));
    }

    [Fact]
    public void Volumes_out_of_range_are_refused_and_other_commands_are_left()
    {
        var groups = new SoundGroups();
        Assert.True(groups.Command([SoundGroups.SetMusicCommand, 128]));
        Assert.True(groups.Command([SoundGroups.SetMusicCommand, -1]));
        Assert.Equal(127, groups.Music);
        Assert.False(groups.Command([0x1000, 1099]));
        Assert.False(groups.Command([]));
    }

    [Fact]
    public void Music_ducks_to_80_of_128_under_speech_in_steps_and_comes_straight_back()
    {
        var mixer = new Mixer();
        var groups = new SoundGroups { Mixer = mixer };
        int target = 127 * SoundGroups.DuckFactor / 128; // 79

        groups.Update(5, speaking: true);
        Assert.Equal(127, groups.MusicHeard); // the first step comes a tenth of a second in
        groups.Update(1, speaking: true);
        Assert.Equal(109, groups.MusicHeard);
        groups.Update(6, speaking: true);
        Assert.Equal(91, groups.MusicHeard);
        groups.Update(60, speaking: true);
        Assert.Equal(target, groups.MusicHeard);
        Assert.Equal(target / 127f, mixer.GetGroupVolume(MixGroup.Music), 5);
        Assert.Equal(1f, mixer.GetGroupVolume(MixGroup.Voice));

        groups.Update(1, speaking: false);
        Assert.Equal(127, groups.MusicHeard);
    }

    [Fact]
    public void Turning_the_music_down_while_ducked_keeps_it_ducked_from_the_new_volume()
    {
        var groups = new SoundGroups();
        groups.Update(60, speaking: true);
        groups.Command([SoundGroups.SetMusicCommand, 64]);
        Assert.True(groups.MusicHeard <= 64 * SoundGroups.DuckFactor / 128 + 1);
        groups.Update(60, speaking: true);
        Assert.Equal(64 * SoundGroups.DuckFactor / 128, groups.MusicHeard);
        groups.Update(1, speaking: false);
        Assert.Equal(64, groups.MusicHeard);
    }

    [Fact]
    public void A_group_volume_change_ramps_over_the_next_block_instead_of_jumping()
    {
        var mixer = new Mixer();
        var pcm = new byte[4096 * 2];
        for (int i = 0; i < pcm.Length; i += 2) { pcm[i] = 0xFF; pcm[i + 1] = 0x3F; } // a steady 0x3FFF, mono
        mixer.Play(new PcmSource(pcm, mixer.SampleRate, 1), MixGroup.Music, pan: -1f);
        var block = new float[512 * 2];
        mixer.Read(block);
        float full = block[^2];

        mixer.SetGroupVolume(MixGroup.Music, 0f);
        mixer.Read(block);
        Assert.True(block[0] > full * 0.9f, "starts where the last block ended");
        Assert.True(Math.Abs(block[^2]) < full * 0.01f, "ends at the new volume");
        Assert.True(block[512] < block[0] && block[512] > block[^2], "and goes there in between");
    }
}
