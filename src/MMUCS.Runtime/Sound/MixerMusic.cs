using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        /// <summary>Where the track starts once loaded (a resumed one carries on from here).</summary>
        public long StartFrame;
        /// <summary>A reload (<see cref="Reload"/>): when it loads the same sound, the one playing carries on.</summary>
        public bool KeepIfSame;
        /// <summary>
        /// The game's own track taking over a mod's for this hook (<see cref="SetHook"/>): it starts where its
        /// jumps for the hook go, or, when it has none, the mod's carries on.
        /// </summary>
        public int ForHook;
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

    /// <summary>
    /// The game's own tracks (<see cref="FromBundles"/>), for the hooks a music mod's track can't take: it has only
    /// its loop, so a hook the game's jumps on (a sting, the way out of a loop) is played by the game's track. Null:
    /// a mod's track ignores such hooks.
    /// </summary>
    public Func<MusicCue, ImusSound> Own { get; set; }

    /// <summary>Whether the track playing has loaded (and started).</summary>
    public bool IsLoaded => _current is { Started: true, Failed: false };

    /// <summary>The track playing (or loading) and its hook, for a status line.</summary>
    public MusicCue Current => _current?.Cue;

    /// <summary>The file playing in place of the game's track (a music mod's), null while it's the game's own or loading.</summary>
    public string Replacement => _current?.Player?.Sound.Source;

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

    public MusicPlace? Place
    {
        get
        {
            if (Playing == 0 || _current is not { } s) return null;
            return new MusicPlace(s.Cue, s.Hook, s.Player?.Frame ?? s.StartFrame);
        }
    }

    public void Resume(MusicPlace place)
    {
        Stop(_current, 0);
        _current = New(place.Cue, place.Hook);
        _current.StartFrame = place.Frame;
        _atExit = null;
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

    /// <summary>
    /// Loads the track playing again, as the loader now gives it (music mods switched on or off), and crossfades
    /// to it from its start over <paramref name="fadeMs"/>; when the loader gives the same sound, nothing changes.
    /// A switch waiting for an "exit" marker still comes.
    /// </summary>
    public void Reload(int fadeMs = 500)
    {
        var old = _current;
        if (old == null || old.Failed) return;
        if (!old.Started)
        {
            // Still loading, perhaps the old way: load it afresh in its place.
            var again = New(old.Cue, old.Hook);
            again.FadeMs = old.FadeMs;
            again.SamePlace = old.SamePlace;
            again.StartFrame = old.StartFrame;
            again.TakesOverFrom = old.TakesOverFrom;
            _current = again;
            return;
        }
        if (old.Voice is { Finished: true }) return;
        var next = New(old.Cue, old.Hook);
        next.FadeMs = fadeMs;
        next.TakesOverFrom = old;
        next.KeepIfSame = true;
        _current = next;
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
        // A mod's track (one with a file of its own) has no jump on this hook: the game's track may have.
        if (hook is 0 or ImusPlayer.ExitLoopHook || Own is not { } own || s.Player?.Sound.Source == null || s.Voice is { Finished: true }) return;
        var cue = s.Cue;
        _current = new Stream { Cue = cue, ForHook = hook, TakesOverFrom = s, Load = Task.Run(() => own(cue)) };
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
        // A track with no "exit" marker (a mod's) can't say when: it switches as soon as it's playing.
        if (_exitReached || (_atExit != null && _current?.Player is { } p && !p.Sound.Markers.Exists(m => m.Text == "exit")))
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
        if (s.KeepIfSame && s.TakesOverFrom is { } same && ReferenceEquals(s.Load.Result, same.Player?.Sound))
        {
            _current = same;
            return;
        }
        if (s.ForHook != 0)
        {
            // Where the game's jumps for the hook go (the most of them, when they go to different places).
            var sound = s.Load.Result;
            var jumps = sound.Jumps.Where(j => j.Hook == s.ForHook).GroupBy(j => j.Destination).OrderByDescending(g => g.Count()).FirstOrDefault();
            if (jumps == null)
            {
                _current = s.TakesOverFrom;
                return;
            }
            s.StartFrame = sound.ToFrame(jumps.Key);
            s.FadeMs = Math.Min(jumps.First().FadeMs, 2000);
            Log.Info($"Game music: {s.Cue.File}: the mod's track has no jump on hook {s.ForHook}; the game's plays from frame {s.StartFrame}");
        }
        s.Player = new ImusPlayer(s.Load.Result) { Hook = s.Hook };
        s.Player.EventRaised += e => OnEvent(s, e);
        if (s.SamePlace && s.TakesOverFrom?.Player is { } was) s.Player.Seek(was.Frame);
        else if (s.StartFrame > 0) s.Player.Seek(s.StartFrame);
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

    /// <summary>
    /// A loader that plays the music mods beside <paramref name="gameFile"/> (<see cref="MusicMods"/>) in place of
    /// the tracks they map, and <paramref name="fallback"/>'s for the rest (or when a mod's track won't load).
    /// While <paramref name="enabled"/> says no, it's <paramref name="fallback"/>'s for every track.
    /// </summary>
    public static Func<MusicCue, ImusSound> WithMods(string gameFile, Func<MusicCue, ImusSound> fallback, Func<bool> enabled = null)
    {
        var mods = MusicMods.Load(gameFile);
        foreach (var problem in mods.Problems) Log.Warn($"Music mods: {problem}");
        if (mods.Tracks.Count == 0) return fallback;
        Log.Info($"Music mods: {mods.Tracks.Count} tracks from {string.Join(", ", mods.Tracks.Values.Select(t => t.Mod).Distinct())}");
        var cache = new Dictionary<int, ImusSound>();
        return cue =>
        {
            int number = cue.FileNumber;
            if (enabled?.Invoke() == false || !mods.Tracks.TryGetValue(number, out var track)) return fallback(cue);
            lock (cache)
                if (cache.TryGetValue(number, out var cached)) return cached;
            try
            {
                var sound = MusicMods.Build(track, track.Loop != null || GameLoops(cue, fallback));
                lock (cache) cache[number] = sound;
                return sound;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                Log.Warn($"Music mods: {cue.File}: {Path.GetFileName(track.File)}: {ex.Message}; playing the game's");
                return fallback(cue);
            }
        };
    }

    /// <summary>
    /// Whether the game's own track for <paramref name="cue"/> loops (has a jump); when it won't load, a state's
    /// music does (1000s) and a sequence's doesn't.
    /// </summary>
    private static bool GameLoops(MusicCue cue, Func<MusicCue, ImusSound> load)
    {
        try { if (load(cue) is { } own) return own.Jumps.Count > 0; }
        catch (Exception ex) when (ex is IOException or InvalidDataException) { }
        return cue.SoundId < 2000;
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
