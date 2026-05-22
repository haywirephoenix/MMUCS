using System;

public class IndexedSurface
{
    public int Width;
    public int Height;
    public int Pitch;

    // Raw palette indices
    public byte[] Pixels;

    // Optional semantic info
    public byte TransparentIndex = 0;

    public int GetOffset(int x, int y)
    {
        return y * Pitch + x;
    }

    public byte GetPixel(int x, int y)
    {
        return Pixels[GetOffset(x, y)];
    }

    public void SetPixel(int x, int y, byte value)
    {
        Pixels[GetOffset(x, y)] = value;
    }
}