using System;
using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;

namespace MMUCS.Core.Audio;

/// <summary>
/// A FLAC stream (for music mods: soundtrack releases come as FLAC). After "fLaC" come metadata blocks, of
/// which only STREAMINFO matters here (rate, channels, bits, length and the MD5 of the decoded audio), then
/// frames. Each frame holds one block of samples per channel as subframes: a constant, verbatim samples, or
/// a fixed or LPC predictor with Rice-coded residuals. Stereo frames may store side channels (left/side,
/// side/right, mid/side), which are undone after decoding.
/// </summary>
public sealed class Flac
{
    private readonly byte[] _file;
    private readonly int _framesAt;

    public int SampleRate { get; }
    public int Channels { get; }
    public int BitsPerSample { get; }
    /// <summary>Sample frames in the stream (per channel), 0 when STREAMINFO doesn't say.</summary>
    public long TotalFrames { get; }
    /// <summary>The MD5 of the decoded samples as STREAMINFO gives it (all zero when the encoder left it out).</summary>
    public byte[] Md5 { get; }

    public TimeSpan Duration => SampleRate == 0 ? TimeSpan.Zero : TimeSpan.FromSeconds((double)TotalFrames / SampleRate);

    private Flac(byte[] file, int framesAt, int rate, int channels, int bits, long total, byte[] md5)
    {
        (_file, _framesAt, SampleRate, Channels, BitsPerSample, TotalFrames, Md5) = (file, framesAt, rate, channels, bits, total, md5);
    }

    public static Flac Open(string path) => Parse(File.ReadAllBytes(path));

    public static Flac Parse(byte[] file)
    {
        if (file.Length < 42 || BinaryPrimitives.ReadUInt32BigEndian(file) != 0x664C6143) throw new InvalidDataException("not a FLAC stream");
        int pos = 4;
        int rate = 0, channels = 0, bits = 0;
        long total = 0;
        byte[] md5 = null;
        while (true)
        {
            if (pos + 4 > file.Length) throw new InvalidDataException("FLAC metadata runs past the file");
            bool last = (file[pos] & 0x80) != 0;
            int type = file[pos] & 0x7F;
            int length = file[pos + 1] << 16 | file[pos + 2] << 8 | file[pos + 3];
            int body = pos + 4;
            if (body + length > file.Length) throw new InvalidDataException("FLAC metadata runs past the file");
            if (type == 0 && length >= 34)
            {
                var b = file.AsSpan(body);
                ulong packed = BinaryPrimitives.ReadUInt64BigEndian(b[10..]);
                rate = (int)(packed >> 44);
                channels = (int)(packed >> 41 & 7) + 1;
                bits = (int)(packed >> 36 & 31) + 1;
                total = (long)(packed & 0xF_FFFF_FFFF);
                md5 = b.Slice(18, 16).ToArray();
            }
            pos = body + length;
            if (last) break;
        }
        if (md5 == null) throw new InvalidDataException("FLAC stream has no STREAMINFO");
        return new Flac(file, pos, rate, channels, bits, total, md5);
    }

    /// <summary>
    /// Decodes as 16-bit little-endian interleaved PCM (wider samples are shortened, narrower widened):
    /// <paramref name="count"/> sample frames from <paramref name="first"/>, into <paramref name="dest"/>
    /// from <paramref name="destAt"/> (count × channels × 2 bytes). Returns the frames written: fewer when the stream ends first.
    /// </summary>
    public long Decode16(long first, long count, byte[] dest, int destAt = 0)
    {
        long written = 0;
        int shift = BitsPerSample - 16;
        Decode(first + count, (block, at, n) =>
        {
            long from = Math.Max(first, at), to = Math.Min(first + count, at + n);
            for (long f = from; f < to; f++)
            {
                int o = destAt + (int)((f - first) * Channels * 2);
                for (int c = 0; c < Channels; c++)
                {
                    int s = block[c][f - at];
                    s = shift >= 0 ? s >> shift : s << -shift;
                    BinaryPrimitives.WriteInt16LittleEndian(dest.AsSpan(o + c * 2), (short)Math.Clamp(s, short.MinValue, short.MaxValue));
                }
            }
            written = Math.Max(written, to - first);
        });
        return written;
    }

    /// <summary>Decodes the whole stream and checks it against STREAMINFO's MD5; true when they match (or it has none).</summary>
    public bool Verify()
    {
        if (Array.TrueForAll(Md5, b => b == 0)) return true;
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        int bytes = (BitsPerSample + 7) / 8;
        byte[] buffer = [];
        Decode(long.MaxValue, (block, _, n) =>
        {
            int size = n * Channels * bytes;
            if (buffer.Length < size) buffer = new byte[size];
            int o = 0;
            for (int i = 0; i < n; i++)
                for (int c = 0; c < Channels; c++)
                {
                    int s = block[c][i];
                    for (int k = 0; k < bytes; k++) buffer[o++] = (byte)(s >> (8 * k));
                }
            md5.AppendData(buffer, 0, size);
        });
        return md5.GetHashAndReset().AsSpan().SequenceEqual(Md5);
    }

    private delegate void BlockHandler(int[][] block, long at, int count);

    /// <summary>Decodes frames in order until <paramref name="until"/> sample frames or the end, handing each block on.</summary>
    private void Decode(long until, BlockHandler handle)
    {
        var block = new int[Math.Max(Channels, 2)][];
        for (int c = 0; c < block.Length; c++) block[c] = new int[65536];
        var r = new BitReader(_file, _framesAt);
        long at = 0;
        while (at < until && r.BytePos + 2 <= _file.Length)
        {
            int n = DecodeFrame(ref r, block);
            if (n <= 0) break;
            handle(block, at, n);
            at += n;
        }
    }

    /// <summary>One frame into <paramref name="block"/>; returns its block size, or 0 at the end or on a broken frame.</summary>
    private int DecodeFrame(ref BitReader r, int[][] block)
    {
        if (r.Bits(14) != 0x3FFE) return 0;
        r.Bits(2); // reserved, blocking strategy
        int sizeCode = r.Bits(4), rateCode = r.Bits(4), assignment = r.Bits(4), bitsCode = r.Bits(3);
        r.Bits(1);
        // The frame or sample number, UTF-8 style: a lead byte saying how many continuation bytes follow.
        int lead = r.Bits(8);
        for (int extra = BitOperations.LeadingZeroCount((uint)(~lead & 0xFF) << 24); extra > 1; extra--) r.Bits(8);
        int size = sizeCode switch
        {
            1 => 192,
            >= 2 and <= 5 => 576 << (sizeCode - 2),
            6 => r.Bits(8) + 1,
            7 => r.Bits(16) + 1,
            >= 8 => 256 << (sizeCode - 8),
            _ => 0,
        };
        if (rateCode == 12) r.Bits(8);
        else if (rateCode is 13 or 14) r.Bits(16);
        r.Bits(8); // CRC-8
        int bits = bitsCode switch { 1 => 8, 2 => 12, 4 => 16, 5 => 20, 6 => 24, 7 => 32, _ => BitsPerSample };
        if (size <= 0 || size > 65536) return 0;

        int channels = assignment < 8 ? assignment + 1 : 2;
        for (int c = 0; c < channels; c++)
        {
            // The side channel carries one bit more.
            bool side = (assignment == 8 && c == 1) || (assignment == 9 && c == 0) || (assignment == 10 && c == 1);
            if (!DecodeSubframe(ref r, block[c], size, bits + (side ? 1 : 0))) return 0;
        }
        var (a, b) = (block[0], block[1]);
        switch (assignment)
        {
            case 8: for (int i = 0; i < size; i++) b[i] = a[i] - b[i]; break;
            case 9: for (int i = 0; i < size; i++) a[i] += b[i]; break;
            case 10:
                for (int i = 0; i < size; i++)
                {
                    int mid = a[i] << 1 | (b[i] & 1), sideS = b[i];
                    a[i] = (mid + sideS) >> 1;
                    b[i] = (mid - sideS) >> 1;
                }
                break;
        }
        r.AlignToByte();
        r.Bits(16); // CRC-16
        return size;
    }

    private static bool DecodeSubframe(ref BitReader r, int[] s, int size, int bits)
    {
        if (r.Bits(1) != 0) return false;
        int type = r.Bits(6);
        int wasted = 0;
        if (r.Bits(1) == 1) wasted = r.Unary() + 1;
        bits -= wasted;

        if (type == 0)
        {
            int v = r.Signed(bits);
            Array.Fill(s, v, 0, size);
        }
        else if (type == 1)
        {
            for (int i = 0; i < size; i++) s[i] = r.Signed(bits);
        }
        else if (type is >= 8 and <= 12)
        {
            int order = type - 8;
            if (order > size) return false;
            for (int i = 0; i < order; i++) s[i] = r.Signed(bits);
            if (!Residual(ref r, s, size, order)) return false;
            for (int i = order; i < size; i++)
                s[i] += order switch
                {
                    1 => s[i - 1],
                    2 => 2 * s[i - 1] - s[i - 2],
                    3 => 3 * s[i - 1] - 3 * s[i - 2] + s[i - 3],
                    4 => 4 * s[i - 1] - 6 * s[i - 2] + 4 * s[i - 3] - s[i - 4],
                    _ => 0,
                };
        }
        else if (type >= 32)
        {
            int order = type - 31;
            if (order > size) return false;
            for (int i = 0; i < order; i++) s[i] = r.Signed(bits);
            int precision = r.Bits(4) + 1;
            if (precision == 16) return false;
            int shift = r.Signed(5);
            Span<int> coefs = stackalloc int[32];
            for (int i = 0; i < order; i++) coefs[i] = r.Signed(precision);
            if (!Residual(ref r, s, size, order)) return false;
            for (int i = order; i < size; i++)
            {
                long sum = 0;
                for (int k = 0; k < order; k++) sum += (long)coefs[k] * s[i - 1 - k];
                s[i] += (int)(sum >> shift);
            }
        }
        else return false;

        if (wasted > 0)
            for (int i = 0; i < size; i++) s[i] <<= wasted;
        return true;
    }

    /// <summary>The Rice-coded residuals after the warm-up samples, in 2^order partitions.</summary>
    private static bool Residual(ref BitReader r, int[] s, int size, int predictorOrder)
    {
        int method = r.Bits(2);
        if (method > 1) return false;
        int paramBits = method == 0 ? 4 : 5, escape = method == 0 ? 15 : 31;
        int partitionOrder = r.Bits(4);
        int partitions = 1 << partitionOrder;
        int per = size >> partitionOrder;
        int i = predictorOrder;
        for (int p = 0; p < partitions; p++)
        {
            int end = (p + 1) * per;
            if (end < i) return false;
            int k = r.Bits(paramBits);
            if (k == escape)
            {
                int raw = r.Bits(5);
                for (; i < end; i++) s[i] = raw == 0 ? 0 : r.Signed(raw);
            }
            else
                for (; i < end; i++)
                {
                    uint u = (uint)r.Unary() << k | (uint)(k == 0 ? 0 : r.Bits(k));
                    s[i] = (int)(u >> 1) ^ -(int)(u & 1);
                }
        }
        return true;
    }

    /// <summary>Reads big-endian bit fields, most significant bit first, through a 64-bit window.</summary>
    private struct BitReader(byte[] data, int pos)
    {
        private ulong _window;
        private int _have;
        private int _pos = pos;

        public readonly int BytePos => _pos - _have / 8;

        private void Fill()
        {
            while (_have <= 56)
            {
                ulong b = _pos < data.Length ? data[_pos] : 0UL;
                _pos++;
                _window |= b << (56 - _have);
                _have += 8;
            }
        }

        public int Bits(int n)
        {
            if (n == 0) return 0;
            if (_have < n) Fill();
            int v = (int)(_window >> (64 - n));
            _window <<= n;
            _have -= n;
            return v;
        }

        public int Signed(int n)
        {
            if (n == 0) return 0;
            int v = Bits(n);
            return v << (32 - n) >> (32 - n);
        }

        /// <summary>Zero bits up to a one, which is used up too; the count of zeros.</summary>
        public int Unary()
        {
            int count = 0;
            while (true)
            {
                if (_have == 0 || _window == 0)
                {
                    count += _have;
                    _window = 0;
                    _have = 0;
                    Fill();
                    if (_pos > data.Length + 8) return count;
                    continue;
                }
                int z = BitOperations.LeadingZeroCount(_window);
                if (z >= _have)
                {
                    count += _have;
                    _window = 0;
                    _have = 0;
                    continue;
                }
                _window = z == 63 ? 0 : _window << (z + 1);
                _have -= z + 1;
                return count + z;
            }
        }

        public void AlignToByte()
        {
            int drop = _have % 8;
            _window <<= drop;
            _have -= drop;
        }
    }
}
