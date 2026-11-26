using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MMUCS.Core;

/// <summary>
/// The mods beside the game: "_mods/&lt;mod&gt;/" folders, each with a mod.json, laid out like an export, so a mod
/// holds what it replaces where the export writes it:
/// <code>
/// _mods/COMI_FLAC_Music/mod.json                 "tracks": the game's music the mod's audio stands in for
/// _mods/COMI_FLAC_Music/music/…/01 - ….flac       the audio, in any folder under music/
/// _mods/WideScreenMod/mod.json
/// _mods/WideScreenMod/rooms/022_clring-a/objects/041_clring-a-haggis-caber_f0.png   an object's image
/// </code>
/// A mod.json with "enabled": false turns its mod off. Mods are read in their folders' name order, and where two
/// replace one thing the first wins.
/// </summary>
public static class GameMods
{
    /// <summary>A mod: its folder's name, the folder, and its mod.json (null when it couldn't be read).</summary>
    public sealed record Mod(string Name, string Folder, bool Enabled, JsonDocument Json);

    /// <summary>The mods folder for <paramref name="gameFile"/>: "_mods" beside it, or null without a game file.</summary>
    public static string Folder(string gameFile) =>
        string.IsNullOrEmpty(gameFile) || Path.GetDirectoryName(Path.GetFullPath(gameFile)) is not { } dir
            ? null : Path.Combine(dir, "_mods");

    /// <summary>
    /// The mods beside <paramref name="gameFile"/> that have a mod.json, in name order, enabled or not; a mod.json
    /// that can't be read is listed in <paramref name="problems"/> and its mod left out.
    /// </summary>
    public static List<Mod> Load(string gameFile, List<string> problems = null)
    {
        var mods = new List<Mod>();
        if (Folder(gameFile) is not { } folder || !Directory.Exists(folder)) return mods;
        foreach (var dir in Directory.GetDirectories(folder).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            string map = Path.Combine(dir, "mod.json");
            if (!File.Exists(map)) continue;
            string name = Path.GetFileName(dir);
            try
            {
                var json = JsonDocument.Parse(File.ReadAllText(map), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                bool enabled = !(json.RootElement.ValueKind == JsonValueKind.Object &&
                                 json.RootElement.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.False);
                mods.Add(new Mod(name, dir, enabled, json));
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                problems?.Add($"{name}/mod.json: {ex.Message}");
            }
        }
        return mods;
    }

    /// <summary>
    /// A file a mod names, looked for under <paramref name="subfolder"/> of the mod (a path from there), then
    /// as a path from the mod's folder, then by its name in any folder under <paramref name="subfolder"/>; null if none.
    /// </summary>
    public static string Find(Mod mod, string subfolder, string file) => Find(mod.Folder, subfolder, file);

    /// <inheritdoc cref="Find(Mod, string, string)"/>
    public static string Find(string modFolder, string subfolder, string file)
    {
        if (string.IsNullOrWhiteSpace(file) || string.IsNullOrEmpty(modFolder)) return null;
        string under = Path.Combine(modFolder, subfolder);
        foreach (var path in new[] { Path.Combine(under, file), Path.Combine(modFolder, file) })
            if (File.Exists(path)) return path;
        if (!Directory.Exists(under)) return null;
        string name = Path.GetFileName(file);
        return Directory.EnumerateFiles(under, name, SearchOption.AllDirectories).Order(StringComparer.Ordinal).FirstOrDefault();
    }
}
