using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MMUCS.Core;
using MMUCS.Core.Diagnostics;

namespace MMUCS.Desktop.Services;

/// <summary>User preferences persisted as JSON in %LocalAppData%/MMUCS/settings.json.</summary>
public sealed class AppSettings
{
    public const int MaxRecentFiles = 10;

    public List<string> RecentFiles { get; set; } = new();
    public bool ReopenLastFileOnStartup { get; set; } = true;
    public bool ShowRoomNames { get; set; } = true;

    private static string FilePath => Path.Combine(CorePaths.Root, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Warn($"Settings could not be read, using defaults: {ex.Message}");
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(CorePaths.Root);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Warn($"Settings could not be saved: {ex.Message}");
        }
    }

    /// <summary>Moves (or inserts) a path to the top of the recent list.</summary>
    public void AddRecent(string path)
    {
        string full = Path.GetFullPath(path);
        RecentFiles.RemoveAll(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, full);
        if (RecentFiles.Count > MaxRecentFiles) RecentFiles.RemoveRange(MaxRecentFiles, RecentFiles.Count - MaxRecentFiles);
    }

    public string LastFile => RecentFiles.FirstOrDefault();
}
