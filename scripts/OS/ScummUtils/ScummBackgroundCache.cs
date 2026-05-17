using System;
using System.IO;
using Godot;

public static class ScummBackgroundCache
{
    
    public static string GetBackgroundCachePath(int roomId)
    {
        string godotFilePath = $"user://bgcache/room_{roomId}.bin";
        return FileUtils.GetOrCreatePath(godotFilePath);
    }

    private const int HeaderSize = 12;
    
    public static bool TryLoadFromCache(int roomId, 
        out int width,
        out int height,
        out int pitch,
        out byte[] indexedPixels)
    {
        string cachePath = GetBackgroundCachePath(roomId);

        indexedPixels = null;

        if (TryLoadIndexedBackground(
            cachePath,
            out width,
            out height,
            out pitch,
            out indexedPixels))
        {
            GD.Print("Loaded background from cache");
        }


        return indexedPixels != null;
    }
    
    public static void SaveIndexedBackground(
        string cachePath,
        int width,
        int height,
        int pitch,
        byte[] indexedPixels)
    {
        try
        {
            string dir = Path.GetDirectoryName(cachePath);

            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            using var fs = new FileStream(
                cachePath,
                FileMode.Create,
                System.IO.FileAccess.Write,
                FileShare.None);

            using var bw = new BinaryWriter(fs);

            bw.Write(width);
            bw.Write(height);
            bw.Write(pitch);

            bw.Write(indexedPixels);

            bw.Flush();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"Failed to save background cache: {ex}");
        }
    }
    
    public static bool TryLoadIndexedBackground(
        string cachePath,
        out int width,
        out int height,
        out int pitch,
        out byte[] indexedPixels)
    {
        width = 0;
        height = 0;
        pitch = 0;
        indexedPixels = null;

        try
        {
            if (!File.Exists(cachePath))
                return false;

            using var fs = new FileStream(
                cachePath,
                FileMode.Open,
                System.IO.FileAccess.Read,
                FileShare.Read);

            using var br = new BinaryReader(fs);

            if (fs.Length < HeaderSize)
                return false;

            width = br.ReadInt32();
            height = br.ReadInt32();
            pitch = br.ReadInt32();

            // sanity
            if (width <= 0 || width > 4096)
                return false;

            if (height <= 0 || height > 4096)
                return false;

            if (pitch < width || pitch > 8192)
                return false;

            int expectedSize = pitch * height;

            long remaining = fs.Length - HeaderSize;

            if (remaining != expectedSize)
            {
                GD.PrintErr(
                    $"Cache size mismatch. Expected {expectedSize}, got {remaining}");
                return false;
            }

            indexedPixels = br.ReadBytes(expectedSize);

            return indexedPixels.Length == expectedSize;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"Failed to load background cache: {ex}");
            return false;
        }
    }
    
    public static Image CreateImageFromIndexed(
        byte[] indexedPixels,
        int width,
        int height,
        int pitch,
        ReadOnlySpan<byte> palette)
    {
        try
        {
            // Remove pitch padding if needed
            byte[] sanitizedPixels;

            if (pitch == width)
            {
                sanitizedPixels = indexedPixels;
            }
            else
            {
                sanitizedPixels = new byte[width * height];

                for (int y = 0; y < height; y++)
                {
                    Buffer.BlockCopy(
                        indexedPixels,
                        y * pitch,
                        sanitizedPixels,
                        y * width,
                        width);
                }
            }

            byte[] rgba = new byte[width * height * 4];

            for (int i = 0; i < sanitizedPixels.Length; i++)
            {
                int palIndex = sanitizedPixels[i] * 3;
                int outIndex = i * 4;

                if (palIndex + 2 >= palette.Length)
                    continue;

                rgba[outIndex + 0] = palette[palIndex + 0];
                rgba[outIndex + 1] = palette[palIndex + 1];
                rgba[outIndex + 2] = palette[palIndex + 2];
                rgba[outIndex + 3] = 255;
            }

            return Image.CreateFromData(
                width,
                height,
                false,
                Image.Format.Rgba8,
                rgba);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"Failed to create image from indexed data: {ex}");
            return null;
        }
    }
}