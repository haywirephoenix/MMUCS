using System;

namespace MMUCS.Core.Smush;

/// <summary>
/// The palette a SMUSH video is showing. NPAL replaces it; XPAL either loads a set of per-channel
/// deltas (and usually a new palette with them) or steps the palette one notch along those deltas,
/// which is how the videos fade.
/// </summary>
public sealed class SmushPalette
{
    private const int Size = SmushHeader.PaletteSize;
    private readonly short[] _delta = new short[Size];
    private bool _hasDelta;

    /// <summary>256 RGB triplets, 8 bits per channel.</summary>
    public byte[] Rgb { get; } = new byte[Size];

    public SmushPalette(ReadOnlySpan<byte> initial) => Set(initial);

    public void Set(ReadOnlySpan<byte> rgb) => rgb[..Math.Min(rgb.Length, Size)].CopyTo(Rgb);

    /// <summary>Applies an XPAL chunk. Returns false for one it doesn't understand.</summary>
    public bool ApplyDelta(ReadOnlySpan<byte> xpal)
    {
        // Long form: 4 bytes, 0x300 signed 16-bit deltas, then (usually) the palette to start from.
        if (xpal.Length >= 4 + Size * 2)
        {
            var deltas = xpal.Slice(4, Size * 2);
            for (int i = 0; i < Size; i++)
                _delta[i] = (short)(deltas[i * 2] | (deltas[i * 2 + 1] << 8));
            _hasDelta = true;
            if (xpal.Length >= 4 + Size * 3) Set(xpal.Slice(4 + Size * 2, Size));
            return true;
        }

        // Short form: one step along the loaded deltas.
        if (!_hasDelta) return false;
        for (int i = 0; i < Size; i++)
            Rgb[i] = Step(Rgb[i], _delta[i]);
        return true;
    }

    /// <summary>
    /// One fade step: the delta is in 1/128ths, on top of the channel grown by 1/128th. C-style
    /// (truncating) division, then clamped.
    /// </summary>
    internal static byte Step(byte value, short delta) => (byte)Math.Clamp((value * 129 + delta) / 128, 0, 255);
}
