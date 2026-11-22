using System;

namespace MMUCS.Core.Media;

/// <summary>
/// Sends a <see cref="Mixer"/>'s output to a sound device. The output pulls blocks from the mixer on its
/// own thread, so sources (and the events they raise) run on that thread.
/// <para>
/// The mixer's <see cref="Mixer.Position"/> runs ahead of what can be heard by however much is queued in
/// the device. <see cref="PlayedFrames"/> is what has actually been played: the clock to follow when
/// picture or game logic must line up with the sound.
/// </para>
/// </summary>
public interface IAudioOutput : IDisposable
{
    /// <summary>The backend and device, for display.</summary>
    string Name { get; }
    Mixer Mixer { get; }
    bool IsRunning { get; }

    void Start();
    void Stop();

    /// <summary>Output frames played so far: the mixer's position minus what is still queued.</summary>
    long PlayedFrames { get; }
    /// <summary>How far the mixer runs ahead of what is heard (the queue length).</summary>
    TimeSpan Latency { get; }
    /// <summary>Times the device ran dry because the mixer didn't keep up (a glitch was heard).</summary>
    int Underruns { get; }
}

public static class AudioOutputExtensions
{
    public static TimeSpan PlayedTime(this IAudioOutput output) =>
        TimeSpan.FromSeconds((double)output.PlayedFrames / output.Mixer.SampleRate);
}
