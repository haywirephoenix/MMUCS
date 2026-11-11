using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MMUCS.Core.Diagnostics;
using MMUCS.Core.Imaging;

namespace MMUCS.Core.Scumm;


public static class AkosCelCache
{
    public readonly struct CelResult
    {
        public IndexedSurface Surface { get; }
        public RgbaImage ShaderDataImage { get; }
        public RgbaImage RawUnshadedImage { get; }

        public CelResult(IndexedSurface surface, RgbaImage shaderDataImage = null, RgbaImage rawUnshadedImage = null)
        {
            Surface          = surface;
            ShaderDataImage  = shaderDataImage;
            RawUnshadedImage = rawUnshadedImage;
        }
    }
    
    private const int HeaderSize = 12;
    private const string CacheBucket = "akos";
    
    public static string GetCelCachePath(long celKey)
    {
        return CorePaths.CacheFile(CacheBucket, $"cel_{celKey}.bin");
    }
    
    public static async Task<CelResult> GetCachedCelAsync(
        AkosData akos, 
        int index, 
        bool bakeImages,
        bool cacheDisabled,
        CancellationToken token)
    {
        long key = BuildCelCacheKey(akos, index);

        if (!cacheDisabled && akos.DecodedCels.TryGetValue(key, out var cachedSurface))
        {
            var (dataImg, finalImg) = ResolveCelImages(cachedSurface, akos, bakeImages);
            return new CelResult(cachedSurface, dataImg, finalImg);
        }

        var surface = await Task.Run(() => 
        {
            if (token.IsCancellationRequested) return null;

            string path = GetCelCachePath(key);

            if (TryLoad(path, out var diskSurface))
                return diskSurface;
        
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
            var (dataImg, finalImg) = ResolveCelImages(surface, akos, bakeImages);
            return new CelResult(surface, dataImg, finalImg);
        }

        return default;
    }

    private static (RgbaImage shaderDataImg, RgbaImage rawUnshadedImg) ResolveCelImages(IndexedSurface surface, AkosData akos, bool bakeImages)
    {
        if (!bakeImages || surface == null) 
            return (null, null);

        RgbaImage shaderData = IndexedRenderer.CreateDataImage(surface, akos);
        RgbaImage rawUnshaded = IndexedRenderer.CreateImage(surface, akos);

        return (shaderData, rawUnshaded);
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

            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            using var bw = new BinaryWriter(fs);

            bw.Write(surface.Width);
            bw.Write(surface.Height);
            bw.Write(surface.Pitch);
            bw.Write(surface.Pixels);
            bw.Flush();
        }
        catch (Exception ex)
        {
            Log.Error($"AKOS cache save failed: {ex}");
        }
    }

    public static bool TryLoad(string path, out IndexedSurface surface)
    {
        surface = null;
        try
        {
            if (!File.Exists(path))
                return false;

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
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
            Log.Error($"AKOS cache load failed: {ex}");
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
            string globalPath = CorePaths.CacheDir(CacheBucket);

            if (Directory.Exists(globalPath))
            {
                string[] files = Directory.GetFiles(globalPath, "cel_*.bin");
                foreach (string file in files)
                {
                    File.Delete(file);
                }
                Log.Info("Akos Cel disk cache cleared successfully.");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to clear Akos disk cache: {ex.Message}");
        }
    }
    
}