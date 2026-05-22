using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Godot;

public static class ScummBackgroundCache
{
    private static readonly Dictionary<int, IndexedSurface> RamCache = new();
    public static string GetBackgroundCachePath(int roomId)
    {
        string godotFilePath = $"user://bgcache/room_{roomId}.bin";
        return FileUtils.GetOrCreatePath(godotFilePath);
    }

    private const int HeaderSize = 12;
    
    public static async Task<IndexedSurface> GetCachedBackgroundAsync(
        int roomId, 
        ScummBlock rmhd,
        ScummBlock room,
        bool cacheDisabled,
        CancellationToken token)
    {
        if (!cacheDisabled && RamCache.TryGetValue(roomId, out var surface))
        {
            return surface;
        }

        surface = await Task.Run(() =>
        {
            if (token.IsCancellationRequested) return null;
            string path = GetBackgroundCachePath(roomId);

            if (!cacheDisabled && TryLoadIndexedBackground(path, out var diskSurface))
            {
                return diskSurface;
            }

            if (token.IsCancellationRequested) return null;
            
            if (ScummDecoders.DecodeBackgroundImage(roomId, rmhd, room, token, 
                out Image _, out byte[] rawBuffer, out int pitch))
            {
                int width = (int)rmhd.GetMetadataItem(ScummMeta.RMHD.width);
                int height = (int)rmhd.GetMetadataItem(ScummMeta.RMHD.height);

                var freshlyDecoded = new IndexedSurface
                {
                    Width = width,
                    Height = height,
                    Pitch = pitch,
                    Pixels = rawBuffer
                };

                if (!cacheDisabled && !token.IsCancellationRequested)
                    SaveIndexedBackground(path, freshlyDecoded);
                
                return freshlyDecoded;
            }

            return null;
        }, token);

        if (surface != null && !cacheDisabled && !token.IsCancellationRequested)
            RamCache[roomId] = surface;

        return surface;
    }
    
    private static void SaveIndexedBackground(string cachePath, IndexedSurface surface)
    {
        try
        {
            string dir = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            using var fs = new FileStream(cachePath, FileMode.Create, System.IO.FileAccess.Write, FileShare.None);
            using var bw = new BinaryWriter(fs);

            bw.Write(surface.Width);
            bw.Write(surface.Height);
            bw.Write(surface.Pitch);
            bw.Write(surface.Pixels);
            bw.Flush();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"Failed to save background cache: {ex}");
        }
    }
    
    private static bool TryLoadIndexedBackground(string cachePath, out IndexedSurface surface)
    {
        surface = null;
        try
        {
            if (!File.Exists(cachePath))
                return false;

            using var fs = new FileStream(cachePath, FileMode.Open, System.IO.FileAccess.Read, FileShare.Read);
            using var br = new BinaryReader(fs);

            if (fs.Length < HeaderSize)
                return false;

            int width = br.ReadInt32();
            int height = br.ReadInt32();
            int pitch = br.ReadInt32();

            if (width <= 0 || width > 4096 || height <= 0 || height > 4096)
                return false;

            int expectedSize = pitch * height;
            if (fs.Length - HeaderSize != expectedSize)
                return false;

            byte[] pixels = br.ReadBytes(expectedSize);
            if (pixels.Length != expectedSize)
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
            GD.PrintErr($"Failed to load background cache: {ex}");
            return false;
        }
    }
    
    public static void ClearRamCache()
    {
        RamCache.Clear();
    }
    
    
}