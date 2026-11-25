using System;
using System.Collections.Generic;
using System.IO;
using MMUCS.Core;
using MMUCS.Core.Audio;
using MMUCS.Core.Diagnostics;
using MMUCS.Core.Media;
using MMUCS.Runtime.Scripts;

namespace MMUCS.Runtime.Sound;

/// <summary>A line's speech playing and where: its tag and the sample frame it has reached.</summary>
public readonly record struct SpeechPlace(string Tag, long Frame);

/// <summary>
/// The lines' speech (ScummVM <c>playSpeech</c> for COMI): the tag names an entry of the voice bundles
/// (BCCB301 is BCCB301.IMX), played through the mixer's voice group. One line at a time. Without a mixer
/// the line is read along with the game's time (<see cref="Update"/>), so a line lasts as long as its speech.
/// </summary>
/// <param name="load">A voice file by name ("BCCB301.IMX"), or null when there's none.</param>
public sealed class VoicePlayer(Func<string, ImusSound> load) : ISpeech
{
    private readonly Func<string, ImusSound> _load = load ?? throw new ArgumentNullException(nameof(load));
    private ImusPlayer _player;
    private MixerVoice _voice;
    private float[] _scratch = [];
    private bool _paused, _muted;
    private int _frequency = 256;
    private double _readAhead; // without a mixer, the part of a frame read too few so far

    public Mixer Mixer { get; set; }

    /// <summary>The line's tag playing now, or null.</summary>
    public string Playing => IsPlaying ? _tag : null;
    private string _tag;

    public bool IsPlaying => _player != null && (_voice != null ? !_voice.Finished : !_player.Finished);

    /// <summary>The line's speech playing and how far it has got, for a save; null when none plays.</summary>
    public SpeechPlace? Place => IsPlaying ? new SpeechPlace(_tag, _player.Frame) : null;

    /// <summary>Plays <paramref name="place"/>'s speech from where it was (a load putting the line back).</summary>
    public void Resume(SpeechPlace place) => Play(place.Tag, place.Frame);

    public void Start(string tag, int actor) => Play(tag, 0);

    /// <summary>
    /// The talking actor's <c>frequency</c>: how fast the line plays, 256 as recorded (iMUSE's pitch shift
    /// for COMI: the track plays at its sample rate × this / 256, so higher is faster and higher pitched).
    /// Outside 0…4095 iMUSE refuses it and the line keeps the one it had. A new line starts at 256.
    /// </summary>
    public int Frequency
    {
        get => _frequency;
        set
        {
            if (value is < 0 or > 4095 || value == _frequency) return;
            _frequency = value;
            _voice?.Speed = value / 256.0;
        }
    }

    private void Play(string tag, long frame)
    {
        Stop();
        ImusSound sound;
        try { sound = _load(tag + ".IMX"); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Game speech {tag}: {ex.Message}");
            return;
        }
        if (sound == null) return;
        _tag = tag;
        _frequency = 256;
        _readAhead = 0;
        _player = new ImusPlayer(sound);
        if (frame > 0) _player.Seek(frame);
        if (Mixer != null)
        {
            _voice = Mixer.Play(_player, MixGroup.Voice);
            _voice.Paused = _paused;
            _voice.Volume = _muted ? 0 : 1;
        }
    }

    /// <summary>
    /// The mouth's width and height in the line's first SYNC block at where it plays plus
    /// <paramref name="aheadMs"/> (ScummVM's getSpeechLipSyncInfo); (0, 0) once the line is over.
    /// </summary>
    public (int Width, int Height) LipSync(int aheadMs)
    {
        var player = _player;
        if (player == null || !IsPlaying) return (0, 0);
        long ms = player.Frame * 1000 / Math.Max(1, player.SampleRate);
        return player.Sound.LipSync(0, (int)Math.Min(int.MaxValue, ms + aheadMs));
    }

    public void Stop()
    {
        _voice?.Stop();
        _voice = null;
        _player = null;
        _tag = null;
    }

    public bool Muted
    {
        get => _muted;
        set
        {
            _muted = value;
            _voice?.Volume = value ? 0 : 1;
        }
    }

    public bool Paused
    {
        get => _paused;
        set
        {
            _paused = value;
            _voice?.Paused = value;
        }
    }

    /// <summary>Once a frame: without a mixer, the line is read for <paramref name="jiffies"/>.</summary>
    public void Update(int jiffies)
    {
        if (Mixer != null || _paused || _player == null) return;
        _readAhead += (double)_player.SampleRate * jiffies / 60 * _frequency / 256;
        int frames = (int)_readAhead;
        _readAhead -= frames;
        int need = frames * _player.Channels;
        if (_scratch.Length < need) _scratch = new float[need];
        _player.Read(_scratch.AsSpan(0, need));
    }

    /// <summary>A loader for the voice bundles (.bun beside <paramref name="gameFile"/>), by entry name, either disc's.</summary>
    public static Func<string, ImusSound> FromBundles(string gameFile)
    {
        var index = new Lazy<Dictionary<string, (string, int)>>(() =>
        {
            var map = new Dictionary<string, (string, int)>(StringComparer.OrdinalIgnoreCase);
            foreach (var bun in GameFiles.Find(gameFile, ".bun"))
            {
                if (AudioExporter.KindOf(bun) == "music") continue;
                using var bundle = Bundle.Open(bun);
                for (int i = 0; i < bundle.Entries.Count; i++) map.TryAdd(bundle.Entries[i].Name, (bun, i));
            }
            return map;
        });
        return name =>
        {
            if (gameFile == null || !index.Value.TryGetValue(name, out var at)) return null;
            using var bundle = Bundle.Open(at.Item1);
            return ImusSound.Parse(BundleCodec.Decompress(bundle.Read(bundle.Entries[at.Item2])));
        };
    }
}
