using System;

namespace MMUCS.Core.Media;

/// <summary>
/// Something that produces audio: float samples in −1…1, interleaved by channel, at its own rate.
/// <see cref="Mixer"/> resamples every source to the output rate, so a source never has to.
/// </summary>
public interface IAudioSource
{
    int SampleRate { get; }
    int Channels { get; }

    /// <summary>
    /// Fills <paramref name="buffer"/> with up to buffer.Length / Channels frames and returns how many it
    /// wrote. Fewer than asked means the source has ended.
    /// </summary>
    int Read(Span<float> buffer);
}

/// <summary>Converts the game's PCM (8-bit unsigned or 16-bit signed little-endian) to float samples.</summary>
public static class Pcm
{
    public static int BytesPerSample(int bits) => bits <= 8 ? 1 : 2;

    /// <summary>Converts <paramref name="count"/> samples starting at byte <paramref name="offset"/>; bytes past the end read as silence.</summary>
    public static void ToFloat(ReadOnlySpan<byte> pcm, int bits, long offset, Span<float> output, int count)
    {
        if (bits <= 8)
        {
            for (int i = 0; i < count; i++)
            {
                long o = offset + i;
                output[i] = o >= 0 && o < pcm.Length ? (pcm[(int)o] - 128) / 128f : 0f;
            }
            return;
        }
        for (int i = 0; i < count; i++)
        {
            long o = offset + i * 2L;
            output[i] = o >= 0 && o + 1 < pcm.Length ? (short)(pcm[(int)o] | (pcm[(int)o + 1] << 8)) / 32768f : 0f;
        }
    }
}

/// <summary>A source over a block of float samples already in memory (tests, short sounds).</summary>
public sealed class BufferSource(float[] samples, int sampleRate, int channels) : IAudioSource
{
    private int _pos;
    public int SampleRate { get; } = sampleRate;
    public int Channels { get; } = channels;

    public int Read(Span<float> buffer)
    {
        int frames = Math.Min(buffer.Length / Channels, (samples.Length - _pos) / Channels);
        samples.AsSpan(_pos, frames * Channels).CopyTo(buffer);
        _pos += frames * Channels;
        return frames;
    }
}
