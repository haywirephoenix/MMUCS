using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;
using MMUCS.Core.Audio;

/// <summary>
/// <c>mprobe match TRACK [FILE] [--in DIR] [--chunks]</c> and <c>mprobe match all --out FILE [--in DIR]</c>: where a
/// game music track's audio is in a FLAC, for a music mod's "start", "end", "loopStart" and "loopEnd". The track is
/// cut into 4-second chunks and each is found in the FLAC on its own by normalised cross-correlation: coarsely over
/// the whole file (FFT, at an eighth of the game's rate), then to the sample at the game's rate, so a FLAC that
/// holds the track rearranged, or its loop played twice, still lines up chunk by chunk (--chunks lists each). It's
/// done both plainly and whitened (differenced, so another mix or master of the same recording still lines up),
/// and the one that scores higher is kept. Runs of chunks at one offset are printed as spans, the game's jumps are
/// carried into the file's time, each matched by the 4 s after it (before it, for an end), and the mod.json entry
/// they give is printed to paste.
/// Without FILE, every FLAC under the mods folder (or --in DIR) is tried with a few chunks of the track
/// (at a sixteenth of the game's rate) and ranked, and the best is matched in full.
/// "all" does that for every music track in the bundles and writes the mod.json to FILE, each entry with its
/// "score" (how much of the track was found, weighted by how well), "mode" and "notes"; a track not found gets a
/// blank "file", with its best guess and that score. FILE's tracks that aren't the game's (a cutscene's) and its
/// other fields are kept, and so are its entries for tracks --only leaves out; FLACs are looked for beside FILE unless
/// --in says where.
/// </summary>
internal static class MatchCommand
{
    private const int Decimation = 8;        // the coarse match's rate: the game's ÷ 8 (2756 Hz for 22050)
    private const int SearchDecimation = 16; // the search's: ÷ 16
    private const double ChunkSeconds = 4, MinChunkSeconds = 1;
    private const double MatchScore = 0.6;   // a chunk correlating this well is found
    private const double MinScore = 0.3;     // one this well is found when nowhere else does half as well
    private const double FoundScore = 0.2;   // a track whose found share (score) is under this isn't taken as found
    private const int SearchChunks = 6;

    private sealed record Candidate(double Lag, double Score);
    private enum ChunkKind { Silent, Unmatched, Matched }
    private sealed record Chunk(long Start, long Length, ChunkKind Kind, double Delta, double Score, double Second = 0);

    /// <summary>A FLAC as the search keeps it: at the search's rate, plain and whitened.</summary>
    private sealed record Entry(string File, float[] Plain, float[] White);

    /// <summary>A search's ranking: a file's median probe score in each mode.</summary>
    private sealed record Ranked(string File, double Plain, double White, double[] Each)
    {
        public double Score => Math.Max(Plain, White);
    }

    /// <summary>A track matched in full in a file: the mode that scored higher, its chunks and the mod.json entry.</summary>
    private sealed record Result(string File, bool Whitened, double Score, double OtherScore, Signals Signals, List<string> Fields, List<string> Notes);

    public static int Run(List<string> a, Func<List<string>, string, string> takeOption,
        Func<string, ImusSound> findSound, Func<IEnumerable<int>> musicNumbers, string gameFile)
    {
        string searchDir = takeOption(a, "--in"), outFile = takeOption(a, "--out"), only = takeOption(a, "--only");
        var positional = a.Where(x => !x.StartsWith("--")).ToList();
        if (positional.Count < 2) throw new UsageException("match needs TRACK (a number, 1100, or a name, Hold1), or all");

        var table = MusicTable.Load(gameFile);
        var names = table == null ? new Dictionary<int, string>() :
            table.States.Concat(table.Sequences).Where(c => c.FileNumber != 0).GroupBy(c => c.FileNumber).ToDictionary(g => g.Key, g => g.First().File);
        if (positional[1] == "all")
        {
            if (outFile == null) throw new UsageException("match all needs --out FILE (the mod.json to write)");
            var picked = only?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(t => TrackNumber(t, names)).ToHashSet();
            return All(Path.GetFullPath(outFile), searchDir ?? Path.GetDirectoryName(Path.GetFullPath(outFile)), names, findSound,
                picked == null ? musicNumbers : () => musicNumbers().Where(picked.Contains));
        }

        int number = TrackNumber(positional[1], names);
        string name = names.TryGetValue(number, out var n) ? n : number.ToString(CultureInfo.InvariantCulture);
        var sound = findSound($"music/{number}") ?? throw new UsageException($"no music/{number} in the bundles");
        if (sound.Bits != 16) throw new UsageException($"music/{number} is {sound.Bits}-bit; only 16-bit tracks are matched");

        float[] game = Mono(sound.Samples, sound.Channels);
        int rate = sound.SampleRate;
        Console.WriteLine($"music/{number} {name}: {rate} Hz {sound.Channels} ch {Clock(game.Length / (double)rate)}, " +
            $"{sound.Regions.Count} regions, {sound.Jumps.Count} jumps");

        string folder = MusicMods.Folder(gameFile);
        string file;
        if (positional.Count >= 3) file = FindFile(positional[2], searchDir ?? folder);
        else
        {
            string dir = searchDir ?? folder;
            if (dir == null || !Directory.Exists(dir)) throw new UsageException($"no folder to search ({dir ?? "no mods folder"}); give FILE or --in DIR");
            var library = Library(dir, rate);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var ranked = Search(game, rate, library);
            var mapped = MappedFiles(dir, number);
            Console.WriteLine("best files (median of the chunks' scores, the higher of plain and whitened):");
            foreach (var r in ranked.Take(5))
                Console.WriteLine($"  {r.Score,5:0.00} {(r.White > r.Plain ? "whitened" : "plain   ")}  {Path.GetRelativePath(dir, r.File)}" +
                    $"  [{string.Join(" ", r.Each.Select(e => e.ToString("0.00", CultureInfo.InvariantCulture)))}]" +
                    (mapped.Contains(Path.GetFullPath(r.File)) ? "  ← mod.json maps it here" : ""));
            Console.WriteLine($"  ({sw.Elapsed.TotalSeconds:0.0} s)");
            if (ranked.Count == 0 || ranked[0].Score < MinScore)
            {
                Console.WriteLine($"nothing scores {MinScore:0.0} or more: the track isn't in these files, or not as the same recording");
                return 1;
            }
            file = ranked[0].File;
        }

        var flac = Flac.Open(file);
        Console.WriteLine($"{Path.GetFileName(file)}: {flac.SampleRate} Hz {flac.BitsPerSample}-bit {flac.Channels} ch {flac.Duration:m\\:ss\\.fff}");
        var result = MatchFile(sound, game, file, flac);
        var sig = result.Signals;
        Console.WriteLine($"matched {(result.Whitened ? "whitened" : "plain")}: score {result.Score:0.000} (how much of the track is found, by how well; " +
            $"{(result.Whitened ? "plain" : "whitened")} {result.OtherScore:0.000})");
        if (a.Contains("--chunks"))
            foreach (var c in sig.Chunks)
                Console.WriteLine($"  chunk {Clock(c.Start / (double)rate)}  {c.Kind,-9}  file {(c.Kind == ChunkKind.Matched ? Clock((c.Start + c.Delta) / rate) : "?"),-9}  score {c.Score:0.000}  next best {c.Second:0.000}");
        PrintSpans(sig.Chunks, rate);
        PrintJumps(sound, sig);

        string key = ModJsonOver(file) is { } map ? ModKeys(map, number).Select(k => k.Key).FirstOrDefault() ?? name : name;
        Console.WriteLine("for mod.json:");
        Console.WriteLine($"  {Json(key)}: {{ {string.Join(", ", result.Fields)} }},");
        foreach (var note in result.Notes) Console.WriteLine($"  note: {note}");
        return sig.Chunks.Any(c => c.Kind == ChunkKind.Matched) ? 0 : 1;
    }

    /// <summary>The track matched in <paramref name="file"/> plainly and whitened, the one scoring higher kept.</summary>
    private static Result MatchFile(ImusSound sound, float[] game, string file, Flac flac)
    {
        int rate = sound.SampleRate;
        float[] audio = Resample(Mono(Decode(flac), flac.Channels), flac.SampleRate, rate);
        var chunks = ChunkStarts(game.Length, rate).ToList();
        Signals Matched(bool white)
        {
            var sig = new Signals(white ? Whiten(game) : game, white ? Whiten(audio) : audio, rate);
            sig.Chunks = MatchChunks(sig, chunks);
            return sig;
        }
        var plain = Matched(false);
        var white = Matched(true);
        double ps = Coverage(plain), ws = Coverage(white);
        var best = ws > ps ? white : plain;
        var (fields, notes) = Suggest(sound, best, file);
        return new Result(file, ws > ps, Math.Max(ps, ws), Math.Min(ps, ws), best, fields, notes);
    }

    /// <summary>How much of the track is found, by how well: the found chunks' scores weighted by length, over the length that isn't silent.</summary>
    private static double Coverage(Signals sig)
    {
        double loud = sig.Chunks.Where(c => c.Kind != ChunkKind.Silent).Sum(c => (double)c.Length);
        return loud == 0 ? 0 : sig.Chunks.Where(c => c.Kind == ChunkKind.Matched).Sum(c => c.Score * c.Length) / loud;
    }

    // ---- every track ----

    private static int All(string outFile, string dir, Dictionary<int, string> names, Func<string, ImusSound> findSound, Func<IEnumerable<int>> musicNumbers)
    {
        if (!Directory.Exists(dir)) throw new UsageException($"no folder {dir} to look for FLACs in");
        var numbers = musicNumbers().Distinct().Order().ToList();
        if (numbers.Count == 0) throw new UsageException("no music in the bundles");

        // What FILE has: its keys in order with their values (raw), and its other fields.
        var existing = new List<(string Key, int Number, string Raw, string File)>();
        var top = new List<(string Name, string Raw)>();
        List<string> comment = null;
        if (File.Exists(outFile))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(outFile), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Name == "tracks" && p.Value.ValueKind == JsonValueKind.Object)
                    foreach (var t in p.Value.EnumerateObject())
                    {
                        int digits = 0;
                        while (digits < t.Name.Length && char.IsAsciiDigit(t.Name[digits])) digits++;
                        int num = digits > 0 && int.TryParse(t.Name.AsSpan(0, digits), out int x) ? x : 0;
                        string f = t.Value.ValueKind == JsonValueKind.String ? t.Value.GetString()
                            : t.Value.ValueKind == JsonValueKind.Object && t.Value.TryGetProperty("file", out var fv) && fv.ValueKind == JsonValueKind.String ? fv.GetString() : null;
                        existing.Add((t.Name, num, t.Value.GetRawText(), string.IsNullOrWhiteSpace(f) ? null : f));
                    }
                else if (p.Name == "_comment" && p.Value.ValueKind == JsonValueKind.Array)
                    comment = p.Value.EnumerateArray().Select(e => e.ToString()).ToList();
                else if (p.Name != "tracks") top.Add((p.Name, p.Value.GetRawText()));
            }
        }

        var first = findSound($"music/{numbers[0]}");
        int rate = first?.SampleRate ?? 22050;
        var library = Library(dir, rate);
        if (library.Count == 0) throw new UsageException($"no FLACs under {dir}");

        var entries = new Dictionary<int, string>();
        var total = System.Diagnostics.Stopwatch.StartNew();
        int found = 0, done = 0;
        foreach (int number in numbers)
        {
            done++;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string name = names.TryGetValue(number, out var nm) ? nm : number.ToString("D4", CultureInfo.InvariantCulture);
            string label = $"[{done,3}/{numbers.Count}] {name,-22}";
            var old = existing.FirstOrDefault(e => e.Number == number);
            string was = old.File != null ? $"  (was {old.File})" : "";
            var sound = findSound($"music/{number:D4}") ?? findSound($"music/{number}");
            if (sound == null || sound.Bits != 16 || sound.SampleRate != rate)
            {
                Console.WriteLine($"{label} skipped: {(sound == null ? "not in the bundles" : $"{sound.Bits}-bit {sound.SampleRate} Hz")}");
                continue;
            }
            float[] game = Mono(sound.Samples, sound.Channels);
            var ranked = Search(game, rate, library);
            var top1 = ranked.FirstOrDefault();
            string Rel(string f) => Path.GetRelativePath(Path.GetDirectoryName(outFile), f).Replace('\\', '/');
            if (top1 == null || top1.Score < MinScore)
            {
                entries[number] = $"{{ \"file\": \"\", \"score\": {Num(top1?.Score ?? 0)}{(top1 != null ? $", \"best\": {Json(Rel(top1.File))}" : "")} }}";
                Console.WriteLine($"{label} not found   (best {top1?.Score ?? 0:0.00}: {(top1 != null ? Path.GetFileName(top1.File) : "none")}){was}  ({sw.Elapsed.TotalSeconds:0.0} s)");
                continue;
            }
            Result r;
            try { r = MatchFile(sound, game, top1.File, Flac.Open(top1.File)); }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                Console.WriteLine($"{label} {Path.GetFileName(top1.File)}: {ex.Message}");
                continue;
            }
            string rel = Rel(r.File);
            if (r.Score < FoundScore)
            {
                entries[number] = $"{{ \"file\": \"\", \"score\": {Num(r.Score)}, \"best\": {Json(rel)} }}";
                Console.WriteLine($"{label} not found   (score {r.Score:0.00}, search {top1.Score:0.00}: {Path.GetFileName(r.File)}){was}  ({sw.Elapsed.TotalSeconds:0.0} s)");
                continue;
            }
            found++;
            var fields = new List<string>(r.Fields) { $"\"score\": {Num(r.Score)}", $"\"mode\": \"{(r.Whitened ? "whitened" : "plain")}\"" };
            fields[0] = $"\"file\": {Json(rel)}";
            if (r.Notes.Count > 0) fields.Add($"\"notes\": [{string.Join(", ", r.Notes.Select(Json))}]");
            if (old.File != null && !(MMUCS.Core.GameMods.Find(Path.GetDirectoryName(outFile), "music", old.File) is { } before && Path.GetFullPath(before).Equals(r.File, StringComparison.Ordinal)))
                fields.Add($"\"previously\": {Json(old.File)}");
            entries[number] = $"{{ {string.Join(", ", fields)} }}";
            string what = string.Join("  ", r.Fields.Skip(1).Where(f => !f.StartsWith("\"fadeMs")).Select(f => f.Replace("\"", "")));
            string changed = old.File != null && Path.GetFileName(old.File) != Path.GetFileName(r.File) ? $"  (was {old.File})" : "";
            Console.WriteLine($"{label} {r.Score:0.00} {(r.Whitened ? "whitened" : "plain   ")}  {Path.GetFileName(r.File)}  {what}{changed}  ({sw.Elapsed.TotalSeconds:0.0} s)");
            foreach (var note in r.Notes) Console.WriteLine($"{"",34}note: {note}");
        }

        // FILE's keys in their order, with the game's tracks matched; then the tracks it hadn't.
        var lines = new List<string>();
        var written = new HashSet<int>();
        foreach (var e in existing)
        {
            if (e.Number == 0) lines.Add($"    {Json(e.Key)}: {e.Raw}");
            else if (entries.TryGetValue(e.Number, out var v) && written.Add(e.Number)) lines.Add($"    {Json(e.Key)}: {v}");
            else if (!entries.ContainsKey(e.Number) && written.Add(e.Number)) lines.Add($"    {Json(e.Key)}: {e.Raw}");
        }
        foreach (var (number, v) in entries.OrderBy(kv => kv.Key))
            if (written.Add(number)) lines.Add($"    {Json(names.TryGetValue(number, out var nm) ? nm : number.ToString("D4", CultureInfo.InvariantCulture))}: {v}");

        comment ??= ["Which game tracks this soundtrack stands in for."];
        comment = comment.Where(c => !c.Contains("first guesses", StringComparison.OrdinalIgnoreCase) && !c.StartsWith("Matched by mprobe match all")).ToList();
        comment.Add("Matched by mprobe match all: \"score\" is how much of the game's track was found in the file, by how well (0 to 1), " +
                    "\"mode\" whether plain or whitened correlation found more, \"notes\" what it couldn't place; a blank \"file\" wasn't found (\"best\" is the nearest).");
        var sb = new StringBuilder("{\n");
        sb.Append($"  \"_comment\": [\n{string.Join(",\n", comment.Select(c => "    " + Json(c)))}\n  ],\n");
        if (!top.Any(t => t.Name == "enabled")) sb.Append("  \"enabled\": true,\n");
        foreach (var (name, raw) in top) sb.Append($"  {Json(name)}: {raw},\n");
        sb.Append($"  \"tracks\": {{\n{string.Join(",\n", lines)}\n  }}\n}}\n");
        File.WriteAllText(outFile, sb.ToString());
        Console.WriteLine($"{found} of {numbers.Count} tracks found in {total.Elapsed:m\\:ss} → {outFile}");
        return 0;
    }

    private static readonly JsonSerializerOptions JsonText = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static string Json(string s) => JsonSerializer.Serialize(s, JsonText);
    private static string Num(double v) => v.ToString("0.000", CultureInfo.InvariantCulture);

    // ---- what to match ----

    private static int TrackNumber(string arg, Dictionary<int, string> names)
    {
        if (int.TryParse(arg, out int number)) return number;
        var hits = names.Where(kv => kv.Value.Equals(arg, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hits.Count == 0) hits = names.Where(kv => kv.Value.Contains(arg, StringComparison.OrdinalIgnoreCase)).ToList();
        return hits.Count switch
        {
            1 => hits[0].Key,
            0 => throw new UsageException($"no game track named like '{arg}'"),
            _ => throw new UsageException($"'{arg}' could be {string.Join(", ", hits.OrderBy(h => h.Key).Select(h => h.Value))}"),
        };
    }

    private static string FindFile(string arg, string dir)
    {
        if (File.Exists(arg)) return Path.GetFullPath(arg);
        if (dir != null && Directory.Exists(dir))
        {
            var hits = Directory.EnumerateFiles(dir, "*.flac", SearchOption.AllDirectories)
                .Where(f => Path.GetFileName(f).Contains(arg, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count == 1) return hits[0];
            if (hits.Count > 1) throw new UsageException($"'{arg}' could be {string.Join(", ", hits.Select(Path.GetFileName))}");
        }
        throw new UsageException($"no FLAC '{arg}'{(dir != null ? $" (in {dir})" : "")}");
    }

    /// <summary>The track's chunks: 4 s each, the last as short as 1 s (or the whole track, when it's shorter).</summary>
    private static IEnumerable<(long Start, long Length)> ChunkStarts(long frames, int rate)
    {
        long size = (long)(ChunkSeconds * rate), min = (long)(MinChunkSeconds * rate);
        for (long s = 0; s < frames; s += size)
        {
            long len = Math.Min(size, frames - s);
            if (len >= min || s == 0) yield return (s, len);
        }
    }

    // ---- search ----

    /// <summary>Every FLAC under <paramref name="dir"/>, decoded a few at a time and kept at the search's rate.</summary>
    private static List<Entry> Library(string dir, int rate)
    {
        var files = Directory.EnumerateFiles(dir, "*.flac", SearchOption.AllDirectories).Order().ToList();
        var entries = new Entry[files.Count];
        int done = 0;
        bool live = !Console.IsOutputRedirected;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Parallel.For(0, files.Count, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i =>
        {
            try
            {
                var flac = Flac.Open(files[i]);
                var audio = Resample(Mono(Decode(flac), flac.Channels), flac.SampleRate, rate);
                entries[i] = new Entry(files[i], Decimate(audio, SearchDecimation), Decimate(Whiten(audio), SearchDecimation));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                Console.Error.WriteLine($"  {Path.GetFileName(files[i])}: {ex.Message}");
            }
            int d = System.Threading.Interlocked.Increment(ref done);
            if (live) lock (entries) Console.Write($"\rdecoding the FLACs in {dir}: {d}/{files.Count}");
        });
        if (live) Console.Write("\r");
        Console.WriteLine($"decoded {files.Count} FLACs in {dir} ({sw.Elapsed.TotalSeconds:0.0} s)");
        return entries.Where(e => e != null).ToList();
    }

    /// <summary>
    /// The files in <paramref name="library"/>, best first, each scored in both modes by the median of a few
    /// chunks' best correlations.
    /// </summary>
    private static List<Ranked> Search(float[] game, int rate, List<Entry> library)
    {
        var loud = ChunkStarts(game.Length, rate).Where(c => !Silent(game, c.Start, c.Length)).ToList();
        if (loud.Count == 0) return [];
        int count = Math.Min(SearchChunks, loud.Count);
        var probes = Enumerable.Range(0, count).Select(i => loud[(int)((i + 0.5) * loud.Count / count)]).ToList();
        var plain = Decimate(game, SearchDecimation);
        var white = Decimate(Whiten(game), SearchDecimation);
        var templates = probes.Select(p => (
            Plain: Slice(plain, p.Start / SearchDecimation, p.Length / SearchDecimation),
            White: Slice(white, p.Start / SearchDecimation, p.Length / SearchDecimation))).ToList();
        var cache = new ConcurrentDictionary<(int Probe, bool White, int N), (double[] Re, double[] Im)>();

        var ranked = new Ranked[library.Count];
        Parallel.For(0, library.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Math.Min(8, Environment.ProcessorCount)) }, i =>
        {
            var e = library[i];
            double[] Scores(float[] audio, bool w)
            {
                var spectrum = new Spectrum(audio);
                return templates.Select((t, k) =>
                {
                    var tt = w ? t.White : t.Plain;
                    if (tt.Length < 2 || tt.Length > audio.Length) return 0;
                    var ts = cache.GetOrAdd((k, w, spectrum.Size), key => Spectrum.Transform(tt, key.N));
                    return Peaks(spectrum.Ncc(tt, ts), 1, rate / SearchDecimation).FirstOrDefault()?.Score ?? 0;
                }).ToArray();
            }
            var p = Scores(e.Plain, false);
            var wh = Scores(e.White, true);
            double mp = Median(p), mw = Median(wh);
            ranked[i] = new Ranked(e.File, mp, mw, mw > mp ? wh : p);
        });
        return ranked.OrderByDescending(r => r.Score).ToList();
    }

    /// <summary>The files the mod.jsons under <paramref name="dir"/> map game track <paramref name="number"/> to.</summary>
    private static HashSet<string> MappedFiles(string dir, int number)
    {
        var files = new HashSet<string>();
        foreach (var map in Directory.EnumerateFiles(dir, "mod.json", SearchOption.AllDirectories))
            foreach (var (_, value) in ModKeys(map, number))
                if (value != null && MMUCS.Core.GameMods.Find(Path.GetDirectoryName(map), "music", value) is { } found) files.Add(Path.GetFullPath(found));
        return files;
    }

    /// <summary>The mod.json of the mod a file is in: in its folder or a folder above it (a mod keeps its audio under music/).</summary>
    private static string ModJsonOver(string file)
    {
        for (var dir = Directory.GetParent(Path.GetFullPath(file)); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "mod.json"))) return Path.Combine(dir.FullName, "mod.json");
        return null;
    }

    /// <summary>The keys of <paramref name="map"/>'s tracks for game track <paramref name="number"/>, with the file each names.</summary>
    private static IEnumerable<(string Key, string File)> ModKeys(string map, int number)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(File.ReadAllText(map), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (Exception ex) when (ex is JsonException or IOException) { yield break; }
        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("tracks", out var tracks) || tracks.ValueKind != JsonValueKind.Object) yield break;
            string prefix = number.ToString(CultureInfo.InvariantCulture);
            foreach (var p in tracks.EnumerateObject())
            {
                if (!p.Name.StartsWith(prefix) || (p.Name.Length > prefix.Length && char.IsAsciiDigit(p.Name[prefix.Length]))) continue;
                string file = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()
                    : p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty("file", out var f) ? f.GetString() : null;
                yield return (p.Name, string.IsNullOrWhiteSpace(file) ? null : file);
            }
        }
    }

    // ---- matching ----

    /// <summary>The game's track and the file, whitened, at the game's rate and coarse, for finding stretches of one in the other.</summary>
    private sealed class Signals(float[] game, float[] audio, int rate)
    {
        public readonly float[] Game = game, Audio = audio, CoarseGame = Decimate(game);
        public readonly Spectrum Spectrum = new(Decimate(audio));
        public readonly int Rate = rate;
        public List<Chunk> Chunks;
    }

    /// <summary>
    /// Each chunk found in the file: see <see cref="Find"/>, carrying on from the chunk before. Delta is the
    /// file's position less the game's, in the game's frames.
    /// </summary>
    private static List<Chunk> MatchChunks(Signals sig, List<(long Start, long Length)> chunks)
    {
        // The coarse correlations are the work: all at once, then each chunk picked in order with the one before as its hint.
        var peaks = new List<Candidate>[chunks.Count];
        Parallel.For(0, chunks.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Math.Min(6, Environment.ProcessorCount)) },
            i => peaks[i] = CoarsePeaks(sig, chunks[i].Start, chunks[i].Length));
        var result = new List<Chunk>();
        double? previous = null;
        for (int i = 0; i < chunks.Count; i++)
        {
            var c = Find(sig, chunks[i].Start, chunks[i].Length, previous, peaks[i]);
            result.Add(c);
            if (c.Kind != ChunkKind.Silent) previous = c.Kind == ChunkKind.Matched ? c.Delta : null;
        }
        return result;
    }

    /// <summary>The best few coarse lags for a stretch of the track; null when it's silent or can't be placed.</summary>
    private static List<Candidate> CoarsePeaks(Signals sig, long start, long length)
    {
        if (Silent(sig.Game, start, length)) return null;
        var template = Slice(sig.CoarseGame, start / Decimation, length / Decimation);
        if (template.Length < 2 || length > sig.Audio.Length) return null;
        return Peaks(sig.Spectrum.Ncc(template), 4, sig.Rate / Decimation);
    }

    /// <summary>
    /// A stretch of the game's track found in the file: the best few coarse lags, the one at <paramref name="hint"/>
    /// (a delta) when it's nearly as good (a loop played twice matches twice), refined to the sample.
    /// </summary>
    private static Chunk Find(Signals sig, long start, long length, double? hint, List<Candidate> peaks = null)
    {
        if (Silent(sig.Game, start, length)) return new Chunk(start, length, ChunkKind.Silent, 0, 0);
        peaks ??= CoarsePeaks(sig, start, length);
        if (peaks == null) return new Chunk(start, length, ChunkKind.Unmatched, 0, 0);
        var pick = peaks.FirstOrDefault();
        if (pick != null && hint is double h)
        {
            var near = peaks.FirstOrDefault(c => Math.Abs(c.Lag * Decimation - start - h) <= 2 * Decimation && c.Score >= 0.8 * pick.Score);
            if (near != null) pick = near;
        }
        if (pick == null || pick.Score < MinScore) return new Chunk(start, length, ChunkKind.Unmatched, 0, pick?.Score ?? 0);
        var (lag, score) = Refine(sig.Game, sig.Audio, start, length, (long)pick.Lag * Decimation);
        double second = peaks.Where(c => c != pick).Select(c => c.Score).DefaultIfEmpty(0).Max();
        // Found when it correlates well, or plainly better there than anywhere else.
        var kind = score >= MatchScore || score >= MinScore && second <= score / 2 ? ChunkKind.Matched : ChunkKind.Unmatched;
        return new Chunk(start, length, kind, lag - start, score, second);
    }

    /// <summary>
    /// A game frame in the file's seconds, found by the 4 s of the track starting there (or, for an end,
    /// ending there): a point at a cut, where a chunk holds both sides of it, is found as well as any. Failing
    /// that, by the offset of the chunk holding it (<see cref="Map"/>).
    /// </summary>
    private static double? Point(Signals sig, long frame, bool isEnd)
    {
        long size = (long)(ChunkSeconds * sig.Rate);
        long from = Math.Max(0, isEnd ? frame - size : frame), to = Math.Min(sig.Game.Length, isEnd ? frame : frame + size);
        double? hint = Delta(sig.Chunks, frame, isEnd) ?? Nearest(sig.Chunks, frame);
        if (to - from >= MinChunkSeconds * sig.Rate)
        {
            var c = Find(sig, from, to - from, hint);
            if (c.Kind == ChunkKind.Matched) return (frame + c.Delta) / sig.Rate;
        }
        return Map(sig.Chunks, frame, isEnd, sig.Rate);
    }

    /// <summary>
    /// How far the track carries on in the file from <paramref name="from"/>'s offset: the run of found chunks
    /// there, then 2 s windows a quarter second apart while they still correlate at that offset. The last game
    /// frame and its time in the file; null when nothing near <paramref name="from"/> is found.
    /// </summary>
    private static (long Frame, double At)? Extent(Signals sig, long from)
    {
        var chunks = sig.Chunks;
        var first = chunks.Where(c => c.Kind == ChunkKind.Matched).MinBy(c => Math.Abs(c.Start - from));
        if (first == null) return null;
        double d = first.Delta;
        int k = chunks.IndexOf(first);
        while (k + 1 < chunks.Count && (chunks[k + 1].Kind == ChunkKind.Silent ||
               chunks[k + 1].Kind == ChunkKind.Matched && Math.Abs(chunks[k + 1].Delta - d) <= 8)) k++;
        while (k > 0 && chunks[k].Kind == ChunkKind.Silent) k--;
        long last = chunks[k].Start + chunks[k].Length;
        int window = 2 * sig.Rate, step = sig.Rate / 4;
        for (long t = last - window + step; t + window <= sig.Game.Length; t += step)
        {
            long lag = (long)Math.Round(t + d);
            if (lag < 0 || lag + window > sig.Audio.Length) break;
            if (Silent(sig.Game, t, window)) { last = t + window; continue; }
            if (Refine(sig.Game, sig.Audio, t, window, lag).Score < 0.45) break;
            last = t + window;
        }
        last = Math.Min(last, (long)(sig.Audio.Length - d));
        return (last, (last + d) / sig.Rate);
    }

    /// <summary>The delta of the found chunk nearest <paramref name="frame"/>, within a chunk either side.</summary>
    private static double? Nearest(List<Chunk> chunks, long frame) =>
        chunks.Where(c => c.Kind == ChunkKind.Matched && c.Start - c.Length <= frame && frame < c.Start + 2 * c.Length)
            .OrderBy(c => Math.Abs(c.Start + c.Length / 2 - frame)).Select(c => (double?)c.Delta).FirstOrDefault();

    /// <summary>The lag near <paramref name="around"/> where the chunk correlates best at the game's rate, to a fraction of a sample.</summary>
    private static (double Lag, double Score) Refine(float[] game, float[] audio, long start, long length, long around)
    {
        double gameEnergy = 0;
        for (long i = 0; i < length; i++) gameEnergy += game[start + i] * (double)game[start + i];
        long from = Math.Max(0, around - 2 * Decimation), to = Math.Min(audio.Length - length, around + 2 * Decimation);
        if (to < from) return (around, 0);
        var ncc = new double[to - from + 1];
        for (long lag = from; lag <= to; lag++)
        {
            double dot = 0, energy = 0;
            for (long i = 0; i < length; i++)
            {
                double f = audio[lag + i];
                dot += game[start + i] * f;
                energy += f * f;
            }
            ncc[lag - from] = gameEnergy > 0 && energy > 0 ? dot / Math.Sqrt(gameEnergy * energy) : 0;
        }
        int best = 0;
        for (int i = 1; i < ncc.Length; i++) if (ncc[i] > ncc[best]) best = i;
        double offset = 0;
        if (best > 0 && best < ncc.Length - 1)
        {
            double l = ncc[best - 1], c = ncc[best], r = ncc[best + 1], d = l - 2 * c + r;
            if (d < 0) offset = 0.5 * (l - r) / d;
        }
        return (from + best + offset, ncc[best]);
    }

    /// <summary>Up to <paramref name="count"/> highest correlations, at least a second apart.</summary>
    private static List<Candidate> Peaks(double[] ncc, int count, int apart)
    {
        var peaks = new List<Candidate>();
        for (int k = 0; k < count; k++)
        {
            int best = -1;
            for (int i = 0; i < ncc.Length; i++)
                if ((best < 0 || ncc[i] > ncc[best]) && peaks.All(p => Math.Abs(p.Lag - i) >= apart)) best = i;
            if (best < 0) break;
            peaks.Add(new Candidate(best, ncc[best]));
        }
        return peaks;
    }

    // ---- results ----

    private static void PrintSpans(List<Chunk> chunks, int rate)
    {
        Console.WriteLine("game → file (runs of 4 s chunks at one offset):");
        for (int i = 0; i < chunks.Count;)
        {
            var first = chunks[i];
            int j = i + 1;
            while (j < chunks.Count && chunks[j].Kind == first.Kind &&
                   (first.Kind != ChunkKind.Matched || Math.Abs(chunks[j].Delta - chunks[j - 1].Delta) <= 8)) j++;
            var run = chunks.GetRange(i, j - i);
            long start = first.Start, end = run[^1].Start + run[^1].Length;
            string game = $"{Clock(start / (double)rate)}–{Clock(end / (double)rate)}";
            if (first.Kind == ChunkKind.Matched)
            {
                double d0 = run[0].Delta, d1 = run[^1].Delta;
                string drift = Math.Abs(d1 - d0) > 1 ? $", drifts {(d1 - d0) / rate * 1000:0.0} ms" : "";
                Console.WriteLine($"  {game,-21} → {Clock((start + d0) / rate)}–{Clock((end + d1) / rate)}" +
                    $"  score {run.Min(c => c.Score):0.000}–{run.Max(c => c.Score):0.000}{drift}");
            }
            else Console.WriteLine($"  {game,-21}   {(first.Kind == ChunkKind.Silent ? "silent" : $"not found (best {run.Max(c => c.Score):0.00})")}");
            i = j;
        }
    }

    /// <summary>
    /// A game frame in the file's seconds, by the offset of the chunk holding it: the chunk before, for an end
    /// (a jump's "at", the end of the data); a silent chunk takes its nearest found neighbour's. Null when that's not found.
    /// </summary>
    private static double? Map(List<Chunk> chunks, long frame, bool isEnd, int rate) =>
        Delta(chunks, frame, isEnd) is double d ? (frame + d) / rate : null;

    /// <summary>The file's position less the game's at <paramref name="frame"/>, as <see cref="Map"/> finds it.</summary>
    private static double? Delta(List<Chunk> chunks, long frame, bool isEnd)
    {
        if (chunks.Count == 0) return null;
        long at = isEnd ? frame - 1 : frame;
        int k = Math.Max(0, chunks.FindLastIndex(c => c.Start <= at));
        if (chunks[k].Kind == ChunkKind.Unmatched) return null;
        // Only silence may be crossed to a neighbour, the side before first for an end.
        int found = chunks[k].Kind == ChunkKind.Matched ? k : -1;
        foreach (int step in isEnd ? new[] { -1, 1 } : [1, -1])
        {
            for (int t = k + step; found < 0 && t >= 0 && t < chunks.Count; t += step)
            {
                if (chunks[t].Kind == ChunkKind.Matched) found = t;
                else if (chunks[t].Kind == ChunkKind.Unmatched) break;
            }
        }
        return found < 0 ? null : chunks[found].Delta;
    }

    private static void PrintJumps(ImusSound sound, Signals sig)
    {
        var chunks = sig.Chunks;
        int rate = sig.Rate;
        if (sound.Jumps.Count == 0) return;
        Console.WriteLine("the game's jumps (game time → file time):");
        foreach (var j in sound.Jumps)
        {
            long at = sound.ToFrame(j.Offset), to = sound.ToFrame(j.Destination);
            Console.WriteLine($"  {Clock(at / (double)rate)} → {Clock(to / (double)rate)}  hook {j.Hook}  fade {j.FadeMs} ms" +
                $"   file {Clock(j.Hook == 0 ? Point(sig, at, true) : Map(chunks, at, true, rate))} → {Clock(j.Hook == 0 ? Point(sig, to, false) : Map(chunks, to, false, rate))}");
        }
    }

    /// <summary>
    /// The mod.json entry: from where the game starts playing (past a hook-0 jump at the very start) to the end
    /// of the data, looping as the game's hook-0 jump back does. Its end is left out when the game's tail isn't
    /// next to the loop's end in the file (the file plays on past the loop's end then, which is what the
    /// crossfade wants from a file that plays the loop again).
    /// </summary>
    private static (List<string> Fields, List<string> Notes) Suggest(ImusSound sound, Signals sig, string file)
    {
        var chunks = sig.Chunks;
        int rate = sig.Rate;
        long fileFrames = sig.Audio.Length;
        long frames = sound.Stop is { } stop ? Math.Min(sound.ToFrame(stop), sound.FrameCount) : sound.FrameCount;
        long playStart = 0;
        var lead = sound.Jumps.FirstOrDefault(j => j.Hook == 0 && sound.ToFrame(j.Offset) == 0 && sound.ToFrame(j.Destination) > 0);
        if (lead != default) playStart = sound.ToFrame(lead.Destination);
        var loop = sound.Jumps.FirstOrDefault(j => j.Hook == 0 && sound.ToFrame(j.Destination) < sound.ToFrame(j.Offset) && sound.ToFrame(j.Offset) > playStart);

        var notes = new List<string>();
        var fields = new List<string> { $"\"file\": {Json(Path.GetFileName(file))}" };
        double? start = Point(sig, playStart, false);
        if (start is double s) fields.Add($"\"start\": \"{Clock(Math.Max(0, s))}\"");
        else if (chunks.Where(c => c.Kind == ChunkKind.Matched).MinBy(c => Math.Abs(c.Start - playStart)) is { } near && playStart + near.Delta < 0)
            notes.Add($"the file starts {Clock(-(playStart + near.Delta) / rate)} after the game's track starts playing ({Clock(playStart / (double)rate)}), so it plays from the file's start");
        else notes.Add($"the start ({Clock(playStart / (double)rate)} in the game) isn't found in the file");

        if (loop != default)
        {
            long at = sound.ToFrame(loop.Offset), to = sound.ToFrame(loop.Destination);
            double? loopStart = Point(sig, to, false), loopEnd = Point(sig, at, true);
            if (loopStart is double ls) fields.Add($"\"loopStart\": \"{Clock(ls)}\"");
            else notes.Add($"the loop's start ({Clock(to / (double)rate)} in the game) isn't found in the file");
            if (loopEnd is double le)
            {
                fields.Add($"\"loopEnd\": \"{Clock(le)}\"");
                // The game's tail (after the loop's end) where it carries on from there in the file, as far as
                // the file goes: every chunk of it found is at the loop's end's offset.
                double d = le * rate - at;
                var tail = chunks.Where(c => c.Start + c.Length > at && c.Start < frames && c.Start + d < fileFrames).ToList();
                bool carriesOn = tail.Count == 0 || tail.All(c => c.Kind != ChunkKind.Unmatched || c.Start + c.Length + d > fileFrames) &&
                                 tail.Where(c => c.Kind == ChunkKind.Matched).All(c => Math.Abs(c.Delta - d) <= 8);
                double e = Math.Min(frames + d, fileFrames) / rate;
                if (!carriesOn) notes.Add("the game's tail after the loop isn't next to the loop's end in the file, so the end is left at the file's");
                else if (frames + d < fileFrames - rate / 100) fields.Insert(2, $"\"end\": \"{Clock(e)}\"");
            }
            else notes.Add($"the loop's end ({Clock(at / (double)rate)} in the game) isn't found in the file");
            if (loop.FadeMs != 500) fields.Add($"\"fadeMs\": {loop.FadeMs}");
        }
        else
        {
            // A sequence gives way to the state's music at its "_end" marker, which a mod's entry puts at loopEnd;
            // the audio after it plays out under the crossfade.
            long? marker = sound.Markers.Where(m => m.Text == "_end").Select(m => (long?)sound.ToFrame(m.Offset)).FirstOrDefault();
            if (marker is long mk && (mk <= playStart || mk >= frames)) marker = null;
            double? end = Point(sig, frames, true);
            double? endMark = marker is long m0 ? Point(sig, m0, true) : null;
            double fileEnd = fileFrames / (double)rate;
            if (end == null && Extent(sig, playStart) is var (lastFrame, lastAt))
            {
                // The file has the track only so far (another cut of the album follows): it ends where the match does.
                if (marker is long m1 && m1 <= lastFrame) end = endMark is double em ? Math.Min(lastAt, em + (lastFrame - m1) / (double)rate) : lastAt;
                else
                {
                    end = lastAt;
                    endMark = null;
                }
                notes.Add($"the file has the game's track only as far as {Clock(lastFrame / (double)rate)} of it" +
                    $"{(marker is long m2 ? $" (its \"_end\" is at {Clock(m2 / (double)rate)})" : "")}, so it ends there, at {Clock(lastAt)}");
            }
            if (end is double e && e < fileEnd - 0.01) fields.Add($"\"end\": \"{Clock(e)}\"");
            else if (end == null) notes.Add("the end isn't found in the file, so it's left at the file's");
            if (endMark is double ek && (end is not double e2 || ek < e2 - 0.01)) fields.Add($"\"loopEnd\": \"{Clock(ek)}\"");
            notes.Add($"the game's track doesn't loop back on its own (no hook-0 jump back), so neither does this{(endMark != null ? "; loopEnd is its \"_end\" marker" : "")}");
        }
        if (sound.Jumps.Any(j => j.Hook != 0)) notes.Add("the jumps with a hook (transitions the scripts pick) have no place in a mod's entry");

        return (fields, notes);
    }

    // ---- signals ----

    private static byte[] Decode(Flac flac)
    {
        var pcm = new byte[flac.TotalFrames * flac.Channels * 2];
        long got = flac.Decode16(0, flac.TotalFrames, pcm);
        return got == flac.TotalFrames ? pcm : pcm.AsSpan(0, (int)(got * flac.Channels * 2)).ToArray();
    }

    private static float[] Mono(ReadOnlySpan<byte> pcm16, int channels)
    {
        int frames = pcm16.Length / (2 * channels);
        var mono = new float[frames];
        for (int f = 0, o = 0; f < frames; f++)
        {
            int sum = 0;
            for (int c = 0; c < channels; c++, o += 2) sum += BinaryPrimitives.ReadInt16LittleEndian(pcm16[o..]);
            mono[f] = sum / (32768f * channels);
        }
        return mono;
    }

    /// <summary>From one rate to another: an average over each output sample's span going down, straight lines going up.</summary>
    private static float[] Resample(float[] src, int from, int to)
    {
        if (from == to) return src;
        double ratio = from / (double)to;
        var dst = new float[(long)(src.Length / ratio)];
        if (ratio < 1)
        {
            for (long i = 0; i < dst.Length; i++)
            {
                double x = i * ratio;
                long a = (long)x;
                double t = x - a;
                dst[i] = (float)(src[a] * (1 - t) + (a + 1 < src.Length ? src[a + 1] : 0) * t);
            }
            return dst;
        }
        for (long i = 0; i < dst.Length; i++)
        {
            double x0 = i * ratio, x1 = x0 + ratio, sum = 0;
            for (long k = (long)x0; k < Math.Min(Math.Ceiling(x1), src.Length); k++)
                sum += src[k] * (Math.Min(x1, k + 1) - Math.Max(x0, k));
            dst[i] = (float)(sum / ratio);
        }
        return dst;
    }

    /// <summary>
    /// The signal's differences, which tilt its spectrum towards the treble: a remaster or another mix of the
    /// same recording differs most in the bass's level and phase, which plain correlation is ruled by.
    /// </summary>
    private static float[] Whiten(float[] s)
    {
        var d = new float[s.Length];
        for (int i = 1; i < s.Length; i++) d[i] = (s[i] - s[i - 1]) * 4;
        return d;
    }

    private static float[] Decimate(float[] src, int by = Decimation)
    {
        var dst = new float[src.Length / by];
        for (int i = 0; i < dst.Length; i++)
        {
            float sum = 0;
            for (int k = 0; k < by; k++) sum += src[i * by + k];
            dst[i] = sum / by;
        }
        return dst;
    }

    private static float[] Slice(float[] src, long start, long length) =>
        src.AsSpan((int)Math.Min(start, src.Length), (int)Math.Max(0, Math.Min(length, src.Length - start))).ToArray();

    private static bool Silent(float[] s, long start, long length)
    {
        double energy = 0;
        for (long i = start; i < start + length; i++) energy += s[i] * (double)s[i];
        return Math.Sqrt(energy / Math.Max(1, length)) < 1e-3;   // about −60 dB
    }

    private static double Median(double[] v)
    {
        if (v.Length == 0) return 0;
        var s = v.Order().ToArray();
        return s.Length % 2 == 1 ? s[s.Length / 2] : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2;
    }

    private static string Clock(double? seconds)
    {
        if (seconds is not double s) return "?";
        string sign = s < 0 ? "-" : "";
        s = Math.Abs(s);
        int m = (int)(s / 60);
        return $"{sign}{m}:{(s - m * 60).ToString("00.000", CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// A signal's spectrum, for normalised cross-correlation against shorter templates: ncc[lag] is the
    /// template's dot product with the signal from lag, over both their lengths' energies.
    /// </summary>
    private sealed class Spectrum
    {
        private readonly int _n;
        private readonly double[] _re, _im, _energy;   // _energy[i]: sum of squares of the signal's first i samples
        private readonly int _length;

        /// <summary>The FFT's size: the signal's length, to a power of two (a lag that fits never wraps round).</summary>
        public int Size => _n;

        public Spectrum(float[] signal)
        {
            _length = signal.Length;
            _n = 1;
            while (_n < signal.Length) _n <<= 1;
            _re = new double[_n];
            _im = new double[_n];
            _energy = new double[signal.Length + 1];
            for (int i = 0; i < signal.Length; i++)
            {
                _re[i] = signal[i];
                _energy[i + 1] = _energy[i] + signal[i] * (double)signal[i];
            }
            Fft(_re, _im, false);
        }

        /// <summary>A template's spectrum at size <paramref name="n"/>, to reuse against signals of that size.</summary>
        public static (double[] Re, double[] Im) Transform(float[] template, int n)
        {
            var re = new double[n];
            var im = new double[n];
            for (int i = 0; i < template.Length; i++) re[i] = template[i];
            Fft(re, im, false);
            return (re, im);
        }

        public double[] Ncc(float[] template, (double[] Re, double[] Im)? transformed = null)
        {
            int lags = _length - template.Length + 1;
            if (lags <= 0) return [];
            var (tre, tim) = transformed ?? Transform(template, _n);
            double energy = 0;
            foreach (float t in template) energy += t * (double)t;
            // The signal's spectrum times the template's conjugate: their correlation.
            var re = new double[_n];
            var im = new double[_n];
            for (int i = 0; i < _n; i++)
            {
                re[i] = _re[i] * tre[i] + _im[i] * tim[i];
                im[i] = _im[i] * tre[i] - _re[i] * tim[i];
            }
            Fft(re, im, true);
            var ncc = new double[lags];
            for (int lag = 0; lag < lags; lag++)
            {
                double window = _energy[lag + template.Length] - _energy[lag];
                ncc[lag] = window > 1e-9 && energy > 0 ? re[lag] / _n / Math.Sqrt(window * energy) : 0;
            }
            return ncc;
        }

        /// <summary>In-place radix-2 FFT; the inverse isn't scaled.</summary>
        private static void Fft(double[] re, double[] im, bool inverse)
        {
            int n = re.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j)
                {
                    (re[i], re[j]) = (re[j], re[i]);
                    (im[i], im[j]) = (im[j], im[i]);
                }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                int half = len >> 1;
                double angle = (inverse ? 2 : -2) * Math.PI / len;
                var wr = new double[half];
                var wi = new double[half];
                for (int k = 0; k < half; k++) { wr[k] = Math.Cos(angle * k); wi[k] = Math.Sin(angle * k); }
                for (int i = 0; i < n; i += len)
                    for (int k = 0; k < half; k++)
                    {
                        int a = i + k, b = a + half;
                        double xr = re[b] * wr[k] - im[b] * wi[k], xi = re[b] * wi[k] + im[b] * wr[k];
                        re[b] = re[a] - xr;
                        im[b] = im[a] - xi;
                        re[a] += xr;
                        im[a] += xi;
                    }
            }
        }
    }
}
