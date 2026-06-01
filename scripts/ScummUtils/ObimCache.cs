using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Godot;

public static class ObimCache
{
    private const int HeaderSize = 12;
    private const string ObimCachePath = "user://obimcache/";

    public static string GetObimCachePath(long uniqueKey)
    {
        string godotFilePath = $"{ObimCachePath}obj_{uniqueKey}.bin";
        return FileUtils.GetOrCreatePath(godotFilePath);
    }

    public static long BuildObimCacheKey(ScummBlock objectBlock, int frameIndex)
    {
        return HashCode.Combine(objectBlock.Offset, frameIndex);
    }

    public static async Task<ObimDecoders.ObimResult> GetCachedObimAsync(
        ScummBlock objectBlock,
        int frameIndex,
        int paletteIndex,
        bool bakeImage,
        CancellationToken token)
    {
        long key = BuildObimCacheKey(objectBlock, frameIndex);

        var room = objectBlock.FindParent(ScummTag.ROOM);
        if (room == null) return default;

        var (paletteBytes, resolvedPalette) = ExtractPaletteContext(room, paletteIndex);
        int transparentIndex = GetTransparentIndex(room, objectBlock, frameIndex);
        
        var result = await Task.Run(() =>
        {
            if (token.IsCancellationRequested) return default;
            string path = GetObimCachePath(key);

            if (TryLoadIndexedSurface(path, out var diskSurface))
            {
                Image image = bakeImage ? ResolveObimImage(diskSurface, paletteBytes, transparentIndex) : null;
                return new ObimDecoders.ObimResult(diskSurface, resolvedPalette, image);
            }

            if (token.IsCancellationRequested) return default;

            if (ObimDecoders.TryDecodeObimFrame(objectBlock, token, frameIndex, paletteIndex, out var decodedRaw, out _, generateImage: false))
            {
                var freshlyDecoded = new IndexedSurface
                {
                    Width = decodedRaw.Width,
                    Height = decodedRaw.Height,
                    Pitch = decodedRaw.Pitch,
                    Pixels = decodedRaw.IndexedPixels
                };

                if (!token.IsCancellationRequested)
                    SaveIndexedSurface(path, freshlyDecoded);

                Image image = bakeImage ? ResolveObimImage(freshlyDecoded, paletteBytes, transparentIndex) : null;
                return new ObimDecoders.ObimResult(freshlyDecoded, resolvedPalette, image);
            }

            return default;
        }, token);

        return result;
    }

    public static (byte[] rawBytes, Color[] colors) ExtractPaletteContext(ScummBlock room, int paletteIndex)
    {
        var apals = room?.FindChild(ScummTag.PALS)?.FindChild(ScummTag.WRAP)?.FindChildrenRecursive(ScummTag.APAL);
        if (apals == null || apals.Count == 0) return (null, null);

        var apal = apals[Mathf.Clamp(paletteIndex, 0, apals.Count - 1)];
        return (apal.DataSpan.ToArray(), ScummDecoders.ExtractPalette(apal));
    }

    public static int GetTransparentIndex(ScummBlock room, ScummBlock objectBlock, int frameIndex)
    {
        var imag = objectBlock.FindChild(ScummTag.IMAG);
        bool hasSmap = imag?.FindChildRecursive(ScummTag.SMAP) != null;
        
        if (hasSmap)
        {
            var rmhdSpan = room.FindChild(ScummTag.RMHD).DataSpan;
            return rmhdSpan != null ? (int)rmhdSpan.U32LE(20) : 0;
        }
        
        return 255;
    }

    public static Image ResolveObimImage(IndexedSurface surface, byte[] paletteBytes, int transparentIndex)
    {
        if (surface == null || paletteBytes == null) return null;
        return ScummImageUtils.CreateGodotImage(surface.Pixels, surface.Width, surface.Height, surface.Pitch, paletteBytes, (byte)transparentIndex);
    }

    // --- Standard Binary File Operations IO Mirroring Akos/Background layouts ---

    private static void SaveIndexedSurface(string path, IndexedSurface surface)
    {
        try
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            using var fs = new FileStream(path, FileMode.Create, System.IO.FileAccess.Write, FileShare.None);
            using var bw = new BinaryWriter(fs);
            bw.Write(surface.Width);
            bw.Write(surface.Height);
            bw.Write(surface.Pitch);
            bw.Write(surface.Pixels);
        }
        catch (Exception ex) { GD.PrintErr($"OBIM cache save failed: {ex}"); }
    }

    private static bool TryLoadIndexedSurface(string path, out IndexedSurface surface)
    {
        surface = null;
        try
        {
            if (!File.Exists(path)) return false;
            using var fs = new FileStream(path, FileMode.Open, System.IO.FileAccess.Read, FileShare.Read);
            using var br = new BinaryReader(fs);

            if (fs.Length < HeaderSize) return false;
            int w = br.ReadInt32();
            int h = br.ReadInt32();
            int p = br.ReadInt32();

            int expected = p * h;
            if (fs.Length - HeaderSize != expected) return false;

            surface = new IndexedSurface { Width = w, Height = h, Pitch = p, Pixels = br.ReadBytes(expected) };
            return true;
        }
        catch (Exception ex) { GD.PrintErr($"OBIM cache load failed: {ex}"); return false; }
    }
}