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
/// and their offsets from its meta.json; --place works out the offsets of the cels meta.json has none for that don't
/// line up (where the game's cel sits in the mod's) and writes them there, leaving the offsets it has (set by hand or
/// in the Inspector) alone; --place FOLDER,… does that in those folders, and --place CEL,… works out just those cels,
/// offset or not. --check decodes every audio file in the mods and checks it against the MD5 its FLAC stream carries.
/// --strip [MOD,…] [--out DIR] writes a copy of each palette mod to share (DIR, else _mods_shared beside the game), with
/// none of the game's own pictures or music in it: see <see cref="Strip"/>.
/// </summary>
internal static class ModsCommand
{
    public static int Run(ScummBlock root, List<string> a, string gameFile)
    {
        int stripAt = a.IndexOf("--strip");
        if (stripAt >= 0)
        {
            List<string> named = stripAt + 1 < a.Count && !a[stripAt + 1].StartsWith("--")
                ? [.. a[stripAt + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)] : null;
            int outAt = a.IndexOf("--out");
            string outDir = outAt >= 0 && outAt + 1 < a.Count ? a[outAt + 1]
                : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(gameFile)) ?? ".", "_mods_shared");
            return Strip(root, gameFile, named, outDir);
        }
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
        HiRes(root, gameFile);
        // --place, or --place NAME,NAME…: folders, or cels worked out again even when they have an offset.
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

    /// <summary>Audio a music mod plays: left out of a shared mod, as its players bring their own copies.</summary>
    private static readonly HashSet<string> AudioFiles = new(StringComparer.OrdinalIgnoreCase) { ".flac", ".wav", ".ogg", ".mp3", ".opus" };

    /// <summary>
    /// <c>--strip</c>: a copy of each enabled mod (or those <paramref name="named"/>) under <paramref name="outDir"/> to share,
    /// with only its author's work in it. Its object images and costume cels become overlays (<c>"base": "game"</c>): the
    /// pixels that draw as the game's under them are made see-through, so the player's own game fills them in, and the
    /// game's pixels it hides are marked to be erased. Each is drawn again from what's written and checked to draw as the
    /// mod did. Its music's audio
    /// is left out (its mod.json stays, and <c>mods --check</c> checks a player's files against their MD5s). A hi-res mod is
    /// the game's art all through, so none is written. Files there already are written over, and others left.
    /// </summary>
    private static int Strip(ScummBlock root, string gameFile, List<string> named, string outDir)
    {
        var images = ObjectImageMods.Load(gameFile);
        var cels = CostumeCelMods.Load(gameFile);
        var rooms = new Dictionary<string, RoomModel>(StringComparer.OrdinalIgnoreCase);
        var costumes = new Dictionary<string, ScummBlock>(StringComparer.OrdinalIgnoreCase);
        foreach (var lflf in root.FindChildrenRecursive(ScummTag.LFLF))
        {
            if (RoomModel.TryCreate(lflf, out var room)) rooms.TryAdd(ScummExporters.RoomFolderName(room), room);
            foreach (var akos in lflf.Children.Where(c => c.Tag == ScummTag.AKOS))
                if (CostumeCelMods.FolderOf(akos) is { } f) costumes.TryAdd(f.Replace('\\', '/'), akos);
        }
        var mods = GameMods.Load(gameFile).Where(m => m.Enabled && (named == null || named.Contains(m.Name, StringComparer.OrdinalIgnoreCase))).ToList();
        foreach (string name in named ?? [])
            if (!mods.Any(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) Console.WriteLine($"--strip {name}: no enabled mod is called that");
        int whole = 0, bad = 0;
        foreach (var mod in mods)
        {
            string dest = Path.Combine(outDir, mod.Name);
            if (mod.Scale > 1)
            {
                Console.WriteLine($"{mod.Name}: a hi-res mod, its pictures the game's art upscaled: nothing written (share how it was made instead)");
                continue;
            }
            Console.WriteLine($"{mod.Name} -> {dest}");
            int audio = 0;
            foreach (var file in Directory.EnumerateFiles(mod.Folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                string rel = Path.GetRelativePath(mod.Folder, file);
                if (AudioFiles.Contains(Path.GetExtension(file))) { audio++; continue; }
                string to = Path.Combine(dest, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(to));
                File.Copy(file, to, overwrite: true);
            }
            if (audio > 0) Console.WriteLine($"  left out {audio} audio file{(audio == 1 ? "" : "s")}: players add their own (mods --check checks them)");

            string under = Path.Combine(mod.Folder, "rooms");
            if (!Directory.Exists(under)) continue;
            foreach (var png in Directory.EnumerateFiles(under, "*.png", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                var parts = Path.GetRelativePath(under, png).Split(Path.DirectorySeparatorChar);
                if (parts is not [var roomName, "objects", var fileName] || !rooms.TryGetValue(roomName, out var room)) continue;
                var hit = room.Objects.SelectMany(o => Enumerable.Range(0, Math.Max(1, o.ImageCount)).Select(f => (o, f)))
                    .FirstOrDefault(t => ScummExporters.ObjectFrameFile(t.o, t.f) == fileName);
                string rel = Path.GetRelativePath(mod.Folder, png).Replace('\\', '/');
                if (hit.o == null || images.Find(room, hit.o, hit.f, mod.Name) is not { } image) { Console.WriteLine($"  {rel}: no object image of the game: copied as it is"); whole++; continue; }
                if (image.OverGame) { Console.WriteLine($"  {rel}: already over the game's"); continue; }
                int palette = MMUCS.Player.Rooms.RoomSceneBuilder.PaletteOf(room, hit.o);
                var game = ObimCache.GetCachedObimAsync(hit.o.Block, hit.f, palette, bakeImage: false, default).GetAwaiter().GetResult().Surface;
                var background = room.Width > 0
                    ? ScummBackgroundCache.GetCachedBackgroundAsync(MMUCS.Player.Rooms.RoomSceneBuilder.BackgroundCacheId(room), room.Rmhd, room.Room, false, 0, default).GetAwaiter().GetResult().Surface
                    : null;
                var stripped = images.Strip(image, game, hit.o.X, hit.o.Y, ObimCache.ExtractPaletteContext(room.Room, palette).rawBytes,
                    ObimCache.GetTransparentIndex(room.Room, hit.o.Block, hit.f), background);
                string to = Path.Combine(dest, Path.GetRelativePath(mod.Folder, png));
                if (Write(rel, to, stripped))
                {
                    ObjectImageMods.SaveOverGame(to);
                    // Widened to take in the game's image: its top-left moves with it.
                    var overlay = image with { File = to, OverGame = true, X = (image.X ?? hit.o.X) + stripped.ShiftX, Y = (image.Y ?? hit.o.Y) + stripped.ShiftY };
                    ObjectImageMods.SavePlace(overlay, overlay.X.Value, overlay.Y.Value);
                    var colours = ObimCache.ExtractPaletteContext(room.Room, palette).rawBytes;
                    int see = ObimCache.GetTransparentIndex(room.Room, hit.o.Block, hit.f);
                    var before = images.Drawn(image, game, hit.o.X, hit.o.Y, colours, see, background);
                    var after = images.Drawn(overlay, game, hit.o.X, hit.o.Y, colours, see, background);
                    if (!Same(before, after, (byte)see)) bad++;
                }
                else whole++;
            }
            foreach (var dir in Directory.EnumerateDirectories(under, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                string rel = Path.GetRelativePath(mod.Folder, dir).Replace('\\', '/');
                if (rel.Split('/') is not [_, _, "costumes", _] || !costumes.TryGetValue(rel, out var akos)) continue;
                var d = AkosParser.Parse(akos);
                var placed = new Dictionary<string, (int X, int Y)>();
                foreach (var (number, cel) in cels.Find(akos).Where(kv => kv.Value.Mod == mod.Name).OrderBy(kv => kv.Key))
                {
                    string celRel = $"{rel}/{Path.GetFileName(cel.File)}";
                    if (cel.OverGame) { Console.WriteLine($"  {celRel}: already over the game's"); continue; }
                    var stripped = CostumeCelMods.Strip(d, number, cel.File, cel.X ?? 0, cel.Y ?? 0);
                    string to = Path.Combine(dest, rel, Path.GetFileName(cel.File));
                    if (Write(celRel, to, stripped))
                    {
                        placed[Path.GetFileName(cel.File)] = ((cel.X ?? 0) + stripped.ShiftX, (cel.Y ?? 0) + stripped.ShiftY);
                        var before = CostumeCelMods.Read(d, cel);
                        var after = CostumeCelMods.Read(d, cel with { File = to, OverGame = true, X = placed[Path.GetFileName(cel.File)].X, Y = placed[Path.GetFileName(cel.File)].Y });
                        if (!Same((before.Surface, before.OffsetX, before.OffsetY), (after.Surface, after.OffsetX, after.OffsetY), d.TransparentIndex)) bad++;
                    }
                    else whole++;
                }
                if (placed.Count > 0) CostumeCelMods.SaveOffsets(Path.Combine(dest, rel), placed, overGame: true);
            }
        }
        if (whole > 0) Console.WriteLine($"{whole} picture{(whole == 1 ? "" : "s")} copied as they are, maybe with the game's pixels in them");
        Console.WriteLine(bad == 0 ? "every overlay draws over the game's as the mod drew it" : $"{bad} overlays don't draw as the mod did");
        return bad == 0 ? 0 : 1;
    }

    /// <summary>Whether a mod picture drawn two ways comes out the same; says how it differs when it doesn't.</summary>
    private static bool Same((IndexedSurface Surface, int X, int Y)? before, (IndexedSurface Surface, int X, int Y)? after, byte transparent)
    {
        if (before is not var (a, ax, ay) || after is not var (b, bx, by)) { Console.WriteLine("    DIFFERS: won't draw"); return false; }
        int n = ModOverlay.Differences(a, ax, ay, b, bx, by, transparent);
        if (n > 0) Console.WriteLine($"    DIFFERS: {n} pixels draw otherwise over the game's");
        return n == 0;
    }

    /// <summary>Writes a stripped mod picture to <paramref name="to"/> and says what's in it, or why it's left whole (false).</summary>
    private static bool Write(string rel, string to, ModOverlay.Stripped stripped)
    {
        if (stripped == null) { Console.WriteLine($"  {rel}: won't read: copied as it is"); return false; }
        using (var f = File.Create(to)) MMUCS.Core.Imaging.PngWriter.Write(f, stripped.Width, stripped.Height, ModOverlay.ToRgba(stripped.Argb));
        int all = stripped.Kept + stripped.Cleared;
        string erases = stripped.Erased > 0 ? $", erases {stripped.Erased} of the game's" : "";
        string widened = stripped.ShiftX != 0 || stripped.ShiftY != 0 ? $", widened by {-stripped.ShiftX},{-stripped.ShiftY} to reach the game's" : "";
        // Little of it the game's where it's placed: a repaint, or the game's art moved, which this can't tell.
        string repaint = all > 0 && stripped.Cleared < all / 10 ? " (a repaint, or the game's art moved: check it's yours)" : "";
        Console.WriteLine($"  {rel}: keeps {stripped.Kept} of its {all} pixels ({(all == 0 ? 0 : stripped.Kept / (double)all):P0}){erases}{widened}{repaint}");
        return true;
    }

    /// <summary>The hi-res mods: per room the background and object images they draw, then PNGs that match no room or object.</summary>
    private static void HiRes(ScummBlock root, string gameFile)
    {
        var mods = HiResMods.Load(gameFile);
        Console.WriteLine($"hi-res: {(mods.Mods.Count == 0 ? "no enabled mod has a scale and rooms/" : string.Join(", ", mods.Mods))}");
        if (mods.Mods.Count == 0) return;
        var used = new HashSet<string>(StringComparer.Ordinal);
        int backgrounds = 0, objects = 0;
        foreach (var lflf in root.FindChildrenRecursive(ScummTag.LFLF))
        {
            if (!RoomModel.TryCreate(lflf, out var room)) continue;
            var parts = new List<string>();
            if (mods.Background(room) is { } bg && used.Add(bg.File))
            {
                parts.Add($"background ×{bg.Scale}");
                backgrounds++;
            }
            foreach (var obj in room.Objects)
                for (int f = 0; f < Math.Max(1, obj.ImageCount); f++)
                    if (mods.Object(room, obj, f) is { } o && used.Add(o.File))
                    {
                        parts.Add(ScummExporters.ObjectFrameFile(obj, f) + (o.X is int x ? $" at {x},{o.Y}" : ""));
                        objects++;
                    }
            if (parts.Count > 0) Console.WriteLine($"  {ScummExporters.RoomFolderName(room),-20} {string.Join(", ", parts)}");
        }
        Console.WriteLine($"  {backgrounds} backgrounds, {objects} object images");
        foreach (var mod in GameMods.Load(gameFile).Where(m => mods.Mods.Contains(m.Name)))
            foreach (var png in Directory.EnumerateFiles(Path.Combine(mod.Folder, "rooms"), "*.png", SearchOption.AllDirectories).Order())
                if (!used.Contains(png) && !png.Contains(Path.DirectorySeparatorChar + "costumes" + Path.DirectorySeparatorChar))
                    Console.WriteLine($"  not drawn: {mod.Name}/{Path.GetRelativePath(mod.Folder, png)} (no such room or object, or another mod's comes first)");
        foreach (var p in mods.Problems) Console.WriteLine($"  problem: {p}");
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
    /// <paramref name="place"/>, the cels meta.json gives no offset that don't fit are worked out and saved, and the
    /// offsets it has are kept; with <paramref name="only"/>, just in the folders it names, and the cels it names are
    /// worked out whatever their offset.
    /// </summary>
    private static void CostumeCels(ScummBlock root, string gameFile, bool place, List<string> only = null)
    {
        var cels = CostumeCelMods.Load(gameFile);
        if (cels.Mods.Count == 0) return;
        var matched = new HashSet<string>();
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
                bool inFolder = only == null || Within(only, $"{mod.Name}/{rel}", dir, matched);
                var d = AkosParser.Parse(akos);
                cels.Apply(d, akos);
                var found = cels.Find(akos).Where(kv => kv.Value.Mod == mod.Name).OrderBy(kv => kv.Key).ToList();
                Console.WriteLine($"  {mod.Name}/{rel}: {found.Count} cels");
                var placed = new Dictionary<string, (int X, int Y)>();
                foreach (var (number, cel) in found)
                {
                    string game = number < d.CelInfos.Length ? $"{d.CelInfos[number].Width}x{d.CelInfos[number].Height}" : "no such cel";
                    var m = d.ModCels?.GetValueOrDefault(number);
                    if (cel.OverGame)
                    {
                        // Drawn over the game's cel: its offset is where its author's pixels go, not where the game's sit in it.
                        Console.WriteLine($"    {Path.GetFileName(cel.File)}: {(m?.Overlay is { } o ? $"{o.Png.Width}x{o.Png.Height}" : "won't read")} over the game's {game} at offset {cel.X ?? 0},{cel.Y ?? 0}");
                        continue;
                    }
                    string size = m != null ? $"{m.Surface.Width}x{m.Surface.Height}" : "won't read";
                    bool given = cel.X != null || cel.Y != null;
                    string offset = given ? $"offset {cel.X ?? 0},{cel.Y ?? 0}" : "no offset";
                    var gameCel = m != null && number < d.CelOffsets.Length ? AkosDecoders.DecodeCel(d, number, default) : null;
                    // How well the game's cel fits where the offset puts it: under 90% it's the wrong offset (or a repaint).
                    double share = gameCel != null ? CostumeCelMods.Match(gameCel, m.Surface, d.TransparentIndex, cel.X ?? 0, cel.Y ?? 0) : 0;
                    if (gameCel != null) offset += $" ({share:P0} of the game's pixels match there)";
                    bool named = only != null && Named(only, $"{mod.Name}/{rel}/{Path.GetFileNameWithoutExtension(cel.File)}", number, matched);
                    if (place && gameCel != null && !named && inFolder && given && share < 0.9)
                    {
                        // Its offset was set (by hand, the Inspector or an earlier --place): kept unless it's named.
                        offset += CostumeCelMods.Place(gameCel, m.Surface, d.TransparentIndex) is { } fit
                            ? $"; kept, as meta.json gives it (the best fit, {fit.X},{fit.Y}, matches {fit.Share:P0}; name the cel to take it)"
                            : "; kept, as meta.json gives it";
                    }
                    else if (place && gameCel != null && (named || (inFolder && !given && share < 0.9)))
                    {
                        var at = CostumeCelMods.Place(gameCel, m.Surface, d.TransparentIndex);
                        if (at is { Share: >= 0.9 } good)
                        {
                            if (!given || (good.X, good.Y) != (cel.X ?? 0, cel.Y ?? 0)) placed[Path.GetFileNameWithoutExtension(cel.File)] = (good.X, good.Y);
                            offset = $"{Moved(given, cel.X ?? 0, cel.Y ?? 0, good.X, good.Y)} ({good.Share:P0} of the game's pixels match)";
                        }
                        else if (at is { } near && named && near.Share >= 0.5)
                        {
                            // Named: its best fit is taken unless it's hardly the game's cel at all.
                            if (!given || (near.X, near.Y) != (cel.X ?? 0, cel.Y ?? 0)) placed[Path.GetFileNameWithoutExtension(cel.File)] = (near.X, near.Y);
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
        if (only != null)
            foreach (string name in only.Where(n => !matched.Contains(n)))
                Console.WriteLine($"  --place {name}: no costume folder or cel of an enabled mod is called that");
    }

    /// <summary>
    /// Whether a costume folder is in one of <paramref name="names"/>: a folder on disk it's in, or a run of the
    /// folders on its path from the mods folder ("WideScreenMod", "022_clring-a", "akos_02A4A050",
    /// "022_clring-a/costumes/akos_02A4A050"). The names that do go in <paramref name="matched"/>.
    /// </summary>
    private static bool Within(List<string> names, string path, string dir, HashSet<string> matched)
    {
        string full = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        bool any = false;
        foreach (string raw in names)
        {
            string name = raw.Replace('\\', '/').Trim('/');
            bool on = name.Length > 0 && (("/" + path + "/").Contains("/" + name + "/", StringComparison.OrdinalIgnoreCase) ||
                      Directory.Exists(raw) && full.StartsWith(Path.GetFullPath(raw).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal));
            if (on) { matched.Add(raw); any = true; }
        }
        return any;
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
    private static bool Named(List<string> names, string path, int number, HashSet<string> matched)
    {
        bool any = false;
        foreach (string raw in names)
        {
            string name = raw.Replace('\\', '/');
            if (name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
            if (int.TryParse(name, out int n) && n == number ||
                path.Equals(name, StringComparison.OrdinalIgnoreCase) || path.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase))
            { matched.Add(raw); any = true; }
        }
        return any;
    }
}
