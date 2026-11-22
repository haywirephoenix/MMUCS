using System;
using System.Diagnostics;
using System.Threading;

namespace MMUCS.Core.Media;

/// <summary>
/// An output without a device: pulls the mixer in real time and throws the sound away. Used when no sound
/// device can be opened, so everything that follows the played clock (cutscenes, the game) still runs
/// at the right speed.
/// </summary>
public sealed class NullAudioOutput : IAudioOutput
{
    // 5 ms blocks, so the played clock moves in small steps at any rate.
    private readonly int _blockFrames;
    private readonly float[] _block;
    private Thread _thread;
    private volatile bool _running;
    private long _played;

    public NullAudioOutput(Mixer mixer, string reason = null)
    {
        Mixer = mixer;
        _blockFrames = Math.Max(16, mixer.SampleRate / 200);
        _block = new float[_blockFrames * Mixer.Channels];
        Name = reason == null ? "No sound device" : $"No sound device ({reason})";
    }

    public string Name { get; }
    public Mixer Mixer { get; }
    public bool IsRunning => _running;
    public long PlayedFrames => Interlocked.Read(ref _played);
    public TimeSpan Latency => TimeSpan.Zero;
    public int Underruns => 0;

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(Run) { IsBackground = true, Name = "MMUCS silent audio" };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join();
        _thread = null;
    }

    private void Run()
    {
        var clock = Stopwatch.StartNew();
        long startFrames = PlayedFrames;
        while (_running)
        {
            long due = startFrames + (long)(clock.Elapsed.TotalSeconds * Mixer.SampleRate);
            while (PlayedFrames + _blockFrames <= due)
            {
                Mixer.Read(_block);
                Interlocked.Add(ref _played, _blockFrames);
            }
            Thread.Sleep(2);
        }
    }

    public void Dispose() => Stop();
}
