using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MMUCS.Core.Diagnostics;

namespace MMUCS.Core.Media;

/// <summary>
/// The order COMI's cutscenes play in, with what's known about each: Names/cutscenes.json (embedded),
/// replaced by a cutscenes.json in the app data folder when there is one. The game has no such list
/// (scripts start cutscenes by name), so it's curated from the scripts that start them.
/// </summary>
public static class CutsceneOrder
{
    public sealed class Entry
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public int Disc { get; set; }
        public string Room { get; set; }
        public string Note { get; set; }
    }

    public sealed class FileModel
    {
        public string Note { get; set; }
        public List<Entry> Cutscenes { get; set; }
    }

    /// <summary>The app data copy that replaces the built-in list.</summary>
    public static string LocalFile => Path.Combine(CorePaths.Root, "cutscenes.json");

    private static readonly Lazy<Dictionary<string, (int Order, Entry Entry)>> _entries = new(Load);

    /// <summary>A cutscene's place in the play order and its entry, or false for one the list doesn't have.</summary>
    public static bool TryGet(string id, out int order, out Entry entry)
    {
        if (id != null && _entries.Value.TryGetValue(id, out var e)) { order = e.Order; entry = e.Entry; return true; }
        order = int.MaxValue;
        entry = null;
        return false;
    }

    private static Dictionary<string, (int, Entry)> Load()
    {
        FileModel model = null;
        try
        {
            if (File.Exists(LocalFile)) model = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(LocalFile));
        }
        catch (Exception ex) { Log.Warn($"{LocalFile} ignored: {ex.Message}"); }
        if (model?.Cutscenes == null)
        {
            try
            {
                using var stream = typeof(CutsceneOrder).Assembly.GetManifestResourceStream("cutscenes.json");
                if (stream != null) model = JsonSerializer.Deserialize<FileModel>(stream);
            }
            catch (Exception ex) { Log.Warn($"Built-in cutscene order ignored: {ex.Message}"); }
        }

        var map = new Dictionary<string, (int, Entry)>(StringComparer.OrdinalIgnoreCase);
        if (model?.Cutscenes == null) return map;
        for (int i = 0; i < model.Cutscenes.Count; i++)
            if (model.Cutscenes[i]?.Id is { } id) map.TryAdd(id, (i, model.Cutscenes[i]));
        return map;
    }
}
