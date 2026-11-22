using System;
using System.Buffers.Binary;

namespace MMUCS.Core.Audio;

/// <summary>
/// Undoes a bundle entry's COMP compression. A COMP entry is a table of blocks, each decoding to
/// 0x2000 bytes of the original file (the last to however many the header says); the result is the
/// iMUS sound as it was before packing, header and all. COMI uses two block codecs, both an IMA ADPCM
/// variant with a variable number of bits per sample (see <see cref="ImcAdpcm"/>): 13 for mono (speech)
/// and 15 for stereo (music). An entry that already starts with iMUS is stored as is.
/// </summary>
public static class BundleCodec
{
    public const int BlockSize = 0x2000;
    private const uint COMP = 0x434F4D50, IMUS = 0x694D5553;

    /// <summary>What decompressing an entry found, for checking the decoder. OverReads: the most bytes a full block's bit reader wanted past its input.</summary>
    public readonly record struct Stats(int Blocks, int Codec, int OverReads);

    public static byte[] Decompress(ReadOnlySpan<byte> entry) => Decompress(entry, out _);

    /// <summary>
    /// Decompresses only the first block (the iMUS header and map, then the first samples), and gives the
    /// size the whole sound decompresses to: enough to list a sound's title and length cheaply.
    /// </summary>
    public static byte[] DecompressFirstBlock(ReadOnlySpan<byte> entry, out long fullLength)
    {
        uint tag = entry.Length >= 4 ? BinaryPrimitives.ReadUInt32BigEndian(entry) : 0;
        if (tag == IMUS) { fullLength = entry.Length; return entry[..Math.Min(entry.Length, BlockSize)].ToArray(); }
        if (tag != COMP || entry.Length < 32) throw new NotSupportedException("bundle entry is not COMP or iMUS");
        int blocks = (int)BinaryPrimitives.ReadUInt32BigEndian(entry[4..]);
        int lastSize = (int)BinaryPrimitives.ReadUInt32BigEndian(entry[12..]);
        if (lastSize <= 0 || lastSize > BlockSize) lastSize = BlockSize;
        fullLength = (long)(blocks - 1) * BlockSize + lastSize;

        var row = entry.Slice(16, 16);
        int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(row);
        int size = (int)BinaryPrimitives.ReadUInt32BigEndian(row[4..]);
        int codec = (int)BinaryPrimitives.ReadUInt32BigEndian(row[8..]);
        var block = new byte[BlockSize];
        var input = entry.Slice(offset, Math.Min(size, entry.Length - offset));
        if (codec is 13 or 15) ImcAdpcm.DecodeBlock(input, codec == 13 ? 1 : 2, block);
        else if (codec == 0) input[..Math.Min(input.Length, BlockSize)].CopyTo(block);
        else throw new NotSupportedException($"bundle block codec {codec}");
        return blocks == 1 ? block.AsSpan(0, lastSize).ToArray() : block;
    }

    public static byte[] Decompress(ReadOnlySpan<byte> entry, out Stats stats)
    {
        stats = default;
        if (entry.Length < 16) throw new ArgumentException("bundle entry is too short");
        uint tag = BinaryPrimitives.ReadUInt32BigEndian(entry);
        if (tag == IMUS) return entry.ToArray();
        if (tag != COMP) throw new NotSupportedException($"bundle entry starts with 0x{tag:X8}, not COMP or iMUS");

        int blocks = (int)BinaryPrimitives.ReadUInt32BigEndian(entry[4..]);
        int lastSize = (int)BinaryPrimitives.ReadUInt32BigEndian(entry[12..]);
        if (blocks <= 0 || 16 + blocks * 16 > entry.Length) throw new ArgumentException($"bad COMP block table ({blocks} blocks)");
        if (lastSize <= 0 || lastSize > BlockSize) lastSize = BlockSize;

        var output = new byte[(blocks - 1) * BlockSize + lastSize];
        var block = new byte[BlockSize];
        int codecSeen = -1, overReads = 0;
        for (int i = 0; i < blocks; i++)
        {
            var row = entry.Slice(16 + i * 16, 16);
            int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(row);
            int size = (int)BinaryPrimitives.ReadUInt32BigEndian(row[4..]);
            int codec = (int)BinaryPrimitives.ReadUInt32BigEndian(row[8..]);
            if (offset < 0 || offset + size > entry.Length) throw new ArgumentException($"block {i} runs past the entry");
            var input = entry.Slice(offset, size);

            switch (codec)
            {
                case 0:
                    block.AsSpan().Clear();
                    input[..Math.Min(size, BlockSize)].CopyTo(block);
                    break;
                case 13:
                case 15:
                {
                    int over = ImcAdpcm.DecodeBlock(input, codec == 13 ? 1 : 2, block);
                    // The last block decodes a whole block's worth from a shorter input; only full ones count.
                    if (i < blocks - 1) overReads = Math.Max(overReads, over);
                }
                    break;
                default:
                    throw new NotSupportedException($"bundle block codec {codec}");
            }
            codecSeen = codec;
            int n = i == blocks - 1 ? lastSize : BlockSize;
            block.AsSpan(0, n).CopyTo(output.AsSpan(i * BlockSize));
        }
        stats = new Stats(blocks, codecSeen, overReads);
        return output;
    }
}

/// <summary>
/// The ADPCM of COMI's bundles. Like IMA ADPCM it keeps a predicted sample and a step index per channel
/// and adds a scaled step for each code, but each code is 2 to 7 bits wide, chosen by the current step
/// index, and the bits are read as one big-endian stream. A block holds 0x1000 16-bit samples, channels
/// coded one after the other and written interleaved. It starts with a 16-bit word: if non-zero, that
/// many bytes of uncoded data come first (the first block's iMUS header); if zero, each channel's
/// starting step index (1 byte, then 4 unused) and sample (4 bytes) follow.
/// </summary>
public static class ImcAdpcm
{
    private const int SamplesPerBlock = BundleCodec.BlockSize / 2;

    /// <summary>The standard IMA ADPCM step sizes.</summary>
    private static readonly int[] Steps =
    {
        7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45,
        50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130, 143, 157, 173, 190, 209, 230, 253, 279, 307,
        337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066,
        2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899,
        15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767,
    };

    /// <summary>How far the step index moves for each code's magnitude, per code width (2–7 bits).</summary>
    private static readonly sbyte[][] IndexMoves =
    {
        new sbyte[] { -1, 4 },
        new sbyte[] { -1, -1, 2, 8 },
        new sbyte[] { -1, -1, -1, -1, 1, 2, 4, 6 },
        new sbyte[] { -1, -1, -1, -1, -1, -1, -1, -1, 1, 2, 4, 6, 8, 12, 16, 32 },
        Moves(16, new sbyte[] { 1, 2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 22, 24, 26, 28, 32 }),
        Moves(32, Range(1, 32)),
    };

    /// <summary>Code width in bits for each step index: grows with the step, kept within 2–7.</summary>
    private static readonly int[] Width = new int[Steps.Length];
    /// <summary>
    /// For each step index and 6-bit magnitude, the delta those magnitude bits add: bit 5 adds the whole
    /// step, bit 4 half of it, and so on (integer halving, as the original).
    /// </summary>
    private static readonly int[] Magnitude = new int[Steps.Length * 64];

    static ImcAdpcm()
    {
        for (int i = 0; i < Steps.Length; i++)
        {
            int bits = 1;
            for (int v = Steps[i] * 4 / 7 / 2; v != 0; v /= 2) bits++;
            Width[i] = Math.Clamp(bits, 3, 8) - 1;

            for (int m = 0; m < 64; m++)
            {
                int sum = 0, step = Steps[i];
                for (int bit = 32; bit != 0; bit >>= 1, step >>= 1)
                    if ((m & bit) != 0) sum += step;
                Magnitude[i * 64 + m] = sum;
            }
        }
    }

    /// <summary>
    /// Decodes one block into <paramref name="output"/> (0x2000 bytes, 16-bit little-endian).
    /// Returns how many bytes the bit reader wanted past the end of the input (the original reads one
    /// zero byte past the end); a correct stream gives 0 or 1.
    /// </summary>
    public static int DecodeBlock(ReadOnlySpan<byte> input, int channels, Span<byte> output)
    {
        output.Clear();
        int pos = 0, dst = 0;
        int samples = SamplesPerBlock;
        Span<int> startIndex = stackalloc int[2];
        Span<int> startSample = stackalloc int[2];

        int raw = Be16(input, 0);
        pos = 2;
        if (raw != 0)
        {
            int n = Math.Min(raw, Math.Min(input.Length - pos, output.Length));
            input.Slice(pos, n).CopyTo(output);
            pos += raw;
            dst = raw;
            samples -= raw / 2;
        }
        else
        {
            for (int c = 0; c < channels; c++)
            {
                startIndex[c] = pos < input.Length ? input[pos] : 0;
                startSample[c] = pos + 9 <= input.Length ? BinaryPrimitives.ReadInt32BigEndian(input.Slice(pos + 5, 4)) : 0;
                pos += 9;
            }
        }

        var bitsIn = input[Math.Min(pos, input.Length)..];
        long bitPos = 0;
        int overRead = 0;

        for (int c = 0; c < channels; c++)
        {
            int index = Math.Clamp(startIndex[c], 0, Steps.Length - 1);
            int sample = startSample[c];
            int count = channels == 1 ? samples : c == 0 ? (samples + 1) / 2 : samples / 2;
            int o = dst + c * 2;

            for (int i = 0; i < count; i++)
            {
                int width = Width[index];
                int at = (int)(bitPos >> 3);
                if (at + 2 > bitsIn.Length) overRead = Math.Max(overRead, at + 2 - bitsIn.Length);
                int word = (Byte(bitsIn, at) << 8 | Byte(bitsIn, at + 1)) << (int)(bitPos & 7) & 0xFFFF;
                int code = word >> (16 - width);
                bitPos += width;

                int sign = 1 << (width - 1);
                int magnitude = code & (sign - 1);
                int delta = (Steps[index] >> (width - 1)) + Magnitude[index * 64 + (magnitude << (7 - width))];
                if ((code & sign) != 0) delta = -delta;
                sample = Math.Clamp(sample + delta, short.MinValue, short.MaxValue);

                if (o + 1 < output.Length) BinaryPrimitives.WriteInt16LittleEndian(output[o..], (short)sample);
                o += channels * 2;

                index = Math.Clamp(index + IndexMoves[width - 2][magnitude], 0, Steps.Length - 1);
            }
        }
        return overRead;
    }

    private static int Be16(ReadOnlySpan<byte> s, int o) => o + 2 <= s.Length ? (s[o] << 8) | s[o + 1] : 0;
    private static int Byte(ReadOnlySpan<byte> s, int o) => o < s.Length ? s[o] : 0;

    private static sbyte[] Moves(int lowerCount, sbyte[] upper)
    {
        var row = new sbyte[lowerCount + upper.Length];
        Array.Fill(row, (sbyte)-1, 0, lowerCount);
        upper.CopyTo(row, lowerCount);
        return row;
    }

    private static sbyte[] Range(int from, int to)
    {
        var r = new sbyte[to - from + 1];
        for (int i = 0; i < r.Length; i++) r[i] = (sbyte)(from + i);
        return r;
    }
}
