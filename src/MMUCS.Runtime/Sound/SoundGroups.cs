using System;
using MMUCS.Core.Media;

namespace MMUCS.Runtime.Sound;

/// <summary>
/// Digital iMUSE's group volumes (ScummVM dimuse_groups.cpp), 0…127 each: the F1 menu's effects, voice and music
/// sliders. The scripts set them with <c>sound-kludge</c> 0x2000, 0x2001 and 0x2002 (at boot from the registry,
/// and while a slider is dragged) and read them back with <c>kernel-get</c> 221, 222 and 223 to place the sliders.
/// They set the <see cref="Mixer"/>'s groups, so they cover every sound in a group: a music mod's track as much
/// as the game's.
/// <para>
/// While speech plays the music ducks to 80/128 of its volume, as iMUSE's 10 Hz speech gain reduction does it
/// (dimuse_engine.cpp): down by 18 every tenth of a second, and straight back up when the speech ends.
/// </para>
/// </summary>
public sealed class SoundGroups
{
    /// <summary>The sound-kludge commands for the group volumes (ScummVM parseScriptCmds).</summary>
    public const int SetEffectsCommand = 0x2000, SetVoiceCommand = 0x2001, SetMusicCommand = 0x2002;

    /// <summary>What a group's volume is until the scripts set it (iMUSE's groups start full).</summary>
    public const int Full = 127;

    /// <summary>The music under speech: 80/128 of its volume (COMI's factor; FT's and The Dig's is 82).</summary>
    public const int DuckFactor = 80;

    /// <summary>How far ducking lowers the music every tenth of a second (6 jiffies).</summary>
    private const int DuckStep = 18, DuckJiffies = 6;

    private int _duckJiffies;

    /// <summary>Where the groups play; null keeps the volumes without applying them.</summary>
    public Mixer Mixer
    {
        get;
        set
        {
            field = value;
            Apply();
        }
    }

    public int Effects { get; private set; } = Full;
    public int Voice { get; private set; } = Full;
    public int Music { get; private set; } = Full;

    /// <summary>The music's volume after ducking (iMUSE's music-effective group), 0…127.</summary>
    public int MusicHeard { get; private set; } = Full;

    /// <summary>A <c>sound-kludge</c> list; true when it set a group volume.</summary>
    public bool Command(ReadOnlySpan<int> list)
    {
        if (list.Length == 0 || list[0] is < SetEffectsCommand or > SetMusicCommand) return false;
        // The menu's sliders stay in 0…127; iMUSE refuses more (setGroupVol returns −5).
        if (list.Length < 2 || list[1] is < 0 or > Full) return true;
        int volume = list[1];
        switch (list[0])
        {
            case SetEffectsCommand: Effects = volume; break;
            case SetVoiceCommand: Voice = volume; break;
            default:
                // Ducking carries on from the new volume: it never leaves the music louder than it's set.
                MusicHeard = Math.Min(MusicHeard * volume / Math.Max(1, Music), volume);
                Music = volume;
                break;
        }
        Apply();
        return true;
    }

    /// <summary><c>kernel-get</c> 221, 222, 223 (getGroupSfxVol, getGroupVoiceVol, getGroupMusicVol); null for others.</summary>
    public int? Get(int kernelGet) => kernelGet switch
    {
        0xDD => Effects,
        0xDE => Voice,
        0xDF => Music,
        _ => null,
    };

    /// <summary>Once a frame: the music ducks while <paramref name="speaking"/> and comes back up when not.</summary>
    public void Update(int jiffies, bool speaking)
    {
        int target = speaking ? Music * DuckFactor / 128 : Music;
        if (MusicHeard == target) { _duckJiffies = 0; return; }
        if (MusicHeard < target) MusicHeard = target; // straight back up
        else
        {
            _duckJiffies += jiffies;
            while (_duckJiffies >= DuckJiffies && MusicHeard > target)
            {
                _duckJiffies -= DuckJiffies;
                MusicHeard = Math.Max(target, MusicHeard - DuckStep);
            }
        }
        Apply();
    }

    private void Apply()
    {
        if (Mixer is not { } mixer) return;
        mixer.SetGroupVolume(MixGroup.Effects, Effects / (float)Full);
        mixer.SetGroupVolume(MixGroup.Voice, Voice / (float)Full);
        mixer.SetGroupVolume(MixGroup.Music, MusicHeard / (float)Full);
    }
}
