using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Godot;

public static class ScummBackgroundCache
{
    public readonly struct BackgroundResult(IndexedSurface surface, byte[] paletteBytes, Image executedImage = null)
    {
        public IndexedSurface Surface { get; } = surface;
        public byte[] PaletteBytes { get; } = paletteBytes;
        public Image ExecutedImage { get; } = executedImage;
    }
    
    private const string bgCachePath = "user://bgcache/";
    
    private static readonly Dictionary<int, BackgroundResult> RamCache = new();
    private const int HeaderSize = 12;
    public static string GetBackgroundCachePath(int roomId)
    {
        string godotFilePath = $"{bgCachePath}room_{roomId}.bin";
        return FileUtils.GetOrCreatePath(godotFilePath);
    }

    
    public static async Task<BackgroundResult> GetCachedBackgroundAsync(
    int roomId, 
    ScummBlock rmhd,
    ScummBlock room,
    bool cacheDisabled,
    int paletteIndex,
    CancellationToken token)
    {
        if (!cacheDisabled && RamCache.TryGetValue(roomId, out var cachedResult))
        {
            var (paletteBytes, outImage) = RoomDecoders.ResolveImageAndPalette(room, rmhd, cachedResult.Surface, paletteIndex);
            return new BackgroundResult(cachedResult.Surface, paletteBytes, outImage);
        }

        var result = await Task.Run(() =>
        {
            if (token.IsCancellationRequested) return default;
            string path = GetBackgroundCachePath(roomId);
            
            if (!cacheDisabled && TryLoadIndexedBackground(path, out var diskSurface))
            {
                var (paletteBytes, outImage) = RoomDecoders.ResolveImageAndPalette(room, rmhd, diskSurface, paletteIndex);
                return new BackgroundResult(diskSurface, paletteBytes, outImage);
            }

            if (token.IsCancellationRequested) return default;
            
            if (RoomDecoders.DecodeBackgroundImage(roomId, rmhd, room, paletteIndex, token, 
                out Image backgroundImage, out byte[] rawBuffer, out int pitch, out ReadOnlySpan<byte> roomPalette, generateImage: true))
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
                
                return new BackgroundResult(freshlyDecoded, roomPalette.ToArray(), backgroundImage);
            }

            return default;
        }, token);
        
        if (result.Surface != null && !cacheDisabled && !token.IsCancellationRequested)
            RamCache[roomId] = result;

        return result;
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
    
    public static void ClearDiskCache()
    {
        try
        {
            RamCache.Clear();
            string globalPath = ProjectSettings.GlobalizePath(bgCachePath);

            if (Directory.Exists(globalPath))
            {
                string[] files = Directory.GetFiles(globalPath, "room_*.bin");
                foreach (string file in files)
                {
                    File.Delete(file);
                }
                StatusBar.SetStatus("Scumm Background disk cache cleared successfully.");
            }
        }
        catch (Exception ex)
        {
            StatusBar.SetStatus($"Failed to clear background disk cache: {ex.Message}", StatusBar.EStatusType.Error);
        }
    }
}