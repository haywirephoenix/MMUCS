using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using MMUCS.Core.Diagnostics;

namespace MMUCS.Core.Scumm;

/// <summary>
/// Names for script variables. SCUMM compiles names away, so they come from two places:
/// <list type="bullet">
/// <item>the globals the v8 engine itself uses (ScummVM's ScummEngine_v8::setupScummVars), fixed here;</item>
/// <item>the game's own globals and bit variables, worked out by reading scripts and collected in
/// Names/variables.json (embedded). A variables.json in the app data folder adds to or overrides it,
/// to try names before contributing them.</item>
/// </list>
/// Anything unnamed stays varN / bitN.
/// </summary>
public static class VariableNames
{
    /// <summary>The app data copy that overrides the embedded map (read once, at first use).</summary>
    public static string LocalFile => Path.Combine(CorePaths.Root, "variables.json");

    private static readonly Dictionary<int, string> EngineVars = new()
    {
        [1] = "room-width", [2] = "room-height",
        [3] = "mouse-x", [4] = "mouse-y", [5] = "virt-mouse-x", [6] = "virt-mouse-y",
        [7] = "cursor-state", [8] = "userput",
        [9] = "camera-pos-x", [10] = "camera-pos-y", [11] = "camera-dest-x", [12] = "camera-dest-y",
        [13] = "camera-followed-actor",
        [14] = "talk-actor", [15] = "have-msg",
        [16] = "left-btn-down", [17] = "right-btn-down", [18] = "left-btn-hold", [19] = "right-btn-hold",
        [24] = "timedate-year", [25] = "timedate-month", [26] = "timedate-day",
        [27] = "timedate-hour", [28] = "timedate-minute", [29] = "timedate-second",
        [30] = "overridden", [31] = "current-room", [32] = "new-room", [33] = "walkto-obj", [34] = "timer",
        [39] = "voice-mode", [40] = "game-loaded", [41] = "language",
        [42] = "current-disk", [45] = "music-bundle-loaded", [46] = "voice-bundle-loaded",
        [50] = "scroll-script", [51] = "entry-script", [52] = "entry-script2", [53] = "exit-script",
        [54] = "exit-script2", [55] = "verb-script", [56] = "sentence-script", [57] = "inventory-script",
        [58] = "cutscene-start-script", [59] = "cutscene-end-script",
        [62] = "cutscene-exit-key", [64] = "pause-key", [65] = "main-menu-key", [66] = "version-key", [67] = "talk-stop-key",
        [111] = "custom-scale-table",
        [112] = "timer-next", [113] = "timer1", [114] = "timer2", [115] = "timer3",
        [116] = "camera-min-x", [117] = "camera-max-x", [118] = "camera-min-y", [119] = "camera-max-y",
        [120] = "camera-speed-x", [121] = "camera-speed-y", [122] = "camera-accel-x", [123] = "camera-accel-y",
        [124] = "camera-threshold-x", [125] = "camera-threshold-y",
        [126] = "ego",
        [128] = "default-talk-delay", [129] = "char-inc",
        [130] = "debug-mode", [132] = "keypress", [133] = "blast-above-text", [134] = "sync",
        [175] = "saveload-page", [176] = "object-label-flag",
    };

    /// <summary>A variable name: lower-case kebab-case, each part starting with a letter (e.g. cursor-object, timer2).</summary>
    private static readonly Regex ValidName = new(@"^[a-z][a-z0-9]*(-[a-z][a-z0-9]*)*$", RegexOptions.Compiled);
    /// <summary>Names that would read as a numbered variable.</summary>
    private static readonly Regex Reserved = new(@"^(var|local|bit)\d+$", RegexOptions.Compiled);

    private sealed class Maps
    {
        public readonly Dictionary<int, string> Globals = new(EngineVars);
        public readonly Dictionary<int, string> Bits = new();
        public readonly HashSet<string> Names = new(StringComparer.Ordinal);
    }

    private static readonly Lazy<Maps> _maps = new(Load);

    /// <summary>The name of global <paramref name="n"/>, or null.</summary>
    public static string Global(int n) => _maps.Value.Globals.TryGetValue(n, out var s) ? s : null;

    /// <summary>The name of bit variable <paramref name="n"/>, or null.</summary>
    public static string Bit(int n) => _maps.Value.Bits.TryGetValue(n, out var s) ? s : null;

    /// <summary>True for any name this gives a variable (engine or mapped).</summary>
    public static bool IsName(string name) => _maps.Value.Names.Contains(name);

    // ── Loading ──

    /// <summary>One file: { "Globals": { "266": { "Name": "...", "Note": "..." } }, "Bits": { ... } }.</summary>
    public sealed class MapFile
    {
        public Dictionary<string, Entry> Globals { get; set; }
        public Dictionary<string, Entry> Bits { get; set; }
    }

    public sealed class Entry
    {
        public string Name { get; set; }
        /// <summary>Why the name fits: where it's used, how sure it is. Not shown; for whoever checks it next.</summary>
        public string Note { get; set; }
    }

    private static Maps Load()
    {
        var maps = new Maps();
        try
        {
            using var stream = typeof(VariableNames).Assembly.GetManifestResourceStream("variables.json");
            if (stream != null) Apply(maps, JsonSerializer.Deserialize<MapFile>(stream), "built-in variables.json");
        }
        catch (Exception ex) { Log.Warn($"Built-in variable names ignored: {ex.Message}"); }
        try
        {
            if (File.Exists(LocalFile)) Apply(maps, JsonSerializer.Deserialize<MapFile>(File.ReadAllText(LocalFile)), LocalFile);
        }
        catch (Exception ex) { Log.Warn($"{LocalFile} ignored: {ex.Message}"); }
        foreach (var n in maps.Globals.Values) maps.Names.Add(n);
        foreach (var n in maps.Bits.Values) maps.Names.Add(n);
        return maps;
    }

    /// <summary>Adds a file's entries (later files override earlier ones), skipping and reporting bad ones.</summary>
    private static List<string> Apply(Maps maps, MapFile file, string source)
    {
        var problems = new List<string>();
        if (file == null) return problems;
        Merge(file.Globals, maps.Globals, maps.Bits, "global", isGlobal: true);
        Merge(file.Bits, maps.Bits, maps.Globals, "bit", isGlobal: false);
        foreach (var p in problems) Log.Warn($"{source}: {p}");
        return problems;

        void Merge(Dictionary<string, Entry> entries, Dictionary<int, string> into, Dictionary<int, string> other, string what, bool isGlobal)
        {
            if (entries == null) return;
            foreach (var (key, entry) in entries)
            {
                string name = entry?.Name?.Trim();
                if (!int.TryParse(key, out int n) || n < 0) { problems.Add($"{what} \"{key}\" isn't a number"); continue; }
                if (string.IsNullOrEmpty(name)) continue; // listed, not named yet
                if (!ValidName.IsMatch(name) || Reserved.IsMatch(name)) { problems.Add($"{what} {n}: \"{name}\" isn't a valid name (lower-case-kebab, parts start with a letter)"); continue; }
                if (isGlobal && EngineVars.ContainsKey(n)) { problems.Add($"global {n} is an engine variable ({EngineVars[n]})"); continue; }
                bool taken = false;
                foreach (var (m, existing) in into) if (m != n && existing == name) taken = true;
                foreach (var existing in other.Values) if (existing == name) taken = true;
                if (taken) { problems.Add($"{what} {n}: \"{name}\" is already used"); continue; }
                into[n] = name;
            }
        }
    }

    /// <summary>Checks a map file's text; returns its problems (empty when every entry is usable).</summary>
    public static List<string> Validate(string json)
    {
        var maps = new Maps();
        return Apply(maps, JsonSerializer.Deserialize<MapFile>(json), "check");
    }
}
