using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MMUCS.Core;
using MMUCS.Core.Audio;
using MMUCS.Core.Diagnostics;
using MMUCS.Core.Media;

namespace MMUCS.Runtime.Sound;

/// <summary>
/// Plays <see cref="ComiMusic"/> through the <see cref="Mixer"/>'s music group: each track is an
/// <see cref="ImusPlayer"/> (so its loops, hooks and markers behave as in the game) loaded from the music
/// bundles in the background. A track that takes over starts once it's loaded, and the old one fades out
/// then. Markers come from the audio thread and are acted on at the next <see cref="Update"/>. Without a
/// mixer it only keeps track of what would play.
/// </summary>
/// <param name="load">Loads a cue's track (it runs on a worker); <see cref="FromBundles"/> reads the game's.</param>
public sealed class MixerMusic(Func<MusicCue, ImusSound> load) : IMusicOutput
{
    private sealed class Stream
    {
        public MusicCue Cue;
        public Task<ImusSound> Load;
        public ImusPlayer Player;
        public MixerVoice Voice;
        public int Hook;
        public bool Started, Failed;
        public Stream TakesOverFrom;
        public int FadeMs;
        public bool SamePlace;
    }

    private readonly Func<MusicCue, ImusSound> _load = load ?? throw new ArgumentNullException(nameof(load));
    private readonly object _gate = new();
    private Stream _current;
    private (MusicCue Cue, int FadeMs, int Hook)? _atExit;
    private volatile int _endArmed;
    private volatile bool _endReached, _exitReached;
    private bool _paused;

    /// <summary>Where the music plays; null keeps it silent.</summary>
    public Mixer Mixer { get; set; }

    /// <summary>The track playing (or loading) and its hook, for a status line.</summary>
    public MusicCue Current => _current?.Cue;

    public int Playing
    {
        get
        {
            var s = _current;
            if (s == null || s.Failed) return 0;
            if (s.Started && s.Voice is { Finished: true }) return 0;
            return s.Cue.SoundId;
        }
    }

    public void Start(MusicCue cue, int hook)
    {
        Stop(_current, 0);
        _current = New(cue, hook);
        _atExit = null;
    }

    public void Switch(MusicCue cue, int fadeMs, bool samePlace, int hook)
    {
        var old = _current;
        var next = New(cue, hook);
        next.FadeMs = Math.Min(fadeMs, 2000); // iMUSE's crossfades last 2 s at most
        next.SamePlace = samePlace;
        // A track still loading never played: the one it was to take over from is what's playing.
        next.TakesOverFrom = old == null ? null : old.Started && !old.Failed ? old : old.TakesOverFrom;
        _current = next;
        _atExit = null;
    }

    public void SwitchAtExit(MusicCue cue, int fadeMs, int hook) => _atExit = (cue, fadeMs, hook);

    public void FadeOut(int ms)
    {
        Stop(_current, ms);
        _current = null;
        _atExit = null;
    }

    public void SetHook(int hook)
    {
        if (_current is not { } s) return;
        s.Hook = hook;
        s.Player?.Hook = hook;
    }

    public void ArmEnd(int soundId) => _endArmed = soundId;

    public bool TakeEnd()
    {
        if (!_endReached) return false;
        _endReached = false;
        return true;
    }

    /// <summary>Pauses or resumes every music voice (the game stopped or carried on).</summary>
    public bool Paused
    {
        get => _paused;
        set
        {
            _paused = value;
            foreach (var s in new[] { _current, _current?.TakesOverFrom })
                if (s?.Voice != null) s.Voice.Paused = value;
        }
    }

    /// <summary>Once a frame, before the music's refresh: a loaded track starts, an "exit" marker switches.</summary>
    public void Update()
    {
        if (_exitReached)
        {
            _exitReached = false;
            if (_atExit is var (cue, fade, hook)) Switch(cue, fade, samePlace: false, hook);
        }
        var s = _current;
        if (s == null || s.Started || !s.Load.IsCompleted) return;
        s.Started = true;
        if (!s.Load.IsCompletedSuccessfully || s.Load.Result == null)
        {
            Log.Warn($"Game music: {s.Cue.File}: {s.Load.Exception?.GetBaseException().Message ?? "not in the music bundles"}");
            s.Failed = true;
            Stop(s.TakesOverFrom, 1000);
            return;
        }
        s.Player = new ImusPlayer(s.Load.Result) { Hook = s.Hook };
        s.Player.EventRaised += e => OnEvent(s, e);
        if (s.SamePlace && s.TakesOverFrom?.Player is { } was) s.Player.Seek(was.Frame);
        if (Mixer != null)
        {
            s.Voice = Mixer.Play(s.Player, MixGroup.Music);
            s.Voice.Paused = _paused;
        }
        Stop(s.TakesOverFrom, s.FadeMs);
        s.TakesOverFrom = null;
    }

    /// <summary>On the audio thread: the markers the music waits for.</summary>
    private void OnEvent(Stream s, ImusEvent e)
    {
        if (e.Kind != ImusEvent.EventKind.Marker || e.Text is not { Length: > 0 } text) return;
        if (text[0] == '_' && _endArmed == s.Cue.SoundId)
        {
            _endArmed = 0;
            _endReached = true;
        }
        if (text == "exit" && ReferenceEquals(s, _current) && _atExit != null) _exitReached = true;
    }

    private Stream New(MusicCue cue, int hook)
    {
        var load = _load;
        return new Stream { Cue = cue, Hook = hook, Load = Task.Run(() => load(cue)) };
    }

    /// <summary>Stops a stream, and the one it was still to take over from.</summary>
    private static void Stop(Stream s, int fadeMs)
    {
        if (s == null) return;
        s.Voice?.Stop(fadeMs);
        s.TakesOverFrom?.Voice?.Stop(fadeMs);
    }

    /// <summary>
    /// A loader for the game's music bundles (.bun beside <paramref name="gameFile"/>): a cue's track is the
    /// entry whose name starts with its file's number (the bundles use 8.3 names: "1100-H~1.IMX"). Loaded
    /// tracks are kept, as the game goes back to them.
    /// </summary>
    public static Func<MusicCue, ImusSound> FromBundles(string gameFile)
    {
        var index = new Lazy<Dictionary<int, (string Bundle, int Entry)>>(() => IndexBundles(gameFile), LazyThreadSafetyMode.ExecutionAndPublication);
        var cache = new Dictionary<int, ImusSound>();
        return cue =>
        {
            int number = cue.FileNumber;
            lock (cache)
                if (cache.TryGetValue(number, out var cached)) return cached;
            if (!index.Value.TryGetValue(number, out var at)) return null;
            using var bundle = Bundle.Open(at.Bundle);
            var sound = ImusSound.Parse(BundleCodec.Decompress(bundle.Read(bundle.Entries[at.Entry])));
            lock (cache) cache[number] = sound;
            return sound;
        };
    }

    private static Dictionary<int, (string, int)> IndexBundles(string gameFile)
    {
        var index = new Dictionary<int, (string, int)>();
        foreach (var bun in GameFiles.Find(gameFile, ".bun"))
        {
            if (AudioExporter.KindOf(bun) != "music") continue;
            using var bundle = Bundle.Open(bun);
            for (int i = 0; i < bundle.Entries.Count; i++)
            {
                string stem = Path.GetFileNameWithoutExtension(bundle.Entries[i].Name);
                int digits = 0;
                while (digits < stem.Length && char.IsAsciiDigit(stem[digits])) digits++;
                if (digits > 0 && int.TryParse(stem.AsSpan(0, digits), out int n)) index.TryAdd(n, (bun, i));
            }
        }
        return index;
    }
}
