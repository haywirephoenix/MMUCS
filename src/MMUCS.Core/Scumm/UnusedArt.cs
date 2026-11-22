using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MMUCS.Core.Diagnostics;

namespace MMUCS.Core.Scumm;

/// <summary>
/// Object images the game data holds but the game never shows (a drawer no script opens, an icon never
/// given out), listed by room and object name. They come from Names/unused-art.json (embedded); an
/// unused-art.json in the app data folder adds more. The room viewer leaves them out unless asked.
/// </summary>
public static class UnusedArt
{
    public static string LocalFile => Path.Combine(CorePaths.Root, "unused-art.json");

    public sealed class Entry
    {
        public int Room { get; set; }
        public string Object { get; set; }
        public string Note { get; set; }
    }

    public sealed class ListFile
    {
        public List<Entry> Objects { get; set; }
    }

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
    private static Dictionary<(int, string), Entry> _entries;

    /// <summary>The entry for an object, or null when it isn't listed as unused.</summary>
    public static Entry Find(int room, string objectName)
    {
        if (string.IsNullOrEmpty(objectName)) return null;
        _entries ??= Load();
        return _entries.TryGetValue((room, objectName), out var e) ? e : null;
    }

    public static bool IsUnused(int room, string objectName)
    {
        return Find(room, objectName) != null;
    }

    private static Dictionary<(int, string), Entry> Load()
    {
        var entries = new Dictionary<(int, string), Entry>();
        void Add(ListFile file)
        {
            foreach (var e in file?.Objects ?? [])
                if (!string.IsNullOrWhiteSpace(e?.Object)) entries[(e.Room, e.Object.Trim())] = e;
        }
        try
        {
            using var stream = typeof(UnusedArt).Assembly.GetManifestResourceStream("unused-art.json");
            if (stream != null) Add(JsonSerializer.Deserialize<ListFile>(stream, Options));
        }
        catch (Exception ex) { Log.Warn($"Built-in unused-art.json ignored: {ex.Message}"); }
        try
        {
            if (File.Exists(LocalFile)) Add(JsonSerializer.Deserialize<ListFile>(File.ReadAllText(LocalFile), Options));
        }
        catch (Exception ex) { Log.Warn($"{LocalFile} ignored: {ex.Message}"); }
        return entries;
    }
}
