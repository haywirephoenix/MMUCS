using System;

namespace MMUCS.Core.Media;

/// <summary>
/// A playback position that can run, pause and seek, driven by an output's played clock, so pictures
/// follow the sound as it is heard. The output keeps running while this is paused; only this clock stops.
/// </summary>
public sealed class MediaClock
{
    private readonly IAudioOutput _output;
    private TimeSpan _base;
    private TimeSpan _startedAt;
    private bool _running;

    public MediaClock(IAudioOutput output) => _output = output;

    public bool Running => _running;

    public TimeSpan Position => _running ? _base + (_output.PlayedTime() - _startedAt) : _base;

    public void Play()
    {
        if (_running) return;
        _startedAt = _output.PlayedTime();
        _running = true;
    }

    public void Pause()
    {
        if (!_running) return;
        _base = Position;
        _running = false;
    }

    public void Seek(TimeSpan position)
    {
        _base = position < TimeSpan.Zero ? TimeSpan.Zero : position;
        _startedAt = _output.PlayedTime();
    }
}
