using System;
using System.Diagnostics;
using System.Threading;
using MMUCS.Core.Diagnostics;
using MMUCS.Core.Media;
using Silk.NET.OpenAL;

namespace MMUCS.Media.OpenAL;

/// <summary>
/// Plays a <see cref="Mixer"/> through OpenAL Soft: one streaming source with a short ring of buffers. A
/// feeder thread refills each buffer the device has finished with, so the latency is
/// <c>bufferFrames × bufferCount</c> (about 93 ms with the defaults at 44.1 kHz). The source plays the
/// mix as is (direct channels, gain 1); OpenAL only hands it to the device.
/// </summary>
public sealed unsafe class OpenALOutput : IAudioOutput
{
    // AL_SOFT_direct_channels: play the buffer's channels straight to the speakers, no virtualisation.
    private const int DirectChannelsSoft = 0x1033;

    private readonly ALContext _alc;
    private readonly AL _al;
    private readonly Device* _device;
    private readonly Context* _context;
    private readonly uint _source;
    private readonly uint[] _buffers;
    private readonly int _bufferFrames;
    private readonly float[] _mix;
    private readonly short[] _pcm;
    private readonly object _alLock = new();

    private Thread _thread;
    private volatile bool _running;
    private long _processedFrames; // frames in buffers the device has finished with
    private int _underruns;
    private bool _disposed;

    public OpenALOutput(Mixer mixer, string deviceName = null, int bufferFrames = 1024, int bufferCount = 4)
    {
        Mixer = mixer;
        _bufferFrames = Math.Max(64, bufferFrames);
        _mix = new float[_bufferFrames * Mixer.Channels];
        _pcm = new short[_bufferFrames * Mixer.Channels];

        // true: use OpenAL Soft (the bundled native), not whatever system OpenAL there is.
        _alc = ALContext.GetApi(true);
        _al = AL.GetApi(true);

        _device = _alc.OpenDevice(deviceName);
        if (_device == null) throw new InvalidOperationException($"OpenAL could not open {(deviceName == null ? "the default sound device" : $"\"{deviceName}\"")}");
        _context = _alc.CreateContext(_device, null);
        if (_context == null)
        {
            _alc.CloseDevice(_device);
            throw new InvalidOperationException("OpenAL could not create a context");
        }
        _alc.MakeContextCurrent(_context);

        _source = _al.GenSource();
        _buffers = _al.GenBuffers(Math.Max(2, bufferCount));
        _al.SetSourceProperty(_source, SourceFloat.Gain, 1f);
        _al.SetSourceProperty(_source, SourceBoolean.Looping, false);
        if (_al.IsExtensionPresent("AL_SOFT_direct_channels"))
            _al.SetSourceProperty(_source, (SourceInteger)DirectChannelsSoft, 1);
        CheckError("creating the source");

        string device = _alc.GetContextProperty(_device, GetContextString.DeviceSpecifier);
        Name = $"OpenAL Soft: {device}";
    }

    public string Name { get; }
    public Mixer Mixer { get; }
    public bool IsRunning => _running;
    public int Underruns => _underruns;
    public TimeSpan Latency => TimeSpan.FromSeconds((double)_bufferFrames * _buffers.Length / Mixer.SampleRate);

    public long PlayedFrames
    {
        get
        {
            if (!_running) return Interlocked.Read(ref _processedFrames);
            lock (_alLock)
            {
                MakeCurrent();
                _al.GetSourceProperty(_source, GetSourceInteger.SampleOffset, out int offset);
                return Interlocked.Read(ref _processedFrames) + Math.Max(0, offset);
            }
        }
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_running) return;
        lock (_alLock)
        {
            MakeCurrent();
            foreach (uint b in _buffers) Fill(b);
            _al.SourceQueueBuffers(_source, _buffers);
            _al.SourcePlay(_source);
            CheckError("starting playback");
        }
        _running = true;
        _thread = new Thread(Feed) { IsBackground = true, Name = "MMUCS audio", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _thread?.Join();
        _thread = null;
        lock (_alLock)
        {
            MakeCurrent();
            _al.SourceStop(_source);
            _al.GetSourceProperty(_source, GetSourceInteger.BuffersQueued, out int queued);
            if (queued > 0)
            {
                var done = new uint[queued];
                _al.SourceUnqueueBuffers(_source, done);
            }
        }
    }

    /// <summary>Refills every buffer the device has played, and restarts the source if it ran dry.</summary>
    private void Feed()
    {
        var one = new uint[1];
        int sleepMs = Math.Max(1, (int)(1000.0 * _bufferFrames / Mixer.SampleRate / 4));
        while (_running)
        {
            try
            {
                lock (_alLock)
                {
                    MakeCurrent();
                    _al.GetSourceProperty(_source, GetSourceInteger.BuffersProcessed, out int processed);
                    for (int i = 0; i < processed; i++)
                    {
                        _al.SourceUnqueueBuffers(_source, one);
                        Interlocked.Add(ref _processedFrames, _bufferFrames);
                        Fill(one[0]);
                        _al.SourceQueueBuffers(_source, one);
                    }

                    _al.GetSourceProperty(_source, GetSourceInteger.SourceState, out int state);
                    if (state != (int)SourceState.Playing)
                    {
                        // Ran out before the next buffer arrived: what was queued is played again from here.
                        _underruns++;
                        _al.SourcePlay(_source);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Audio output: {ex.Message}");
                _running = false;
                return;
            }
            Thread.Sleep(sleepMs);
        }
    }

    /// <summary>Mixes the next block into an OpenAL buffer as 16-bit stereo.</summary>
    private void Fill(uint buffer)
    {
        Mixer.Read(_mix);
        for (int i = 0; i < _mix.Length; i++)
            _pcm[i] = (short)Math.Clamp((int)MathF.Round(_mix[i] * 32767f), short.MinValue, short.MaxValue);
        _al.BufferData(buffer, BufferFormat.Stereo16, _pcm, Mixer.SampleRate);
    }

    private void MakeCurrent()
    {
        if (_alc.GetCurrentContext() != _context) _alc.MakeContextCurrent(_context);
    }

    private void CheckError(string what)
    {
        var error = _al.GetError();
        if (error != AudioError.NoError) throw new InvalidOperationException($"OpenAL error {error} while {what}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        lock (_alLock)
        {
            MakeCurrent();
            _al.DeleteSource(_source);
            _al.DeleteBuffers(_buffers);
            _alc.MakeContextCurrent(null);
            _alc.DestroyContext(_context);
            _alc.CloseDevice(_device);
        }
        _al.Dispose();
        _alc.Dispose();
        _disposed = true;
    }
}
