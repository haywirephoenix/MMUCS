using System;
using System.IO;

namespace MMUCS.Core.Imaging;

/// <summary>
/// Straight (non-premultiplied) RGBA8888 bitmap. Framework-neutral replacement for Godot.Image.
/// UI layers wrap <see cref="Data"/> directly (e.g. SKBitmap / WriteableBitmap) without copying semantics.
/// </summary>
public sealed class RgbaImage
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Data { get; }
    public int Stride => Width * 4;

    public RgbaImage(int width, int height, byte[] rgba)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (rgba == null || rgba.Length < width * height * 4) throw new ArgumentException("Buffer too small", nameof(rgba));
        Width = width;
        Height = height;
        Data = rgba;
    }

    public static RgbaImage CreateEmpty(int width, int height) => new(width, height, new byte[width * height * 4]);

    // Kept for readability of code ported from Godot.
    public int GetWidth() => Width;
    public int GetHeight() => Height;
    public byte[] GetData() => Data;

    public ScummColor GetPixel(int x, int y)
    {
        int i = (y * Width + x) * 4;
        return ScummColor.FromBytes(Data[i], Data[i + 1], Data[i + 2], Data[i + 3]);
    }

    public void SetPixel(int x, int y, ScummColor c)
    {
        int i = (y * Width + x) * 4;
        Data[i] = c.R8; Data[i + 1] = c.G8; Data[i + 2] = c.B8; Data[i + 3] = c.A8;
    }

    public void SavePng(string path)
    {
        string dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using var fs = File.Create(path);
        PngWriter.Write(fs, Width, Height, Data);
    }
}
