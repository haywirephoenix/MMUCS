using System;
using System.Buffers.Binary;
using System.IO;

namespace MMUCS.Core.Smush;

/// <summary>
/// A cutscene's sound, decoded from its IACT chunks: 16-bit stereo PCM at 22,050 Hz, from the first
/// frame on, little-endian (as a WAV holds it).
/// </summary>
public sealed record SmushAudioTrack(byte[] Pcm, int SampleRate, int Channels, int Packets, int Escapes, int LeftoverBytes, int Chunks)
{
    public const int Bits = 16;
    public long FrameCount => Pcm.Length / (Channels * 2);
    public TimeSpan Duration => TimeSpan.FromSeconds((double)FrameCount / SampleRate);
}

/// <summary>
/// COMI's cutscene audio. Every audio IACT chunk (code 8, flags 46) carries an 18-byte header (code,
/// flags, two zero words, track, index, frame count, a byte count), then part of one continuous stream:
/// the payloads join in order, across chunks and track numbers, into packets. A packet is a big-endian
/// length, a byte with two 4-bit shifts (high nibble left, low nibble right), then 1024 stereo frames,
/// left sample first. Each sample is a signed byte shifted up by its channel's shift, except 0x80, which
/// is followed by the sample itself as a big-endian 16-bit word. So a packet is 4096 bytes of sound,
/// 1024 frames at 22,050 Hz.
/// </summary>
public static class SmushAudio
{
    public const int SampleRate = 22050;
    public const int FramesPerPacket = 1024;
    private const int HeaderSize = 18;
    private const int AudioCode = 8, AudioFlags = 46;

    /// <summary>Decodes all of a video's sound, or returns null when it has none.</summary>
    public static SmushAudioTrack Decode(SmushVideo video)
    {
        // Join the payloads into the stream they were cut from.
        using var stream = new MemoryStream();
        int chunks = 0;
        var data = video.Data;
        foreach (var frame in video.Frames)
            foreach (var chunk in frame.Children(data))
            {
                if (chunk.Tag != SmushTag.IACT || chunk.Length < HeaderSize) continue;
                var c = chunk.Data(data);
                if (BinaryPrimitives.ReadUInt16LittleEndian(c) != AudioCode || BinaryPrimitives.ReadUInt16LittleEndian(c[2..]) != AudioFlags) continue;
                stream.Write(c[HeaderSize..]);
                chunks++;
            }
        if (chunks == 0) return null;

        var input = stream.GetBuffer().AsSpan(0, (int)stream.Length);
        using var pcm = new MemoryStream(input.Length * 2);
        Span<byte> packet = stackalloc byte[FramesPerPacket * 4];
        int pos = 0, packets = 0, escapes = 0;
        while (pos + 2 <= input.Length)
        {
            int length = BinaryPrimitives.ReadUInt16BigEndian(input[pos..]);
            if (pos + 2 + length > input.Length) break; // a cut-off last packet
            escapes += DecodePacket(input.Slice(pos + 2, length), packet);
            pcm.Write(packet);
            pos += 2 + length;
            packets++;
        }
        return new SmushAudioTrack(pcm.ToArray(), SampleRate, 2, packets, escapes, input.Length - pos, chunks);
    }

    /// <summary>Decodes one packet (after its length) into 1024 stereo frames; returns how many samples were escaped.</summary>
    private static int DecodePacket(ReadOnlySpan<byte> p, Span<byte> output)
    {
        output.Clear();
        if (p.Length == 0) return 0;
        int leftShift = p[0] >> 4, rightShift = p[0] & 0x0F;
        int pos = 1, o = 0, escapes = 0;
        for (int i = 0; i < FramesPerPacket * 2 && pos < p.Length; i++)
        {
            int shift = (i & 1) == 0 ? leftShift : rightShift;
            byte b = p[pos++];
            short sample;
            if (b == 0x80)
            {
                sample = pos + 2 <= p.Length ? BinaryPrimitives.ReadInt16BigEndian(p[pos..]) : (short)0;
                pos += 2;
                escapes++;
            }
            // The original keeps the low 16 bits of the shifted byte.
            else sample = unchecked((short)((sbyte)b << shift));
            BinaryPrimitives.WriteInt16LittleEndian(output[o..], sample);
            o += 2;
        }
        return escapes;
    }
}
