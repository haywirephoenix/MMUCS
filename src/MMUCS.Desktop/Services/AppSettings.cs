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
    private const int CurrentSettingsVersion = 3;

    public List<string> RecentFiles { get; set; } = [];
    public bool ReopenLastFileOnStartup { get; set; } = true;
    /// <summary>The game window eases its camera instead of SCUMM's steps (on by default; the window's Smooth camera button).</summary>
    public bool SmoothCamera { get; set; } = true;
    /// <summary>The game window shows its status line (on by default; the Options flyout's Show Info).</summary>
    public bool ShowInfo { get; set; } = true;
    /// <summary>The game plays the music mods beside it in place of its tracks (on by default; the Options flyout's Music Mods).</summary>
    public bool MusicModsEnabled { get; set; } = true;
    /// <summary>Walking actors glide between frames, with the smooth camera (on by default; the window's Smooth walk button).</summary>
    public bool SmoothWalk { get; set; } = true;
    public bool ShowRoomNames { get; set; } = true;
    public bool ShowObjectNames { get; set; } = true;
    public bool ColorizeTags { get; set; } = true;
    /// <summary>Script viewer shows decompiled SCUMM (true) or the instruction listing.</summary>
    public bool ScriptShowScumm { get; set; } = true;
    /// <summary>Script viewer shows byte offsets in the gutter.</summary>
    public bool ScriptShowOffsets { get; set; } = true;
    /// <summary>Script viewer code font size.</summary>
    public double ScriptFontSize { get; set; } = 12;
    /// <summary>Costume viewer: also play the chore in the Room viewer.</summary>
    public bool CostumePlayInPlace { get; set; } = true;
    /// <summary>Title bars and the Preview tab inside the viewers (Layout menu; shown to rearrange their panels).</summary>
    public bool ShowViewerPanelHeaders { get; set; }
    /// <summary>The Inspector's Metadata section is open.</summary>
    public bool InspectorMetadataExpanded { get; set; } = true;
    /// <summary>The Inspector's Costume / Object section is open.</summary>
    public bool InspectorViewerExpanded { get; set; } = true;
    /// <summary>The Inspector's Position section is open.</summary>
    public bool InspectorPositionExpanded { get; set; } = true;
    /// <summary>The Room viewer's View menu and Objects panel toggle.</summary>
    public RoomViewOptions RoomView { get; set; } = new();
    /// <summary>Per opened game (its first resource file): the selected hierarchy block, as child indices from the root.</summary>
    public System.Collections.Generic.Dictionary<string, string> LastSelection { get; set; } = [];
    /// <summary>Per opened game: the room the Room viewer showed (its LFLF, as child indices from the root).</summary>
    public System.Collections.Generic.Dictionary<string, string> LastRoom { get; set; } = [];
    /// <summary>Manual object → costume links ("room/object name" → costume number, 0 = none).</summary>
    public System.Collections.Generic.Dictionary<string, int> CostumeLinks { get; set; } = [];

    // Export wizard
    public string LastExportFolder { get; set; }
    public bool ExportBackgrounds { get; set; } = true;
    public bool ExportObjects { get; set; } = true;
    public bool ExportCostumes { get; set; } = true;
    public bool ExportAllBackgroundPalettes { get; set; }
    public bool ExportScripts { get; set; }
    public bool ExportScriptOffsets { get; set; }
    public bool ExportRoomData { get; set; }
    public bool ExportCutscenes { get; set; }
    public bool ExportAudio { get; set; }
    public bool ExportFonts { get; set; }
    // Media player: cutscene text
    public bool ShowSubtitles { get; set; } = true;
    /// <summary>"game", "folder" or "typeface"; see <see cref="MMUCS.Player.Text.SubtitleFontKind"/>.</summary>
    public string SubtitleFontKind { get; set; } = "game";
    /// <summary>The font folder or typeface file last chosen.</summary>
    public string SubtitleFontPath { get; set; }
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
        if (SettingsVersion >= CurrentSettingsVersion)
        {
            return;
        }
        // v1: object names now default on (older files saved "false" while the options menu couldn't toggle it).
        if (SettingsVersion < 1)
        {
            ShowObjectNames = true;
        }
        // v2: Play in place became on by default (earlier files saved it off before it was ever offered on).
        if (SettingsVersion < 2)
        {
            CostumePlayInPlace = true;
        }
        // v3: the smooth camera became on by default (earlier files saved it off before it was offered).
        if (SettingsVersion < 3)
        {
            SmoothCamera = true;
        }

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
        if (RecentFiles.Count > MaxRecentFiles)
        {
            RecentFiles.RemoveRange(MaxRecentFiles, RecentFiles.Count - MaxRecentFiles);
        }
    }

    public string LastFile => RecentFiles.FirstOrDefault();
}

/// <summary>Room viewer display choices (defaults match a fresh viewer).</summary>
public sealed class RoomViewOptions
{
    public bool ShowBackground { get; set; } = true;
    // ShowObjects and ShowUnmatchedCostumes are no longer used (the Show mode replaced those toggles).
    // Kept so settings files that have them still load.
    public bool ShowObjects { get; set; } = true;
    public bool ShowFullScreenObjects { get; set; }
    public bool ShowUnmatchedCostumes { get; set; }
    public bool ShowHotspots { get; set; }
    public bool ShowWalkboxes { get; set; }
    public bool ShowLabels { get; set; } = true;
    public bool ShowBounds { get; set; }
    public bool ShowSelectionOutline { get; set; } = true;
    public bool ShowObjectsPanel { get; set; } = true;
    public bool ZPlaneMasking { get; set; } = true;
    public bool ShowZPlanes { get; set; }
    /// <summary>What each room starts with: 0 Room start, 1 Everything, 2 Hidden at start, 3 None.</summary>
    public int ShowMode { get; set; }
    /// <summary>Include the art the game never shows (unused-art.json).</summary>
    public bool ShowUnused { get; set; }
    /// <summary>Show the mods' object images (_mods/&lt;mod&gt;/rooms/…) in place of the game's.</summary>
    public bool ShowModImages { get; set; } = true;
}
