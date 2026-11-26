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
/// replaces none. Costume cels: each costume folder under a mod's rooms/ with its cels, their sizes against the game's
/// and their offsets from its meta.json; --place works out the offsets that don't line up (where the game's cel
/// sits in the mod's) and writes them there, and --place CEL,… just those cels, lined up or not. --check decodes every audio file in the mods and checks it against the
/// MD5 its FLAC stream carries.
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
        // --place, or --place NAME,NAME…: only those cels, worked out again even when their offset fits.
        int placeAt = a.IndexOf("--place");
        List<string> only = placeAt >= 0 && placeAt + 1 < a.Count && !a[placeAt + 1].StartsWith("--")
            ? [.. a[placeAt + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)] : null;
        CostumeCels(root, gameFile, placeAt >= 0, only);

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
                // Costume cels are listed with their costumes.
                if (Path.GetRelativePath(under, png).Split(Path.DirectorySeparatorChar) is [_, "costumes", ..]) continue;
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

    /// <summary>
    /// Each costume folder under an enabled mod's rooms/: its costume, and each cel with its size and offset. With
    /// <paramref name="place"/>, the offsets that don't fit are worked out and saved; with <paramref name="only"/>, just
    /// those cels are, fitting or not.
    /// </summary>
    private static void CostumeCels(ScummBlock root, string gameFile, bool place, List<string> only = null)
    {
        var cels = CostumeCelMods.Load(gameFile);
        if (cels.Mods.Count == 0) return;
        var costumes = new Dictionary<string, ScummBlock>(StringComparer.OrdinalIgnoreCase);
        foreach (var lflf in root.FindChildrenRecursive(ScummTag.LFLF))
            foreach (var akos in lflf.Children.Where(c => c.Tag == ScummTag.AKOS))
                if (CostumeCelMods.FolderOf(akos) is { } f) costumes.TryAdd(f.Replace('\\', '/'), akos);
        Console.WriteLine("costume cels:");
        foreach (var mod in GameMods.Load(gameFile).Where(m => cels.Mods.Contains(m.Name)))
        {
            string rooms = Path.Combine(mod.Folder, "rooms");
            foreach (var dir in Directory.EnumerateDirectories(rooms, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                string rel = Path.GetRelativePath(mod.Folder, dir).Replace('\\', '/');
                var parts = rel.Split('/');
                if (parts.Length != 4 || parts[2] != "costumes") continue;
                if (!costumes.TryGetValue(rel, out var akos))
                {
                    Console.WriteLine($"  {mod.Name}/{rel}: no costume of the game is exported there");
                    continue;
                }
                var d = AkosParser.Parse(akos);
                cels.Apply(d, akos);
                var found = cels.Find(akos).Where(kv => kv.Value.Mod == mod.Name).OrderBy(kv => kv.Key).ToList();
                Console.WriteLine($"  {mod.Name}/{rel}: {found.Count} cels");
                var placed = new Dictionary<string, (int X, int Y)>();
                foreach (var (number, cel) in found)
                {
                    string game = number < d.CelInfos.Length ? $"{d.CelInfos[number].Width}x{d.CelInfos[number].Height}" : "no such cel";
                    var m = d.ModCels?.GetValueOrDefault(number);
                    string size = m != null ? $"{m.Surface.Width}x{m.Surface.Height}" : "won't read";
                    bool given = cel.X != null || cel.Y != null;
                    string offset = given ? $"offset {cel.X ?? 0},{cel.Y ?? 0}" : "no offset";
                    var gameCel = m != null && number < d.CelOffsets.Length ? AkosDecoders.DecodeCel(d, number, default) : null;
                    // How well the game's cel fits where the offset puts it: under 90% it's the wrong offset (or a repaint).
                    double share = gameCel != null ? CostumeCelMods.Match(gameCel, m.Surface, d.TransparentIndex, cel.X ?? 0, cel.Y ?? 0) : 0;
                    if (gameCel != null) offset += $" ({share:P0} of the game's pixels match there)";
                    bool named = only != null && Named(only, $"{mod.Name}/{rel}/{Path.GetFileNameWithoutExtension(cel.File)}", number);
                    if (place && gameCel != null && (only == null ? share < 0.9 : named))
                    {
                        var at = CostumeCelMods.Place(gameCel, m.Surface, d.TransparentIndex);
                        if (at is { Share: >= 0.9 } good)
                        {
                            placed[Path.GetFileNameWithoutExtension(cel.File)] = (good.X, good.Y);
                            offset = $"{Moved(given, cel.X ?? 0, cel.Y ?? 0, good.X, good.Y)} ({good.Share:P0} of the game's pixels match)";
                        }
                        else if (at is { } near && named && near.Share >= 0.5)
                        {
                            // Named: its best fit is taken unless it's hardly the game's cel at all.
                            placed[Path.GetFileNameWithoutExtension(cel.File)] = (near.X, near.Y);
                            offset = $"{Moved(given, cel.X ?? 0, cel.Y ?? 0, near.X, near.Y)}, its best fit ({near.Share:P0} of the game's pixels match: a repaint?)";
                        }
                        else if (at is { } near2) offset += $"; the best fit, {near2.X},{near2.Y}, matches {near2.Share:P0}, so it's left";
                        else offset += "; the game's cel is bigger, so it's left";
                    }
                    Console.WriteLine($"    {Path.GetFileName(cel.File)}: {size} (game's {game}), {offset}");
                }
                if (placed.Count > 0) WriteOffsets(Path.Combine(dir, "meta.json"), placed);
            }
        }
        foreach (var p in cels.Problems) Console.WriteLine($"  problem: {p}");
    }

    /// <summary>Adds <paramref name="placed"/> to a costume folder's meta.json "cels", keeping what it has.</summary>
    private static void WriteOffsets(string meta, Dictionary<string, (int X, int Y)> placed)
    {
        CostumeCelMods.SaveOffsets(Path.GetDirectoryName(meta), placed);
        Console.WriteLine($"    wrote {placed.Count} offset{(placed.Count == 1 ? "" : "s")} to {meta}");
    }

    /// <summary>What placing did to a cel's offset: placed (none before), moved, or kept where it was.</summary>
    private static string Moved(bool given, int fromX, int fromY, int x, int y) =>
        !given ? $"placed at {x},{y}" : (fromX, fromY) == (x, y) ? $"stays at {x},{y}" : $"moved from {fromX},{fromY} to {x},{y}";

    /// <summary>
    /// Whether a cel is among <paramref name="names"/>: by its number ("44"), its file ("cel_044", "cel_044.png"), or the
    /// end of its path from the mods folder ("akos_02A4A050/cel_044", "WideScreenMod/rooms/022_clring-a/costumes/akos_02A4A050/cel_044").
    /// </summary>
    private static bool Named(List<string> names, string path, int number)
    {
        foreach (string raw in names)
        {
            string name = raw.Replace('\\', '/');
            if (name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
            if (int.TryParse(name, out int n) && n == number) return true;
            if (path.Equals(name, StringComparison.OrdinalIgnoreCase) || path.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
