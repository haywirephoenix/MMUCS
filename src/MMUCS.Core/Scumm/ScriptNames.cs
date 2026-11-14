using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using MMUCS.Core.Diagnostics;

namespace MMUCS.Core.Scumm;

/// <summary>
/// Names for scripts, which SCUMM knows only by number: global scripts by their number, local scripts
/// by room and number. They come from Names/scripts.json (embedded); a scripts.json in the app data
/// folder adds to or overrides it. Files and citations keep the numbers (local_2013.scumm); the name is
/// shown beside them.
/// </summary>
public static class ScriptNames
{
    /// <summary>The app data copy that overrides the embedded map (read once, at first use).</summary>
    public static string LocalFile => Path.Combine(CorePaths.Root, "scripts.json");

    /// <summary>Lower-case kebab-case, each part starting with a letter or digit after the first.</summary>
    private static readonly Regex ValidName = new(@"^[a-z][a-z0-9]*(-[a-z0-9]+)*$", RegexOptions.Compiled);

    private sealed class Maps
    {
        public readonly Dictionary<int, string> Globals = new();
        public readonly Dictionary<(int Room, int No), string> Locals = new();
    }

    private static readonly Lazy<Maps> _maps = new(Load);

    /// <summary>The name of global script <paramref name="no"/>, or null.</summary>
    public static string Global(int no) => _maps.Value.Globals.TryGetValue(no, out var s) ? s : null;

    /// <summary>The name of local script <paramref name="no"/> of room <paramref name="room"/>, or null.</summary>
    public static string Local(int room, int no) => _maps.Value.Locals.TryGetValue((room, no), out var s) ? s : null;

    /// <summary>One file: { "Globals": { "88": { "Name": "...", "Note": "..." } }, "Locals": { "14:2013": { ... } } }.</summary>
    public sealed class MapFile
    {
        public Dictionary<string, VariableNames.Entry> Globals { get; set; }
        public Dictionary<string, VariableNames.Entry> Locals { get; set; }
    }

    private static Maps Load()
    {
        var maps = new Maps();
        try
        {
            using var stream = typeof(ScriptNames).Assembly.GetManifestResourceStream("scripts.json");
            if (stream != null) Apply(maps, JsonSerializer.Deserialize<MapFile>(stream), "built-in scripts.json");
        }
        catch (Exception ex) { Log.Warn($"Built-in script names ignored: {ex.Message}"); }
        try
        {
            if (File.Exists(LocalFile)) Apply(maps, JsonSerializer.Deserialize<MapFile>(File.ReadAllText(LocalFile)), LocalFile);
        }
        catch (Exception ex) { Log.Warn($"{LocalFile} ignored: {ex.Message}"); }
        return maps;
    }

    private static List<string> Apply(Maps maps, MapFile file, string source)
    {
        var problems = new List<string>();
        if (file == null) return problems;
        foreach (var (key, entry) in file.Globals ?? new())
        {
            string name = entry?.Name?.Trim();
            if (string.IsNullOrEmpty(name)) continue;
            if (!int.TryParse(key, out int no) || no < 0) problems.Add($"global script \"{key}\" isn't a number");
            else if (!ValidName.IsMatch(name)) problems.Add($"global script {no}: \"{name}\" isn't a valid name");
            else if (maps.Globals.Any(g => g.Key != no && g.Value == name)) problems.Add($"global script {no}: \"{name}\" is already used");
            else maps.Globals[no] = name;
        }
        foreach (var (key, entry) in file.Locals ?? new())
        {
            string name = entry?.Name?.Trim();
            if (string.IsNullOrEmpty(name)) continue;
            var parts = key.Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[0], out int room) || !int.TryParse(parts[1], out int no))
                problems.Add($"local script \"{key}\" isn't ROOM:NUMBER");
            else if (!ValidName.IsMatch(name)) problems.Add($"local script {key}: \"{name}\" isn't a valid name");
            // Local scripts are numbered per room, so a name needs to be unique only there.
            else if (maps.Locals.Any(l => l.Key.Room == room && l.Key.No != no && l.Value == name)) problems.Add($"local script {key}: \"{name}\" is already used in room {room}");
            else maps.Locals[(room, no)] = name;
        }
        foreach (var p in problems) Log.Warn($"{source}: {p}");
        return problems;
    }

    /// <summary>Checks a map file's text; returns its problems (empty when every entry is usable).</summary>
    public static List<string> Validate(string json) => Apply(new Maps(), JsonSerializer.Deserialize<MapFile>(json), "check");
}
