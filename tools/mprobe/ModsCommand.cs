using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MMUCS.Core;
using MMUCS.Core.Audio;
using MMUCS.Core.Scumm;

/// <summary>
/// <c>mprobe mods [--check]</c>: the mods beside the game (_mods/&lt;mod&gt;/mod.json). Music: the game tracks each
/// mod replaces and with what, the problems reading its map, and the game's tracks no mod replaces. Object
/// images: each PNG under a mod's rooms/ with the object image it replaces and where it goes, or why it
/// replaces none. --check decodes every audio file in the mods and checks it against the MD5 its FLAC stream carries.
/// </summary>
internal static class ModsCommand
{
    public static int Run(ScummBlock root, List<string> a, string gameFile)
    {
        string folder = GameMods.Folder(gameFile);
        var all = GameMods.Load(gameFile);
        Console.WriteLine($"mods in {folder}: {(all.Count == 0 ? "none with a mod.json" : string.Join(", ", all.Select(m => m.Enabled ? m.Name : m.Name + " (off)")))}");
        var mods = MusicMods.Load(gameFile);
        Console.WriteLine($"music: {(mods.Mods.Count == 0 ? "no mod has tracks" : string.Join(", ", mods.Mods))}");
        foreach (var p in mods.Problems) Console.WriteLine($"  problem: {p}");

        var table = MusicTable.Load(gameFile);
        var names = table == null ? new Dictionary<int, string>() :
            table.States.Concat(table.Sequences).Where(c => c.FileNumber != 0).GroupBy(c => c.FileNumber).ToDictionary(g => g.Key, g => g.First().File);
        foreach (var (number, t) in mods.Tracks.OrderBy(kv => kv.Key))
        {
            string span = $"{t.Start:0.##}–{(t.End is { } e ? $"{e:0.##}" : "end")}";
            string range = $"{(t.LoopStart ?? t.Start):0.##}–{(t.LoopEnd is { } le ? $"{le:0.##}" : "end")}";
            string loop = t.Loop switch { true => $", loops {range}", false => ", once", null => $", loops {range} if the game's does" };
            Console.WriteLine($"  {(names.TryGetValue(number, out var n) ? n : $"{number} (no such game track)"),-22} {t.Mod}/{Path.GetFileName(t.File)} {span}{loop}");
        }
        var unmapped = names.Keys.Where(k => !mods.Tracks.ContainsKey(k)).Order().Select(k => names[k]).ToList();
        if (table != null) Console.WriteLine($"game tracks no mod replaces ({unmapped.Count} of {names.Count}): {string.Join(", ", unmapped)}");
        ObjectImages(root, gameFile);

        if (!a.Contains("--check") || folder == null || !Directory.Exists(folder)) return 0;
        int bad = 0;
        foreach (var file in Directory.EnumerateFiles(folder, "*.flac", SearchOption.AllDirectories).Order())
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string verdict;
            try
            {
                var flac = Flac.Open(file);
                bool ok = flac.Verify();
                if (!ok) bad++;
                verdict = $"{(ok ? "ok" : "MD5 MISMATCH")}  {flac.SampleRate} Hz {flac.BitsPerSample}-bit {flac.Channels} ch {flac.Duration:m\\:ss\\.f}";
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                bad++;
                verdict = $"BROKEN: {ex.Message}";
            }
            Console.WriteLine($"  {Path.GetRelativePath(folder, file)}: {verdict} ({sw.Elapsed.TotalSeconds:0.0} s)");
        }
        Console.WriteLine(bad == 0 ? "every file decodes to its MD5" : $"{bad} files failed");
        return bad == 0 ? 0 : 1;
    }

    /// <summary>Each PNG under an enabled mod's rooms/: the object image it stands in for, or why it doesn't.</summary>
    private static void ObjectImages(ScummBlock root, string gameFile)
    {
        var images = ObjectImageMods.Load(gameFile);
        Console.WriteLine($"object images: {(images.Mods.Count == 0 ? "no enabled mod has rooms/" : string.Join(", ", images.Mods))}");
        if (images.Mods.Count == 0) return;
        var rooms = new Dictionary<string, RoomModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var lflf in root.FindChildrenRecursive(ScummTag.LFLF))
            if (RoomModel.TryCreate(lflf, out var room)) rooms.TryAdd(ScummExporters.RoomFolderName(room), room);
        foreach (var mod in GameMods.Load(gameFile).Where(m => images.Mods.Contains(m.Name)))
        {
            string under = Path.Combine(mod.Folder, "rooms");
            foreach (var png in Directory.EnumerateFiles(under, "*.png", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                string rel = Path.GetRelativePath(mod.Folder, png).Replace('\\', '/');
                var parts = Path.GetRelativePath(under, png).Split(Path.DirectorySeparatorChar);
                string verdict;
                if (parts.Length != 3 || parts[1] != "objects") verdict = "not at rooms/<room>/objects/<file>.png";
                else if (!rooms.TryGetValue(parts[0], out var room)) verdict = $"no room folder {parts[0]} in the export's names";
                else
                {
                    var hit = room.Objects.SelectMany(o => Enumerable.Range(0, Math.Max(1, o.ImageCount)).Select(f => (o, f)))
                        .FirstOrDefault(t => ScummExporters.ObjectFrameFile(t.o, t.f) == parts[2]);
                    if (hit.o == null) verdict = $"no object image of room {room.RoomNo} is named {parts[2]}";
                    else if (images.Find(room, hit.o, hit.f) is not { } found) verdict = "not found by the lookup";
                    else
                    {
                        string size = "";
                        try { var p = MMUCS.Core.Imaging.PngReader.Load(png); size = $" {p.Width}x{p.Height}"; }
                        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException) { size = $" (won't read: {ex.Message})"; }
                        string wins = found.File == png ? "" : $", but {found.Mod}'s is shown";
                        verdict = $"object {hit.o.Name} image {hit.f} ({hit.o.Width}x{hit.o.Height} at {hit.o.X},{hit.o.Y}): " +
                                  $"shown{size} at {found.X ?? hit.o.X},{found.Y ?? hit.o.Y}{(found.X == null && found.Y == null ? " (its place)" : "")}{wins}";
                    }
                }
                Console.WriteLine($"  {mod.Name}/{rel}: {verdict}");
            }
        }
        foreach (var p in images.Problems) Console.WriteLine($"  problem: {p}");
    }
}
