using System;
using System.Collections.Generic;
using MMUCS.Core.Audio;

namespace MMUCS.Core.Media;

/// <summary>Something <see cref="ImusPlayer"/> did, in the sound's sample frames (from the start of its data).</summary>
public readonly record struct ImusEvent(ImusEvent.EventKind Kind, long Frame, long ToFrame = 0, string Text = null, int Hook = 0)
{
    public enum EventKind
    {
        /// <summary>A region starts at <see cref="Frame"/>.</summary>
        Region,
        /// <summary>A marker (TEXT) was passed: the scripts' trigger point.</summary>
        Marker,
        /// <summary>Jumped from <see cref="Frame"/> to <see cref="ToFrame"/>, crossfading for <see cref="Hook"/>'s jump.</summary>
        Jump,
        /// <summary>A jump at <see cref="Frame"/> was passed without jumping (its hook didn't match).</summary>
        JumpSkipped,
        /// <summary>Playback ended (STOP, or nothing more to play).</summary>
        Stop,
    }
}

/// <summary>
/// Plays a Digital iMUSE sound the way the game does. Playback runs region by region; where a region
/// ends, the map's events at that position are handled in map order:
/// <list type="bullet">
/// <item>a marker (TEXT) is reported (<see cref="EventRaised"/>): it's what the scripts' triggers wait for;</item>
/// <item>a jump moves playback to its destination when its hook allows (<see cref="Hook"/>), crossfading
/// from the old position over the jump's fade time;</item>
/// <item>a region carries on playing; a stop ends the sound.</item>
/// </list>
/// Hooks follow iMUSE: a jump with hook 0 always jumps unless the track's hook is 128 (which skips it
/// once: "leave the loop"); a jump with hook N jumps only when the track's hook is N. Either way a hook
/// that was used goes back to 0. The game sets hooks from its scripts; here anyone can set <see cref="Hook"/>.
/// </summary>
public sealed class ImusPlayer : IAudioSource
{
    /// <summary>The track hook that makes a hook-0 jump fall through instead (iMUSE's "exit loop").</summary>
    public const int ExitLoopHook = 128;
    private const int MaxEventsPerStep = 256;

    private readonly ImusSound _sound;
    private readonly int _bpf, _bytesPerSample;
    private readonly long _dataStart, _dataEnd;
    private long _offset;     // byte offset in the iMUS file of the next sample
    private long _remaining;  // bytes left in the current region
    private bool _finished;
    private volatile int _hook;
    // Read runs on the audio thread; Seek and Restart may come from the UI.
    private readonly object _gate = new();

    // Crossfade after a jump: the old position keeps playing and fades out while the new one fades in.
    private long _fadeOffset;
    private long _fadeTotal, _fadeDone;
    private float[] _fadeBuffer = [];

    public ImusPlayer(ImusSound sound)
    {
        _sound = sound;
        _bytesPerSample = Pcm.BytesPerSample(sound.Bits);
        _bpf = _bytesPerSample * Math.Max(1, sound.Channels);
        _dataStart = sound.DataOffset;
        _dataEnd = sound.DataOffset + (long)sound.DataLength;
        RestartLocked();
    }

    public ImusSound Sound => _sound;
    public int SampleRate => _sound.SampleRate;
    public int Channels => _sound.Channels;

    /// <summary>The track's jump hook (iMUSE's setHook). Safe to set from any thread.</summary>
    public int Hook { get => _hook; set => _hook = value; }

    /// <summary>Where playback is, in sample frames from the start of the data.</summary>
    public long Frame => (_offset - _dataStart) / _bpf;
    public bool Finished => _finished;
    /// <summary>A crossfade from a jump is playing.</summary>
    public bool Crossfading => _fadeDone < _fadeTotal;

    /// <summary>Raised on the thread that reads the player (the audio thread when it plays live).</summary>
    public event Action<ImusEvent> EventRaised;

    /// <summary>Starts again from the beginning, as the game starts a sound.</summary>
    public void Restart()
    {
        lock (_gate) RestartLocked();
    }

    private void RestartLocked()
    {
        _finished = false;
        _remaining = 0;
        _fadeTotal = _fadeDone = 0;
        // The first read handles the events at the start, so a handler attached after construction sees them.
        _offset = _sound.Regions.Count > 0 ? _sound.Regions[0].Offset : _dataStart;
    }

    /// <summary>
    /// Jumps straight to the region holding <paramref name="frame"/> and plays on from that frame, without
    /// a crossfade (for a timeline). Returns false when no region holds it.
    /// </summary>
    public bool Seek(long frame)
    {
        lock (_gate) return SeekLocked(frame);
    }

    private bool SeekLocked(long frame)
    {
        long target = _dataStart + frame * _bpf;
        foreach (var r in _sound.Regions)
        {
            if (target < r.Offset || target >= r.Offset + r.Length) continue;
            _finished = false;
            _fadeTotal = _fadeDone = 0;
            _offset = target;
            _remaining = r.Offset + r.Length - target;
            return true;
        }
        return false;
    }

    public int Read(Span<float> buffer)
    {
        lock (_gate) return ReadLocked(buffer);
    }

    private int ReadLocked(Span<float> buffer)
    {
        int channels = Math.Max(1, _sound.Channels);
        int want = buffer.Length / channels;
        int written = 0;

        while (written < want && !_finished)
        {
            if (_remaining < _bpf)
            {
                _offset += _remaining;
                _remaining = 0;
                Navigate();
                continue;
            }

            int frames = (int)Math.Min(want - written, _remaining / _bpf);
            var dst = buffer.Slice(written * channels, frames * channels);
            Pcm.ToFloat(_sound.Bytes, _sound.Bits, _offset, dst, frames * channels);
            if (Crossfading) MixCrossfade(dst, frames, channels);

            _offset += (long)frames * _bpf;
            _remaining -= (long)frames * _bpf;
            written += frames;
        }
        return written;
    }

    /// <summary>
    /// Mixes the fading-out old audio under the new: the new fades in as the old fades out, linearly,
    /// over the jump's fade time (iMUSE's crossfade).
    /// </summary>
    private void MixCrossfade(Span<float> dst, int frames, int channels)
    {
        int n = (int)Math.Min(frames, _fadeTotal - _fadeDone);
        if (_fadeBuffer.Length < n * channels) _fadeBuffer = new float[n * channels];
        var old = _fadeBuffer.AsSpan(0, n * channels);
        Pcm.ToFloat(_sound.Bytes, _sound.Bits, _fadeOffset, old, n * channels);
        for (int i = 0; i < n; i++)
        {
            float oldGain = 1f - (float)(_fadeDone + i) / _fadeTotal;
            for (int c = 0; c < channels; c++)
            {
                int k = i * channels + c;
                dst[k] = dst[k] * (1f - oldGain) + old[k] * oldGain;
            }
        }
        _fadeDone += n;
        _fadeOffset += (long)n * _bpf;
    }

    /// <summary>Handles the map's events at the current position until a region starts or the sound ends.</summary>
    private void Navigate()
    {
        var events = _sound.Events;
        int handled = 0;
        int start = 0;
        while (handled++ < MaxEventsPerStep)
        {
            int found = -1;
            for (int i = start; i < events.Count; i++)
                if (events[i].Offset == _offset) { found = i; break; }
            if (found < 0) { Finish(); return; }
            var e = events[found];
            start = found + 1;

            switch (e.Kind)
            {
                case ImusSound.EventKind.Marker:
                    Raise(new ImusEvent(ImusEvent.EventKind.Marker, ToFrame(e.Offset), Text: _sound.Markers[e.Index].Text));
                    break;
                case ImusSound.EventKind.Sync:
                    break;
                case ImusSound.EventKind.Stop:
                    Finish();
                    return;
                case ImusSound.EventKind.Region:
                {
                    var r = _sound.Regions[e.Index];
                    _remaining = Math.Max(0, Math.Min(r.Offset + r.Length, _dataEnd) - _offset);
                    Raise(new ImusEvent(ImusEvent.EventKind.Region, ToFrame(r.Offset)));
                    return;
                }
                case ImusSound.EventKind.Jump:
                {
                    var j = _sound.Jumps[e.Index];
                    if (!TakeHook(j.Hook))
                    {
                        Raise(new ImusEvent(ImusEvent.EventKind.JumpSkipped, ToFrame(j.Offset), ToFrame(j.Destination), Hook: j.Hook));
                        break;
                    }
                    StartCrossfade(j);
                    Raise(new ImusEvent(ImusEvent.EventKind.Jump, ToFrame(j.Offset), ToFrame(j.Destination), Hook: j.Hook));
                    _offset = j.Destination;
                    start = 0; // the destination's events, from the top of the map
                    break;
                }
            }
        }
        Finish();
    }

    /// <summary>iMUSE's hook rule: whether this jump is taken, consuming the track's hook when used.</summary>
    private bool TakeHook(int jumpHook)
    {
        int track = _hook;
        if (jumpHook != 0)
        {
            if (track != jumpHook) return false;
            _hook = 0;
            return true;
        }
        if (track == ExitLoopHook)
        {
            _hook = 0;
            return false;
        }
        return true;
    }

    /// <summary>
    /// The old audio carries on from the jump point for the fade time (an even number of frames, as the
    /// original rounds it), never past the end of the data.
    /// </summary>
    private void StartCrossfade(ImusSound.Jump j)
    {
        long frames = ((long)_sound.SampleRate * j.FadeMs / 1000) & ~1L;
        long available = Math.Max(0, (_dataEnd - j.Offset) / _bpf);
        _fadeTotal = Math.Min(frames, available);
        _fadeDone = 0;
        _fadeOffset = j.Offset;
    }

    private void Finish()
    {
        if (_finished) return;
        _finished = true;
        _remaining = 0;
        Raise(new ImusEvent(ImusEvent.EventKind.Stop, ToFrame(_offset)));
    }

    private long ToFrame(long offset)
    {
        return (offset - _dataStart) / _bpf;
    }

    private void Raise(ImusEvent e)
    {
        EventRaised?.Invoke(e);
    }
}
