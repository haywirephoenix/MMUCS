using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MMUCS.Core.Audio;

/// <summary>
/// <c>mprobe mods [--check]</c>: the music mods beside the game (_mods/music/&lt;mod&gt;/mod.json): each mod, the
/// game tracks it replaces and with what, the problems reading its map, and the game's tracks no mod replaces.
/// --check decodes every audio file in the mods and checks it against the MD5 its FLAC stream carries.
/// </summary>
internal static class ModsCommand
{
    public static int Run(List<string> a, string gameFile)
    {
        string folder = MusicMods.Folder(gameFile);
        var mods = MusicMods.Load(gameFile);
        Console.WriteLine($"music mods in {folder}: {(mods.Mods.Count == 0 ? "none with a mod.json" : string.Join(", ", mods.Mods))}");
        foreach (var p in mods.Problems) Console.WriteLine($"  problem: {p}");

        var table = MusicTable.Load(gameFile);
        var names = table == null ? new Dictionary<int, string>() :
            table.States.Concat(table.Sequences).Where(c => c.FileNumber != 0).GroupBy(c => c.FileNumber).ToDictionary(g => g.Key, g => g.First().File);
        foreach (var (number, t) in mods.Tracks.OrderBy(kv => kv.Key))
        {
            string span = $"{t.Start:0.##}–{(t.End is { } e ? $"{e:0.##}" : "end")}";
            string loop = t.Loop ? $", loops {(t.LoopStart ?? t.Start):0.##}–{(t.LoopEnd is { } le ? $"{le:0.##}" : "end")}" : ", once";
            Console.WriteLine($"  {(names.TryGetValue(number, out var n) ? n : $"{number} (no such game track)"),-22} {t.Mod}/{Path.GetFileName(t.File)} {span}{loop}");
        }
        var unmapped = names.Keys.Where(k => !mods.Tracks.ContainsKey(k)).Order().Select(k => names[k]).ToList();
        if (table != null) Console.WriteLine($"game tracks no mod replaces ({unmapped.Count} of {names.Count}): {string.Join(", ", unmapped)}");

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
}
