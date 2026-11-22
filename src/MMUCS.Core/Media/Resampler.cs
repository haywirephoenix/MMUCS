using System;

namespace MMUCS.Core.Media;

/// <summary>
/// Adapts a source to the mixer's rate and to stereo (mono is copied to both sides). Linear
/// interpolation between neighbouring frames; at the same rate it passes samples through unchanged,
/// so a mixer at the game's own 22,050 Hz reproduces the original exactly.
/// </summary>
public sealed class Resampler
{
    private readonly IAudioSource _source;
    private readonly int _channels;
    private readonly double _step;
    private readonly bool _passThrough;
    private float[] _in = new float[0];
    private int _inFrames, _inPos;
    // The frame before the current input block, so interpolation runs across block edges.
    private float _prevL, _prevR, _curL, _curR;
    private double _frac;
    private bool _primed, _ended;

    public Resampler(IAudioSource source, int outputRate)
    {
        _source = source;
        _channels = Math.Max(1, source.Channels);
        _step = (double)source.SampleRate / outputRate;
        _passThrough = source.SampleRate == outputRate;
    }

    public IAudioSource Source => _source;
    /// <summary>True once the source has ended and every frame of it has been read.</summary>
    public bool Ended => _ended;

    /// <summary>Writes up to stereo.Length / 2 stereo frames; fewer means the source ended.</summary>
    public int Read(Span<float> stereo)
    {
        int want = stereo.Length / 2;
        int written = 0;
        while (written < want)
        {
            if (_passThrough)
            {
                if (!NextFrame(out float l, out float r)) break;
                stereo[written * 2] = l;
                stereo[written * 2 + 1] = r;
                written++;
                continue;
            }

            if (!_primed)
            {
                if (!NextFrame(out _prevL, out _prevR)) break;
                if (!NextFrame(out _curL, out _curR)) { _curL = _prevL; _curR = _prevR; }
                _primed = true;
            }
            while (_frac >= 1.0)
            {
                _prevL = _curL; _prevR = _curR;
                if (!NextFrame(out _curL, out _curR)) { _ended = true; return written; }
                _frac -= 1.0;
            }
            float t = (float)_frac;
            stereo[written * 2] = _prevL + (_curL - _prevL) * t;
            stereo[written * 2 + 1] = _prevR + (_curR - _prevR) * t;
            written++;
            _frac += _step;
        }
        if (written < want) _ended = true;
        return written;
    }

    private bool NextFrame(out float l, out float r)
    {
        if (_inPos >= _inFrames)
        {
            // Small reads, so a source's own position (which a picture may follow) stays close to what is mixed.
            if (_in.Length == 0) _in = new float[256 * _channels];
            _inFrames = _source.Read(_in);
            _inPos = 0;
            if (_inFrames <= 0) { l = r = 0; _ended = true; return false; }
        }
        int i = _inPos++ * _channels;
        l = _in[i];
        r = _channels > 1 ? _in[i + 1] : l;
        return true;
    }
}
