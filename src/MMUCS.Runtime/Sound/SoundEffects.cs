using System;
using System.Collections.Generic;
using MMUCS.Core.Audio;
using MMUCS.Core.Diagnostics;
using MMUCS.Core.Media;

namespace MMUCS.Runtime.Sound;

/// <summary>
/// The game's sound effects as Digital iMUSE plays them for the scripts (ScummVM startSfx, stopSound,
/// isSoundRunning and the set-param / fade-param commands): each start plays the sound resource (its SOUN
/// block's iMUS) as a new track at full volume, centred, so a sound can play more than once at a time;
/// stopping, a volume or a pan goes to every copy of it. Without a mixer the tracks are still read along
/// with the game's time (<see cref="Update"/>), so the scripts that wait for a sound to end see it end.
/// </summary>
/// <param name="load">A sound resource by number, or null when there's none.</param>
public sealed class SoundEffects(Func<int, ImusSound> load)
{
    /// <summary>iMUSE's parameters and the sound-kludge commands that set them (ScummVM dimuse_defs.h, parseScriptCmds).</summary>
    public const int VolumeParam = 0x600, PanParam = 0x700;
    public const int StopAllCommand = 10, SetParamCommand = 12, FadeParamCommand = 14;

    private sealed class Track
    {
        public int Id;
        public ImusPlayer Player;
        public MixerVoice Voice;
        public bool Stopped;
        public bool Ended => Stopped || (Voice != null ? Voice.Finished : Player.Finished);
    }

    private readonly Func<int, ImusSound> _load = load ?? throw new ArgumentNullException(nameof(load));
    private readonly List<Track> _tracks = [];
    private readonly Dictionary<int, ImusSound> _sounds = [];
    private float[] _scratch = [];
    private bool _paused;

    /// <summary>Where the effects play; without one they're only read along with the game's time.</summary>
    public Mixer Mixer { get; set; }

    /// <summary>A sound started (its number), for a log.</summary>
    public event Action<int> Started;

    /// <summary>The sounds playing now, each once.</summary>
    public IEnumerable<int> Playing
    {
        get
        {
            var seen = new HashSet<int>();
            foreach (var t in _tracks)
                if (!t.Ended && seen.Add(t.Id)) yield return t.Id;
        }
    }

    /// <summary><c>start-sound</c>: a new copy of sound <paramref name="id"/> from its start.</summary>
    public void Start(int id)
    {
        if (!_sounds.TryGetValue(id, out var sound))
        {
            try { sound = _load(id); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Warn($"Game sound {id}: {ex.Message}");
                sound = null;
            }
            _sounds[id] = sound;
        }
        if (sound == null) return;
        var track = new Track { Id = id, Player = new ImusPlayer(sound) };
        if (Mixer != null)
        {
            track.Voice = Mixer.Play(track.Player, MixGroup.Effects);
            track.Voice.Paused = _paused;
        }
        _tracks.Add(track);
        Started?.Invoke(id);
    }

    /// <summary><c>stop-sound</c>: every copy of it ends.</summary>
    public void Stop(int id)
    {
        foreach (var t in _tracks)
            if (t.Id == id) End(t, 0);
    }

    public void StopAll()
    {
        foreach (var t in _tracks) End(t, 0);
    }

    /// <summary><c>sound-running</c>: whether a copy of it still plays.</summary>
    public bool IsRunning(int id)
    {
        foreach (var t in _tracks)
            if (t.Id == id && !t.Ended) return true;
        return false;
    }

    /// <summary>A sound-kludge list for effects (stop all, set param, fade param); false for anything else.</summary>
    public bool Command(ReadOnlySpan<int> list)
    {
        Span<int> a = stackalloc int[5];
        list[..Math.Min(5, list.Length)].CopyTo(a);
        switch (a[0])
        {
            case StopAllCommand: StopAll(); return true;
            case SetParamCommand: SetParam(a[1], a[2], a[3], 0); return true;
            case FadeParamCommand: SetParam(a[1], a[2], a[3], a[4]); return true;
            default: return false;
        }
    }

    /// <summary>
    /// A sound's volume (0–127) or pan (0–127, 64 the middle), at once or over <paramref name="ticks"/>
    /// 60ths of a second; a fade to silence ends it. Other parameters (priority, detune…) don't change
    /// what's heard here.
    /// </summary>
    public void SetParam(int id, int param, int value, int ticks)
    {
        int ms = ticks * 1000 / 60;
        foreach (var t in _tracks)
        {
            if (t.Id != id || t.Ended || t.Voice == null) continue;
            switch (param)
            {
                case VolumeParam:
                    if (value <= 0 && ticks > 0) End(t, ms);
                    else t.Voice.FadeTo(Math.Clamp(value, 0, 127) / 127f, Math.Max(1, ms));
                    break;
                case PanParam:
                    t.Voice.Pan = (Math.Clamp(value, 0, 127) - 64) / 64f;
                    break;
            }
        }
    }

    public bool Paused
    {
        get => _paused;
        set
        {
            _paused = value;
            foreach (var t in _tracks)
                t.Voice?.Paused = value;
        }
    }

    /// <summary>Once a frame: ended tracks go, and without a mixer the rest are read for <paramref name="jiffies"/>.</summary>
    public void Update(int jiffies)
    {
        _tracks.RemoveAll(t => t.Ended);
        if (Mixer != null || _paused) return;
        foreach (var t in _tracks)
        {
            int frames = t.Player.SampleRate * jiffies / 60, need = frames * t.Player.Channels;
            if (_scratch.Length < need) _scratch = new float[need];
            t.Player.Read(_scratch.AsSpan(0, need));
        }
    }

    private static void End(Track t, int fadeMs)
    {
        t.Voice?.Stop(fadeMs);
        if (fadeMs <= 0 || t.Voice == null) t.Stopped = true;
    }

    /// <summary>A loader for the game's sound resources: DSOU's room and offset, the SOUN block there, its iMUS.</summary>
    public static Func<int, ImusSound> FromResources(Scripts.ScriptStore store, Core.Scumm.ScummBlock root)
    {
        var directory = Core.Scumm.RoomScripts.Sounds(root);
        return id =>
        {
            if (id <= 0 || id >= directory.Count) return null;
            var (room, offset) = directory[id];
            if ((room == 0 && offset == 0) || store.RoomBlock(room) is not { } lflf) return null;
            foreach (var b in lflf.Children)
                if (b.Tag == Core.Scumm.ScummTag.SOUN && b.Offset == lflf.Offset + offset)
                    return ImusSound.Parse(b.DataSpan.ToArray());
            return null;
        };
    }
}
