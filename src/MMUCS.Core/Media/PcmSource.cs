using System;
using System.Threading;

namespace MMUCS.Core.Media;

/// <summary>
/// Plays a block of 16-bit (or 8-bit) PCM held in memory, such as a cutscene's sound. Seekable from any
/// thread while it plays; <see cref="Frame"/> says how far the mixer has read it.
/// </summary>
public sealed class PcmSource : IAudioSource
{
    private readonly byte[] _pcm;
    private readonly int _bits, _bytesPerFrame;
    private long _frame;

    public PcmSource(byte[] pcm, int sampleRate, int channels, int bits = 16)
    {
        _pcm = pcm;
        SampleRate = sampleRate;
        Channels = Math.Max(1, channels);
        _bits = bits;
        _bytesPerFrame = Pcm.BytesPerSample(bits) * Channels;
    }

    public int SampleRate { get; }
    public int Channels { get; }
    public long FrameCount => _pcm.Length / _bytesPerFrame;
    /// <summary>The next frame the mixer will read.</summary>
    public long Frame => Interlocked.Read(ref _frame);
    public bool Ended => Frame >= FrameCount;

    public void Seek(long frame) => Interlocked.Exchange(ref _frame, Math.Clamp(frame, 0, FrameCount));

    public int Read(Span<float> buffer)
    {
        long start = Frame;
        int frames = (int)Math.Min(buffer.Length / Channels, FrameCount - start);
        if (frames <= 0) return 0;
        Pcm.ToFloat(_pcm, _bits, start * _bytesPerFrame, buffer, frames * Channels);
        // A seek during the read wins over this advance.
        Interlocked.CompareExchange(ref _frame, start + frames, start);
        return frames;
    }
}
