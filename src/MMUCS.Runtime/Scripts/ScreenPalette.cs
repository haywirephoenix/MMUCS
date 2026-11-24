using System;

namespace MMUCS.Runtime.Scripts;

/// <summary>
/// The colours on the screen, as ScummVM's v8 engine keeps them (palette.cpp): everything is drawn in
/// palette indices, so the room, its objects, the actors and the texts all show <see cref="Current"/>.
/// Entering a room sets it from the room's first palette; the room instructions then change it.
/// <see cref="Base"/> is ScummVM's <c>_darkenPalette</c>, what <c>palette-intensity</c> and
/// <c>palette-saturation</c> scale from: it follows the room's palette and single colours the scripts set,
/// not the scaled ones, so a fade from black comes back to the room's own colours. The shadow tables
/// (<c>build-palette-shadow</c>) say which colour a shadow pixel turns each colour into.
/// <see cref="Version"/> changes whenever a colour does, so a host knows when to colour its pictures again.
/// </summary>
public sealed class ScreenPalette
{
    public const int Colours = 256, ShadowSlots = 8;

    /// <summary>The first colour <see cref="Nearest"/> picks in v8 (0–23 are the game's fixed colours).</summary>
    public const int FirstFreeColour = 24;

    private byte[] _target;
    private int[] _between;
    private int _transformStart, _transformEnd, _transformLeft;

    /// <summary>The colours shown, 256 RGB triples.</summary>
    public byte[] Current { get; } = new byte[Colours * 3];

    /// <summary>The colours the intensity and saturation instructions scale (ScummVM <c>_darkenPalette</c>).</summary>
    public byte[] Base { get; } = new byte[Colours * 3];

    /// <summary>The shadow tables, 256 colours each (ScummVM <c>_shadowPalette</c>).</summary>
    public byte[] Shadows { get; } = new byte[ShadowSlots * Colours];

    /// <summary>Goes up each time a colour changes.</summary>
    public int Version { get; private set; }

    /// <summary>Whether a <c>palette-transform</c> is still on its way.</summary>
    public bool IsTransforming => _transformLeft > 0;

    /// <summary>The colour of index <paramref name="i"/>.</summary>
    public (byte R, byte G, byte B) this[int i] => (Current[i * 3], Current[i * 3 + 1], Current[i * 3 + 2]);

    /// <summary>A copy of the colours shown.</summary>
    public byte[] Snapshot() => (byte[])Current.Clone();

    /// <summary>A new palette (ScummVM <c>setPaletteFromPtr</c>): the colours given, the rest left; the base follows.</summary>
    public void Set(ReadOnlySpan<byte> rgb)
    {
        int n = Math.Min(rgb.Length / 3, Colours) * 3;
        bool changed = !rgb[..n].SequenceEqual(Current.AsSpan(0, n));
        rgb[..n].CopyTo(Current);
        Current.AsSpan().CopyTo(Base);
        _transformLeft = 0;
        if (changed) Version++;
    }

    /// <summary><c>room palette N is rgb R,G,B</c> (ScummVM <c>setPalColor</c>): the colour and, in v8, its base.</summary>
    public void SetColour(int index, int r, int g, int b)
    {
        if (index is < 0 or >= Colours) throw new ScriptException($"palette colour {index} out of range");
        int o = index * 3;
        Base[o] = (byte)r;
        Base[o + 1] = (byte)g;
        Base[o + 2] = (byte)b;
        Write(o, (byte)r, (byte)g, (byte)b);
    }

    /// <summary>
    /// <c>palette-intensity R,G,B slots A to B</c> (ScummVM <c>darkenPalette</c>): each colour in the range is
    /// its base scaled by R/255, G/255 and B/255, at most 255, so 255 is the room's own colour and 0 black.
    /// </summary>
    public void Darken(int redScale, int greenScale, int blueScale, int start, int end)
    {
        if (start > end) return;
        Check(start, end, "palette-intensity");
        for (int i = start; i <= end; i++)
        {
            int o = i * 3;
            Write(o, Scale(Base[o], redScale), Scale(Base[o + 1], greenScale), Scale(Base[o + 2], blueScale));
        }

        static byte Scale(byte c, int scale) => (byte)Math.Clamp(c * scale / 0xFF, 0, 255);
    }

    /// <summary>
    /// <c>palette-saturation H,S,L slots A to B</c> (ScummVM <c>desaturatePalette</c>): each colour in the range is
    /// its base with hue, saturation and lightness scaled by H/255, S/255 and L/255 (in the engine's integer HLS).
    /// </summary>
    public void Desaturate(int hueScale, int satScale, int lightScale, int start, int end)
    {
        if (start > end) return;
        Check(start, end, "palette-saturation");
        for (int j = start; j <= end; j++)
        {
            int o = j * 3;
            int r = Base[o], g = Base[o + 1], b = Base[o + 2];
            int min = Math.Min(r, Math.Min(g, b)), max = Math.Max(r, Math.Max(g, b));
            int diff = max - min, sum = max + min;
            if (diff != 0)
            {
                int s = sum <= 255 ? 255 * diff / sum : 255 * diff / (255 * 2 - sum);
                int h = r == max ? 60 * (g - b) / diff : g == max ? 120 + 60 * (b - r) / diff : 240 + 60 * (r - g) / diff;
                if (h < 0) h += 360;
                h = h * hueScale / 255;
                s = s * satScale / 255;
                int l = sum * lightScale / 255;
                int m2 = l <= 255 ? l * (255 + s) / (255 * 2) : l * (255 - s) / (255 * 2) + s;
                int m1 = l - m2;
                r = Hls(m1, m2, h + 120);
                g = Hls(m1, m2, h);
                b = Hls(m1, m2, h - 120);
            }
            else r = g = b = r * lightScale / 255;
            Write(o, (byte)r, (byte)g, (byte)b); // the engine stores a byte: larger values wrap
        }

        static int Hls(int n1, int n2, int hue)
        {
            if (hue > 360) hue -= 360;
            else if (hue < 0) hue += 360;
            if (hue < 60) return n1 + (n2 - n1) * hue / 60;
            if (hue < 180) return n2;
            if (hue < 240) return n1 + (n2 - n1) * (240 - hue) / 60;
            return n1;
        }
    }

    /// <summary>
    /// <c>palette-transform N slots A to B time T</c> (ScummVM v6+ <c>palManipulateInit</c>): colours A up to (not
    /// including) B move to <paramref name="target"/>'s (a room palette) over T frames, a step each <see cref="Step"/>.
    /// </summary>
    public void StartTransform(ReadOnlySpan<byte> target, int start, int end, int time)
    {
        Check(start, Math.Max(start, end - 1), "palette-transform");
        _target ??= new byte[Colours * 3];
        _between ??= new int[Colours * 3];
        for (int i = start * 3; i < end * 3 && i < target.Length; i++)
        {
            _target[i] = target[i];
            _between[i] = Current[i] << 8;
        }
        (_transformStart, _transformEnd, _transformLeft) = (start, end, time);
    }

    /// <summary>A frame of the transform under way, if any (ScummVM <c>palManipulate</c>, once a frame after drawing).</summary>
    public void Step()
    {
        if (_transformLeft <= 0) return;
        for (int i = _transformStart * 3; i < _transformEnd * 3; i++)
        {
            _between[i] += ((_target[i] << 8) - _between[i]) / _transformLeft;
            if (Current[i] != (byte)(_between[i] >> 8))
            {
                Current[i] = (byte)(_between[i] >> 8);
                Changed();
            }
        }
        _transformLeft--;
    }

    /// <summary>
    /// <c>build-palette-shadow N, R,G,B, A, B</c> (ScummVM <c>setShadowPalette</c>): shadow table N maps every
    /// colour to itself, but colours A to B to the colour nearest theirs scaled by R/256, G/256 and B/256.
    /// </summary>
    public void SetShadow(int slot, int redScale, int greenScale, int blueScale, int start, int end)
    {
        if (slot is < 0 or >= ShadowSlots) throw new ScriptException($"shadow palette {slot} out of range");
        if (start is < 0 or > 255 || end is < 0 or > 255 || end < start) throw new ScriptException($"shadow palette range {start} to {end}");
        var table = Shadows.AsSpan(slot * Colours, Colours);
        for (int i = 0; i < Colours; i++) table[i] = (byte)i;
        for (int i = start; i <= end; i++)
            table[i] = (byte)Nearest(Current[i * 3] * redScale >> 8, Current[i * 3 + 1] * greenScale >> 8, Current[i * 3 + 2] * blueScale >> 8);
    }

    /// <summary>
    /// The colour nearest R,G,B among 24–254 (ScummVM <c>remapPaletteColor</c>, v8): an exact match (to 6 bits)
    /// at once, else the least weighted distance. With a <paramref name="threshold"/>, a match further than it
    /// takes a free colour (a white one from 254 down to 49) and sets it.
    /// </summary>
    public int Nearest(int r, int g, int b, int threshold = -1)
    {
        r = Math.Min(r, 255) & ~3;
        g = Math.Min(g, 255) & ~3;
        b = Math.Min(b, 255) & ~3;
        long best = long.MaxValue;
        int bestItem = 0;
        for (int i = FirstFreeColour; i < 255; i++)
        {
            int ar = Current[i * 3] & ~3, ag = Current[i * 3 + 1] & ~3, ab = Current[i * 3 + 2] & ~3;
            if (ar == r && ag == g && ab == b) return i;
            long sum = Weight(ar - r, ag - g, ab - b);
            if (sum < best) (best, bestItem) = (sum, i);
        }
        if (threshold != -1 && best > Weight(threshold, threshold, threshold))
            for (int i = 254; i > 48; i--)
                if (Current[i * 3] >= 252 && Current[i * 3 + 1] >= 252 && Current[i * 3 + 2] >= 252)
                {
                    SetColour(i, r, g, b);
                    return i;
                }
        return bestItem;

        static long Weight(int dr, int dg, int db) => 3L * dr * dr + 6L * dg * dg + 2L * db * db;
    }

    private void Write(int o, byte r, byte g, byte b)
    {
        if (Current[o] == r && Current[o + 1] == g && Current[o + 2] == b) return;
        Current[o] = r;
        Current[o + 1] = g;
        Current[o + 2] = b;
        Changed();
    }

    private void Changed() => Version++;

    private static void Check(int start, int end, string what)
    {
        if (start is < 0 or >= Colours || end is < 0 or >= Colours) throw new ScriptException($"{what} slots {start} to {end} out of range");
    }
}
