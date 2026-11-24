using System;
using MMUCS.Core.Imaging;

namespace MMUCS.Core.Scumm;

/// <summary>
/// How an actor's shadow pixels (costume colours 1–7) draw, as the v8 engine does them (ScummVM akos.cpp
/// codec 1). In shadow mode 3, colour N turns the screen pixel under it into its colour in shadow table N;
/// the tables scale colours by an R,G,B factor, so the frame's shadow pixels come out as a layer of those
/// factors, <see cref="Image"/>, for the host to multiply onto what's under the actor. In the other modes
/// they are ordinary colours.
/// </summary>
public sealed class AkosShadow
{
    /// <summary>The actor's shadow mode (ScummVM <c>_shadowMode</c>).</summary>
    public int Mode { get; }

    /// <summary>Each shadow table's factors, 8 RGB triples of 0–255 (255: as it was).</summary>
    public byte[] Factors { get; }

    private byte[] _layer;
    private int _width, _height;
    private bool _any;

    public AkosShadow(int mode, byte[] factors)
    {
        Mode = mode;
        Factors = factors ?? throw new ArgumentNullException(nameof(factors));
        if (factors.Length < 8 * 3) throw new ArgumentException("8 RGB factors needed", nameof(factors));
    }

    /// <summary>The last frame's shadow layer (opaque where it shades, in the factors), or null when it has none.</summary>
    public RgbaImage Image => _any ? new RgbaImage(_width, _height, _layer) : null;

    internal void Reset() => _any = false;

    internal void Start(int width, int height)
    {
        (_width, _height, _any) = (width, height, false);
        _layer = new byte[width * height * 4];
    }

    /// <summary>Shadow table <paramref name="table"/> shades pixel offset <paramref name="o"/>; over a shaded one it shades it again.</summary>
    internal void Shade(int o, int table)
    {
        int f = table * 3;
        if (_layer[o + 3] == 0)
        {
            _layer[o] = Factors[f];
            _layer[o + 1] = Factors[f + 1];
            _layer[o + 2] = Factors[f + 2];
            _layer[o + 3] = 255;
        }
        else
            for (int k = 0; k < 3; k++) _layer[o + k] = (byte)(_layer[o + k] * Factors[f + k] / 255);
        _any = true;
    }

    /// <summary>An opaque pixel drawn over offset <paramref name="o"/> covers its shade.</summary>
    internal void Clear(int o)
    {
        if (_layer[o + 3] != 0) _layer[o] = _layer[o + 1] = _layer[o + 2] = _layer[o + 3] = 0;
    }

    internal void Mirror()
    {
        for (int y = 0; y < _height; y++)
            for (int i = y * _width, j = i + _width - 1; i < j; i++, j--)
                for (int k = 0; k < 4; k++)
                    (_layer[i * 4 + k], _layer[j * 4 + k]) = (_layer[j * 4 + k], _layer[i * 4 + k]);
    }
}
