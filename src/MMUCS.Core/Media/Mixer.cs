using System;
using System.Collections.Generic;

namespace MMUCS.Core.Media;

/// <summary>The mixer groups, each with its own volume (the game's music, speech and effects sliders).</summary>
public enum MixGroup { Music, Voice, Effects, Video }

/// <summary>
/// One sound playing in the <see cref="Mixer"/>: its volume, pan and fades. Safe to change from any thread;
/// changes apply from the next mixed block.
/// </summary>
public sealed class MixerVoice
{
    internal readonly Resampler Resampler;
    private volatile bool _stopRequested;
    private float _volume = 1f, _gain, _fadeFrom, _fadeTo;
    private long _fadeFrames, _fadeDone;
    private readonly object _lock = new();

    internal MixerVoice(IAudioSource source, int outputRate, MixGroup group, float volume, float pan, int fadeInMs)
    {
        Resampler = new Resampler(source, outputRate);
        Group = group;
        Pan = pan;
        _volume = volume;
        _gain = fadeInMs > 0 ? 0f : 1f;
        if (fadeInMs > 0) StartFade(1f, fadeInMs, outputRate);
        OutputRate = outputRate;
    }

    private int OutputRate { get; }
    public IAudioSource Source => Resampler.Source;
    public MixGroup Group { get; }
    /// <summary>0…1 (more amplifies).</summary>
    public float Volume { get => _volume; set => _volume = Math.Max(0f, value); }
    /// <summary>−1 (left) … 1 (right).</summary>
    public float Pan { get; set; }
    /// <summary>True once it has ended or been stopped and the mixer has dropped it.</summary>
    public bool Finished { get; internal set; }
    /// <summary>While paused the mixer skips it: its source isn't read, so it resumes where it was.</summary>
    public bool Paused { get; set; }

    /// <summary>Stops the sound, fading it out over <paramref name="fadeMs"/> first.</summary>
    public void Stop(int fadeMs = 0)
    {
        if (fadeMs <= 0) { _stopRequested = true; return; }
        StartFade(0f, fadeMs, OutputRate);
    }

    /// <summary>Fades the sound's fade gain (on top of <see cref="Volume"/>) to <paramref name="target"/>.</summary>
    public void FadeTo(float target, int ms)
    {
        StartFade(target, ms, OutputRate);
    }

    private void StartFade(float target, int ms, int rate)
    {
        lock (_lock)
        {
            _fadeFrom = _gain;
            _fadeTo = target;
            _fadeFrames = Math.Max(1, (long)rate * ms / 1000);
            _fadeDone = 0;
        }
    }

    /// <summary>Adds up to frames of this voice into <paramref name="mix"/>. Returns false once it is over.</summary>
    internal bool MixInto(Span<float> mix, Span<float> scratch, int frames, float groupVolume)
    {
        if (_stopRequested) return false;
        if (Paused) return true;
        int got = Resampler.Read(scratch[..(frames * 2)]);

        // Equal-power pan: centre is −3 dB on each side.
        float angle = (Math.Clamp(Pan, -1f, 1f) + 1f) * MathF.PI / 4f;
        float panL = MathF.Cos(angle) * MathF.Sqrt(2f), panR = MathF.Sin(angle) * MathF.Sqrt(2f);
        float vol = _volume * groupVolume;

        lock (_lock)
        {
            for (int i = 0; i < got; i++)
            {
                if (_fadeDone < _fadeFrames)
                {
                    _fadeDone++;
                    _gain = _fadeFrom + (_fadeTo - _fadeFrom) * _fadeDone / _fadeFrames;
                }
                float g = vol * _gain;
                mix[i * 2] += scratch[i * 2] * g * panL;
                mix[i * 2 + 1] += scratch[i * 2 + 1] * g * panR;
            }
            if (_fadeDone >= _fadeFrames && _fadeTo <= 0f && _fadeFrames > 0) return false;
        }
        return got == frames && !Resampler.Ended;
    }
}

/// <summary>
/// Mixes every playing sound into stereo float at one output rate. The output (a sound card, or a file
/// when rendering offline) pulls blocks with <see cref="Read"/>; how many frames it has pulled is the
/// media clock everything else (cutscene frames, game time) follows. Play and stop from any thread.
/// </summary>
public sealed class Mixer(int sampleRate = 44100)
{
    private readonly List<MixerVoice> _voices = [];
    private readonly object _lock = new();
    private readonly float[] _groupVolume = [1f, 1f, 1f, 1f];
    private float[] _scratch = new float[4096];
    // Reused every block, so mixing allocates nothing (it runs on the audio thread ~43 times a second).
    private MixerVoice[] _mixing = new MixerVoice[8];
    private readonly List<MixerVoice> _ended = [];
    private long _position;

    public int SampleRate { get; } = sampleRate;
    public const int Channels = 2;
    public float MasterVolume { get; set; } = 1f;

    /// <summary>Frames mixed so far: the media clock, in output frames.</summary>
    public long Position => System.Threading.Interlocked.Read(ref _position);
    public TimeSpan Time => TimeSpan.FromSeconds((double)Position / SampleRate);

    /// <summary>Samples that had to be clipped to −1…1 so far (the mix got too loud).</summary>
    public long ClippedSamples { get; private set; }

    public float GetGroupVolume(MixGroup g)
    {
        return _groupVolume[(int)g];
    }

    public void SetGroupVolume(MixGroup g, float v)
    {
        _groupVolume[(int)g] = Math.Max(0f, v);
    }

    public int VoiceCount { get { lock (_lock) return _voices.Count; } }

    public MixerVoice Play(IAudioSource source, MixGroup group, float volume = 1f, float pan = 0f, int fadeInMs = 0)
    {
        var voice = new MixerVoice(source, SampleRate, group, volume, pan, fadeInMs);
        lock (_lock) _voices.Add(voice);
        return voice;
    }

    public void StopAll()
    {
        lock (_lock)
        {
            foreach (var v in _voices) v.Finished = true;
            _voices.Clear();
        }
    }

    /// <summary>Mixes the next block: fills all of <paramref name="stereo"/> (silence when nothing plays).</summary>
    public void Read(Span<float> stereo)
    {
        stereo.Clear();
        int frames = stereo.Length / Channels;
        if (_scratch.Length < frames * 2) _scratch = new float[frames * 2];

        // The voices as they are now: Play and StopAll may change the list while this block mixes.
        int count;
        lock (_lock)
        {
            count = _voices.Count;
            if (_mixing.Length < count) _mixing = new MixerVoice[Math.Max(count, _mixing.Length * 2)];
            _voices.CopyTo(_mixing);
        }

        // A voice writes the scratch frames it then reads, so the scratch needs no clearing.
        for (int i = 0; i < count; i++)
        {
            var v = _mixing[i];
            if (!v.MixInto(stereo, _scratch, frames, _groupVolume[(int)v.Group]))
                _ended.Add(v);
        }
        Array.Clear(_mixing, 0, count); // don't keep stopped voices (and their sound) alive
        if (_ended.Count > 0)
        {
            lock (_lock)
                foreach (var v in _ended) { _voices.Remove(v); v.Finished = true; }
            _ended.Clear();
        }

        float master = MasterVolume;
        long clipped = 0;
        for (int i = 0; i < stereo.Length; i++)
        {
            float s = stereo[i] * master;
            if (s > 1f) { s = 1f; clipped++; }
            else if (s < -1f) { s = -1f; clipped++; }
            stereo[i] = s;
        }
        ClippedSamples += clipped;
        System.Threading.Interlocked.Add(ref _position, frames);
    }
}
