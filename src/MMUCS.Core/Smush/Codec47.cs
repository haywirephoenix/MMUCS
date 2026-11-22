using System;

namespace MMUCS.Core.Smush;

/// <summary>
/// SMUSH codec 47, the block codec of COMI's cutscenes. It keeps three screens: the one being built
/// and two earlier ones. Each frame either stores pixels outright (raw or RLE), repeats one of the
/// earlier screens, or (the usual case) rebuilds the screen from 8×8 blocks, each of which is copied
/// from the screen two back at a motion offset, copied from the previous screen in place, filled with
/// one colour, drawn as a two-colour line glyph, or split into four smaller blocks down to 2×2.
/// After a frame the screens rotate so the next frame's references are the right ones.
/// </summary>
public sealed class Codec47
{
    private const int HeaderSize = 26;
    /// <summary>Bit 0 of the flags byte: an interpolation table of this size precedes the pixel data.</summary>
    private const int InterpolationTableSize = 0x8080;

    private readonly int _width, _height;
    /// <summary>Internal row length: the width rounded up to whole 8×8 blocks.</summary>
    private readonly int _pitch;
    private readonly int _rows;
    private byte[] _current, _previous, _older;
    private int _lastSequence = int.MinValue;
    private readonly byte[] _fills = new byte[8];

    public Codec47(int width, int height)
    {
        _width = width;
        _height = height;
        _pitch = (width + 7) & ~7;
        _rows = (height + 7) & ~7;
        _current = new byte[_pitch * _rows];
        _previous = new byte[_pitch * _rows];
        _older = new byte[_pitch * _rows];
    }

    public int Width => _width;
    public int Height => _height;

    /// <summary>
    /// Bytes left unread after the last block-coded frame. 0 (or a byte of padding) means the block
    /// stream was parsed exactly as it was written; more points at a decoding mistake.
    /// </summary>
    public int UnreadBytes { get; private set; }
    /// <summary>Motion-vector copies so far that reached outside the screen.</summary>
    public int OutsideCopies { get; private set; }

    /// <summary>
    /// Decodes one frame object's payload (after the FOBJ header) into <paramref name="output"/>,
    /// width × height bytes. Returns the compression mode the frame used.
    /// </summary>
    public int Decode(ReadOnlySpan<byte> src, Span<byte> output)
    {
        if (src.Length < HeaderSize) throw new ArgumentException("codec 47 frame is shorter than its header");

        int sequence = src[0] | (src[1] << 8);
        int compression = src[2];
        int rotation = src[3];
        int flags = src[4];
        src.Slice(8, 8).CopyTo(_fills);
        byte startPrevious = src[12], startOlder = src[13];

        var gfx = src[HeaderSize..];
        if ((flags & 1) != 0) gfx = gfx[Math.Min(InterpolationTableSize, gfx.Length)..];

        // Sequence 0 starts a run of frames: the reference screens start as flat colours.
        if (sequence == 0)
        {
            Array.Fill(_previous, startPrevious);
            Array.Fill(_older, startOlder);
            _lastSequence = -1;
        }
        bool inSequence = sequence == _lastSequence + 1;

        switch (compression)
        {
            case 0: CopyRows(gfx, _current); break;
            case 1: HalfResolution(gfx); break;
            case 2:
                // Blocks refer to the frames before, so they only make sense in order.
                if (inSequence) DecodeBlocks(gfx);
                break;
            case 3: Array.Copy(_older, _current, _current.Length); break;
            case 4: Array.Copy(_previous, _current, _current.Length); break;
            case 5:
            {
                var flat = new byte[_width * _height];
                SmushRle.DecodeStream(gfx, flat);
                CopyRows(flat, _current);
                break;
            }
            default: throw new NotSupportedException($"codec 47 compression {compression}");
        }

        for (int y = 0; y < _height; y++)
            _current.AsSpan(y * _pitch, _width).CopyTo(output.Slice(y * _width, _width));

        if (inSequence)
        {
            // 1: this frame becomes the "older" reference. 2: the previous one ages too.
            if (rotation == 2) (_previous, _older) = (_older, _previous);
            if (rotation is 1 or 2) (_current, _older) = (_older, _current);
        }
        _lastSequence = sequence;
        return compression;
    }

    /// <summary>Copies width-long rows from a packed source into the padded screen.</summary>
    private void CopyRows(ReadOnlySpan<byte> packed, byte[] screen)
    {
        for (int y = 0; y < _height; y++)
        {
            int start = y * _width;
            if (start >= packed.Length) break;
            int n = Math.Min(_width, packed.Length - start);
            packed.Slice(start, n).CopyTo(screen.AsSpan(y * _pitch, n));
        }
    }

    /// <summary>Compression 1: a half-size picture shown with every pixel doubled. COMI never uses it.</summary>
    private void HalfResolution(ReadOnlySpan<byte> gfx)
    {
        int hw = _width / 2;
        for (int y = 0; y < _height; y++)
            for (int x = 0; x < _width; x++)
            {
                int i = (y / 2) * hw + x / 2;
                _current[y * _pitch + x] = i < gfx.Length ? gfx[i] : (byte)0;
            }
    }

    private void DecodeBlocks(ReadOnlySpan<byte> gfx)
    {
        int pos = 0;
        for (int y = 0; y < _rows; y += 8)
            for (int x = 0; x < _pitch; x += 8)
                Block(gfx, ref pos, x, y, 8);
        UnreadBytes = gfx.Length - pos;
    }

    private void Block(ReadOnlySpan<byte> gfx, ref int pos, int x, int y, int size)
    {
        int at = y * _pitch + x;
        byte code = gfx[pos++];

        if (code < 0xF8)
        {
            // Copy from two frames back, offset by one of 248 fixed motion vectors.
            int mx = MotionVectors[code * 2], my = MotionVectors[code * 2 + 1];
            Copy(_older, at + my * _pitch + mx, at, size);
            return;
        }

        switch (code)
        {
            case 0xFF when size == 2:
                _current[at] = gfx[pos];
                _current[at + 1] = gfx[pos + 1];
                _current[at + _pitch] = gfx[pos + 2];
                _current[at + _pitch + 1] = gfx[pos + 3];
                pos += 4;
                return;
            case 0xFF:
            {
                int half = size / 2;
                Block(gfx, ref pos, x, y, half);
                Block(gfx, ref pos, x + half, y, half);
                Block(gfx, ref pos, x, y + half, half);
                Block(gfx, ref pos, x + half, y + half, half);
                return;
            }
            case 0xFE:
                Fill(at, size, gfx[pos++]);
                return;
            case 0xFD when size > 2:
            {
                int glyph = gfx[pos];
                byte on = gfx[pos + 1], off = gfx[pos + 2];
                pos += 3;
                ulong bits = size == 8 ? Glyphs.Large[glyph] : Glyphs.Small[glyph];
                for (int row = 0; row < size; row++)
                {
                    int o = at + row * _pitch;
                    for (int col = 0; col < size; col++)
                        _current[o + col] = ((bits >> (row * size + col)) & 1) != 0 ? on : off;
                }
                return;
            }
            case 0xFC:
                Copy(_previous, at, at, size);
                return;
            default:
                // F8–FB (and FD on a 2×2 block): one of the colours in the frame header.
                Fill(at, size, _fills[code & 7]);
                return;
        }
    }

    private void Fill(int at, int size, byte colour)
    {
        for (int row = 0; row < size; row++)
            _current.AsSpan(at + row * _pitch, size).Fill(colour);
    }

    /// <summary>
    /// Copies a size×size block. The source is a flat offset into the screen, so a vector reaching past
    /// a row's end continues on the next row, as in the original; bytes outside the screen read as 0.
    /// </summary>
    private void Copy(byte[] from, int src, int dst, int size)
    {
        int last = src + (size - 1) * _pitch + size;
        if (src >= 0 && last <= from.Length)
        {
            for (int row = 0; row < size; row++)
                Buffer.BlockCopy(from, src + row * _pitch, _current, dst + row * _pitch, size);
            return;
        }
        OutsideCopies++;
        for (int row = 0; row < size; row++)
            for (int col = 0; col < size; col++)
            {
                int s = src + row * _pitch + col;
                _current[dst + row * _pitch + col] = s >= 0 && s < from.Length ? from[s] : (byte)0;
            }
    }

    /// <summary>The 248 motion vectors (x, y) block codes 0x00–0xF7 refer to; part of the format.</summary>
    private static readonly sbyte[] MotionVectors =
    {
          0,  0,  -1,-43,   6,-43,  -9,-42,  13,-41, -16,-40,  19,-39, -23,-36,
         26,-34,  -2,-33,   4,-33, -29,-32,  -9,-32,  11,-31, -16,-29,  32,-29,
         18,-28, -34,-26, -22,-25,  -1,-25,   3,-25,  -7,-24,   8,-24,  24,-23,
         36,-23, -12,-22,  13,-21, -38,-20,   0,-20, -27,-19,  -4,-19,   4,-19,
        -17,-18,  -8,-17,   8,-17,  18,-17,  28,-17,  39,-17, -12,-15,  12,-15,
        -21,-14,  -1,-14,   1,-14, -41,-13,  -5,-13,   5,-13,  21,-13, -31,-12,
        -15,-11,  -8,-11,   8,-11,  15,-11,  -2,-10,   1,-10,  31,-10, -23, -9,
        -11, -9,  -5, -9,   4, -9,  11, -9,  42, -9,   6, -8,  24, -8, -18, -7,
         -7, -7,  -3, -7,  -1, -7,   2, -7,  18, -7, -43, -6, -13, -6,  -4, -6,
          4, -6,   8, -6, -33, -5,  -9, -5,  -2, -5,   0, -5,   2, -5,   5, -5,
         13, -5, -25, -4,  -6, -4,  -3, -4,   3, -4,   9, -4, -19, -3,  -7, -3,
         -4, -3,  -2, -3,  -1, -3,   0, -3,   1, -3,   2, -3,   4, -3,   6, -3,
         33, -3, -14, -2, -10, -2,  -5, -2,  -3, -2,  -2, -2,  -1, -2,   0, -2,
          1, -2,   2, -2,   3, -2,   5, -2,   7, -2,  14, -2,  19, -2,  25, -2,
         43, -2,  -7, -1,  -3, -1,  -2, -1,  -1, -1,   0, -1,   1, -1,   2, -1,
          3, -1,  10, -1,  -5,  0,  -3,  0,  -2,  0,  -1,  0,   1,  0,   2,  0,
          3,  0,   5,  0,   7,  0, -10,  1,  -7,  1,  -3,  1,  -2,  1,  -1,  1,
          0,  1,   1,  1,   2,  1,   3,  1, -43,  2, -25,  2, -19,  2, -14,  2,
         -5,  2,  -3,  2,  -2,  2,  -1,  2,   0,  2,   1,  2,   2,  2,   3,  2,
          5,  2,   7,  2,  10,  2,  14,  2, -33,  3,  -6,  3,  -4,  3,  -2,  3,
         -1,  3,   0,  3,   1,  3,   2,  3,   4,  3,  19,  3,  -9,  4,  -3,  4,
          3,  4,   7,  4,  25,  4, -13,  5,  -5,  5,  -2,  5,   0,  5,   2,  5,
          5,  5,   9,  5,  33,  5,  -8,  6,  -4,  6,   4,  6,  13,  6,  43,  6,
        -18,  7,  -2,  7,   0,  7,   2,  7,   7,  7,  18,  7, -24,  8,  -6,  8,
        -42,  9, -11,  9,  -4,  9,   5,  9,  11,  9,  23,  9, -31, 10,  -1, 10,
          2, 10, -15, 11,  -8, 11,   8, 11,  15, 11,  31, 12, -21, 13,  -5, 13,
          5, 13,  41, 13,  -1, 14,   1, 14,  21, 14, -12, 15,  12, 15, -39, 17,
        -28, 17, -18, 17,  -8, 17,   8, 17,  17, 18,  -4, 19,   0, 19,   4, 19,
         27, 19,  38, 20, -13, 21,  12, 22, -36, 23, -24, 23,  -8, 24,   7, 24,
         -3, 25,   1, 25,  22, 25,  34, 26, -18, 28, -32, 29,  16, 29, -11, 31,
          9, 32,  29, 32,  -4, 33,   2, 33, -26, 34,  23, 36, -19, 39,  16, 40,
    };

    /// <summary>
    /// The two-colour glyphs for code 0xFD: 256 per block size, one for each pair of points from a
    /// fixed list of 16. A line is drawn between the two points and the side of the block it faces is
    /// filled; set bits take the first colour. Stored as bit masks, bit (row × size + column).
    /// </summary>
    private static class Glyphs
    {
        public static readonly ulong[] Small = Build(4,
            new[] { 0, 1, 2, 3, 3, 3, 3, 2, 1, 0, 0, 0, 1, 2, 2, 1 },
            new[] { 0, 0, 0, 0, 1, 2, 3, 3, 3, 3, 2, 1, 1, 1, 2, 2 });

        public static readonly ulong[] Large = Build(8,
            new[] { 0, 2, 5, 7, 7, 7, 7, 7, 7, 5, 2, 0, 0, 0, 0, 0 },
            new[] { 0, 0, 0, 0, 1, 3, 4, 6, 7, 7, 7, 7, 6, 4, 3, 1 });

        private enum Edge { Left, Top, Right, Bottom, None }
        private enum Side { Left, Up, Right, Down, None }

        private static ulong[] Build(int size, int[] xs, int[] ys)
        {
            var glyphs = new ulong[256];
            for (int a = 0; a < 16; a++)
                for (int b = 0; b < 16; b++)
                    glyphs[a * 16 + b] = Draw(size, xs[a], ys[a], xs[b], ys[b]);
            return glyphs;
        }

        private static ulong Draw(int size, int x0, int y0, int x1, int y1)
        {
            var side = FillSide(EdgeOf(x0, y0, size), EdgeOf(x1, y1, size));
            int steps = Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0));
            ulong bits = 0;
            for (int i = 0; i <= steps; i++)
            {
                // Walks from (x1, y1) to (x0, y0), rounding to the nearest pixel.
                int px = steps == 0 ? x0 : (x0 * i + x1 * (steps - i) + steps / 2) / steps;
                int py = steps == 0 ? y0 : (y0 * i + y1 * (steps - i) + steps / 2) / steps;
                switch (side)
                {
                    case Side.Up: for (int y = 0; y <= py; y++) bits |= Bit(px, y, size); break;
                    case Side.Down: for (int y = py; y < size; y++) bits |= Bit(px, y, size); break;
                    case Side.Left: for (int x = 0; x <= px; x++) bits |= Bit(x, py, size); break;
                    case Side.Right: for (int x = px; x < size; x++) bits |= Bit(x, py, size); break;
                }
            }
            return bits;
        }

        private static ulong Bit(int x, int y, int size) => 1UL << (y * size + x);

        // Row 0 counts as the bottom edge and the last row as the top: the original's naming.
        private static Edge EdgeOf(int x, int y, int size) =>
            y == 0 ? Edge.Bottom : y == size - 1 ? Edge.Top : x == 0 ? Edge.Left : x == size - 1 ? Edge.Right : Edge.None;

        private static Side FillSide(Edge e0, Edge e1)
        {
            bool Pair(Edge a, Edge b) => (e0 == a && e1 == b) || (e1 == a && e0 == b);
            bool OneIsNotOpposite(Edge a, Edge notB) => (e0 == a && e1 != notB) || (e1 == a && e0 != notB);

            if (Pair(Edge.Left, Edge.Right) || OneIsNotOpposite(Edge.Bottom, Edge.Top)) return Side.Up;
            if (OneIsNotOpposite(Edge.Top, Edge.Bottom)) return Side.Down;
            if (OneIsNotOpposite(Edge.Left, Edge.Right)) return Side.Left;
            if (Pair(Edge.Top, Edge.Bottom) || OneIsNotOpposite(Edge.Right, Edge.Left)) return Side.Right;
            return Side.None;
        }
    }
}
