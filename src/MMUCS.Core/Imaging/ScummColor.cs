using System;

namespace MMUCS.Core.Imaging;

/// <summary>
/// Framework-neutral RGBA colour with float channels in [0,1].
/// Replaces Godot.Color in the parsing/decoding layer.
/// </summary>
public struct ScummColor(float r, float g, float b, float a = 1f) : IEquatable<ScummColor>
{
    public float R = r;
    public float G = g;
    public float B = b;
    public float A = a;

    public static ScummColor FromBytes(byte r, byte g, byte b, byte a = 255)
    {
        return new(r / 255f, g / 255f, b / 255f, a / 255f);
    }

    public static readonly ScummColor Transparent = new(0, 0, 0, 0);
    public static readonly ScummColor Magenta = new(1, 0, 1, 1);

    public readonly byte R8 => (byte)Math.Clamp((int)(R * 255f + 0.5f), 0, 255);
    public readonly byte G8 => (byte)Math.Clamp((int)(G * 255f + 0.5f), 0, 255);
    public readonly byte B8 => (byte)Math.Clamp((int)(B * 255f + 0.5f), 0, 255);
    public readonly byte A8 => (byte)Math.Clamp((int)(A * 255f + 0.5f), 0, 255);

    /// <summary>Packed as 0xAARRGGBB (matches SKColor / Avalonia uint layout).</summary>
    public readonly uint ToArgb()
    {
        return ((uint)A8 << 24) | ((uint)R8 << 16) | ((uint)G8 << 8) | B8;
    }

    public readonly ScummColor Lerp(ScummColor to, float t)
    {
        return new(
        R + (to.R - R) * t,
        G + (to.G - G) * t,
        B + (to.B - B) * t,
        A + (to.A - A) * t);
    }

    public readonly bool Equals(ScummColor other)
    {
        return R == other.R && G == other.G && B == other.B && A == other.A;
    }

    public override readonly bool Equals(object obj)
    {
        return obj is ScummColor c && Equals(c);
    }

    public override readonly int GetHashCode()
    {
        return HashCode.Combine(R, G, B, A);
    }

    public override readonly string ToString()
    {
        return $"#{R8:X2}{G8:X2}{B8:X2}{A8:X2}";
    }

    public static bool operator ==(ScummColor left, ScummColor right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(ScummColor left, ScummColor right)
    {
        return !(left == right);
    }
}
