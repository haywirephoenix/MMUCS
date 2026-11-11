using System;
using System.IO;

namespace MMUCS.Core;

/// <summary>
/// Replaces Godot's user:// paths. Defaults to %LocalAppData%/MMUCS (or ~/.local/share/MMUCS).
/// </summary>
public static class CorePaths
{
    private static string _root;

    public static string Root
    {
        get => _root ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MMUCS");
        set => _root = value;
    }

    /// <summary>Returns the absolute directory for a cache bucket, creating it if needed.</summary>
    public static string CacheDir(string bucket)
    {
        string dir = Path.Combine(Root, "cache", bucket);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string CacheFile(string bucket, string fileName) => Path.Combine(CacheDir(bucket), fileName);
}
