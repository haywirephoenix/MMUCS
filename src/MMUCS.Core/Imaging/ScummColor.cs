using System;

namespace MMUCS.Core.Imaging;

/// <summary>
/// Framework-neutral RGBA colour with float channels in [0,1].
/// Replaces Godot.Color in the parsing/decoding layer.
/// </summary>
public struct ScummColor : IEquatable<ScummColor>
{
    public float R;
    public float G;
    public float B;
    public float A;

    public ScummColor(float r, float g, float b, float a = 1f)
    {
        R = r; G = g; B = b; A = a;
    }

    public static ScummColor FromBytes(byte r, byte g, byte b, byte a = 255) =>
        new(r / 255f, g / 255f, b / 255f, a / 255f);

    public static readonly ScummColor Transparent = new(0, 0, 0, 0);
    public static readonly ScummColor Magenta = new(1, 0, 1, 1);

    public byte R8 => (byte)Math.Clamp((int)(R * 255f + 0.5f), 0, 255);
    public byte G8 => (byte)Math.Clamp((int)(G * 255f + 0.5f), 0, 255);
    public byte B8 => (byte)Math.Clamp((int)(B * 255f + 0.5f), 0, 255);
    public byte A8 => (byte)Math.Clamp((int)(A * 255f + 0.5f), 0, 255);

    /// <summary>Packed as 0xAARRGGBB (matches SKColor / Avalonia uint layout).</summary>
    public uint ToArgb() => ((uint)A8 << 24) | ((uint)R8 << 16) | ((uint)G8 << 8) | B8;

    public ScummColor Lerp(ScummColor to, float t) => new(
        R + (to.R - R) * t,
        G + (to.G - G) * t,
        B + (to.B - B) * t,
        A + (to.A - A) * t);

    public bool Equals(ScummColor other) => R == other.R && G == other.G && B == other.B && A == other.A;
    public override bool Equals(object obj) => obj is ScummColor c && Equals(c);
    public override int GetHashCode() => HashCode.Combine(R, G, B, A);
    public override string ToString() => $"#{R8:X2}{G8:X2}{B8:X2}{A8:X2}";
}
