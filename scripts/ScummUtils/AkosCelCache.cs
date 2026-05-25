using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Godot;

public static class AkosCelCache
{
    private const int HeaderSize = 12;
    private const string akosCachePath = "user://akoscache/";
    
    public static string GetCelCachePath(long celKey)
    {
        string godotFilePath = $"{akosCachePath}cel_{celKey}.bin";
        return FileUtils.GetOrCreatePath(godotFilePath); 
    }
    
    public static async Task<IndexedSurface> GetCachedCelAsync(AkosData akos, int index, CancellationToken token)
    {
        long key = AkosCelCache.BuildCelCacheKey(akos, index);

        if (akos.DecodedCels.TryGetValue(key, out var surface))
        {
            return surface;
        }

        surface = await Task.Run(() => 
        {
            if (token.IsCancellationRequested) return null;

            string path = GetCelCachePath(key);

            if (TryLoad(path, out var diskSurface))
            {
                return diskSurface;
            }
            
            if (token.IsCancellationRequested) return null;
            var decodedSurface = AkosDecoders.DecodeCel(akos, index, token);

            if (decodedSurface != null && !token.IsCancellationRequested)
            {
                Save(path, decodedSurface);
            }

            return decodedSurface;
        }, token);

        if (surface != null && !token.IsCancellationRequested)
        {
            akos.DecodedCels[key] = surface;
        }

        return surface;
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

    public static void Save(string path, IndexedSurface surface)
    {
        try
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            using var fs = new FileStream(path, FileMode.Create, System.IO.FileAccess.Write, FileShare.None);
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

    public static bool TryLoad(string path, out IndexedSurface surface)
    {
        surface = null;
        try
        {
            if (!File.Exists(path))
                return false;

            using var fs = new FileStream(path, FileMode.Open, System.IO.FileAccess.Read, FileShare.Read);
            using var br = new BinaryReader(fs);

            if (fs.Length < HeaderSize)
                return false;

            int width = br.ReadInt32();
            int height = br.ReadInt32();
            int pitch = br.ReadInt32();

            // Sanity
            if (width <= 0 || width > 4096 || height <= 0 || height > 4096)
                return false;

            int expected = pitch * height;
            if (fs.Length - HeaderSize != expected)
                return false;

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
    
    public static void ClearRamCache(AkosData akos)
    {
        if (akos?.DecodedCels != null)
        {
            akos.DecodedCels.Clear();
        }
    }
    public static void ClearDiskCache()
    {
        try
        {
            string globalPath = ProjectSettings.GlobalizePath(akosCachePath);

            if (Directory.Exists(globalPath))
            {
                string[] files = Directory.GetFiles(globalPath, "cel_*.bin");
                foreach (string file in files)
                {
                    File.Delete(file);
                }
                StatusBar.SetStatus("Akos Cel disk cache cleared successfully.");
            }
        }
        catch (Exception ex)
        {
            StatusBar.SetStatus($"Failed to clear Akos disk cache: {ex.Message}", StatusBar.EStatusType.Error);
        }
    }
    
}