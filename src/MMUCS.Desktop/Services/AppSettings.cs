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

    /// <summary>Bumped when defaults change in a way existing settings files should pick up.</summary>
    public int SettingsVersion { get; set; }
    private const int CurrentSettingsVersion = 1;

    public List<string> RecentFiles { get; set; } = new();
    public bool ReopenLastFileOnStartup { get; set; } = true;
    public bool ShowRoomNames { get; set; } = true;
    public bool ShowObjectNames { get; set; } = true;
    public bool ColorizeTags { get; set; } = true;
    /// <summary>Script viewer shows decompiled SCUMM (true) or the instruction listing.</summary>
    public bool ScriptShowScumm { get; set; } = true;
    /// <summary>Per opened game (its first resource file): the selected hierarchy block, as child indices from the root.</summary>
    public System.Collections.Generic.Dictionary<string, string> LastSelection { get; set; } = new();
    /// <summary>Manual object → costume links ("room/object name" → costume number, 0 = none).</summary>
    public System.Collections.Generic.Dictionary<string, int> CostumeLinks { get; set; } = new();

    // Export wizard
    public string LastExportFolder { get; set; }
    public bool ExportBackgrounds { get; set; } = true;
    public bool ExportObjects { get; set; } = true;
    public bool ExportCostumes { get; set; } = true;
    public bool ExportAllBackgroundPalettes { get; set; }
    public bool CelAlphaEnabled { get; set; } = true;
    public bool CelShadowColorEnabled { get; set; } = true;
    public double CelShadowAmount { get; set; } = 0.4;
    public bool CelLinearFilter { get; set; }

    private static string FilePath => Path.Combine(CorePaths.Root, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings();
                s.Migrate();
                return s;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Settings could not be read, using defaults: {ex.Message}");
        }
        return new AppSettings { SettingsVersion = CurrentSettingsVersion };
    }

    private void Migrate()
    {
        if (SettingsVersion >= CurrentSettingsVersion) return;
        // v1: object names now default on (older files saved "false" while the options menu couldn't toggle it).
        if (SettingsVersion < 1) ShowObjectNames = true;
        SettingsVersion = CurrentSettingsVersion;
        Save();
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
