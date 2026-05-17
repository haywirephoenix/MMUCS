using System;
using System.IO;
using Godot;

public static class AkosCelCache
{
    private const int HeaderSize = 12;
    
    public static string GetCelCachePath(long celIndex)
    {
        return $"akoscache/cel_{celIndex}.bin";
    }
    
    public static long BuildCelCacheKey(
        AkosData akos,
        int celIndex)
    {
        var cel = akos.CelOffsets[celIndex];

        return HashCode.Combine(
            akos.Offset,
            celIndex,
            cel.AkcdOffset);
    }

    public static void Save(
        string path,
        IndexedSurface surface)
    {
        try
        {
            string dir = Path.GetDirectoryName(path);

            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            using var fs = new FileStream(
                path,
                FileMode.Create,
                System.IO.FileAccess.Write,
                FileShare.None);

            using var bw = new BinaryWriter(fs);

            bw.Write(surface.Width);
            bw.Write(surface.Height);
            bw.Write(surface.Pitch);

            bw.Write(surface.Pixels);

            bw.Flush();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"AKOS cache save failed: {ex}");
        }
    }

    public static bool TryLoad(
        string path,
        out IndexedSurface surface)
    {
        surface = null;

        try
        {
            if (!File.Exists(path))
                return false;

            using var fs = new FileStream(
                path,
                FileMode.Open,
                System.IO.FileAccess.Read,
                FileShare.Read);

            using var br = new BinaryReader(fs);

            if (fs.Length < HeaderSize)
                return false;

            int width = br.ReadInt32();
            int height = br.ReadInt32();
            int pitch = br.ReadInt32();

            int expected = pitch * height;

            byte[] pixels = br.ReadBytes(expected);

            if (pixels.Length != expected)
                return false;

            surface = new IndexedSurface
            {
                Width = width,
                Height = height,
                Pitch = pitch,
                Pixels = pixels
            };

            return true;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"AKOS cache load failed: {ex}");
            return false;
        }
    }
}