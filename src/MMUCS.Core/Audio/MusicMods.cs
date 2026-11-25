using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MMUCS.Core.Audio;

/// <summary>
/// Music mods: replacement tracks the game plays in place of its own. They live in "_mods/music/&lt;mod&gt;/" beside
/// the game (the export's layout, one folder per mod), each with a mod.json saying which of the game's tracks its
/// audio files stand in for. A track is named by its file number, as the export names it ("music/1100"); the key
/// may carry the name too ("1100-Hold1"). A value is the file, or an object:
/// <code>
/// {
///   "tracks": {
///     "1099-Menu": "01 - The Adventure Continues.flac",
///     "1100-Hold1": { "file": "03 - The Curse of Monkey Island.flac", "start": 12.5, "end": "2:31", "loopStart": 20, "loop": true, "fadeMs": 500 }
///   }
/// }
/// </code>
/// Times are seconds or "m:ss.s" in the file: the track plays from start to end (the whole file by default) and,
/// when it loops, jumps from loopEnd (the end) back to loopStart (the start), crossfading over fadeMs into the
/// audio after loopEnd. Without "loop" it loops when the game's track does: an interlude that ends by running
/// out (2105-Interlude1) has to end here too, or the scripts' music after it never comes. Its "_end" marker (where a sequence gives way to the state's music) is at loopEnd.
/// A blank value is a track still to be mapped, and a key without a track number (music baked into a cutscene,
/// "opening.san title") is kept for later; both are skipped. A mod.json with "enabled": false is skipped too;
/// where two mods map one track, the folder first by name wins.
/// </summary>
public sealed class MusicMods
{
    /// <summary>
    /// One replacement: the mod, its audio file and the stretch of it that plays and loops (seconds). Loop null
    /// does what the game's track does (see <see cref="Build"/>).
    /// </summary>
    public sealed record Track(string Mod, string File, double Start, double? End, double? LoopStart, double? LoopEnd, bool? Loop, int FadeMs);

    private readonly Dictionary<int, Track> _tracks = [];

    /// <summary>The replacements by the game track's file number.</summary>
    public IReadOnlyDictionary<int, Track> Tracks => _tracks;

    /// <summary>The mods found (folder names), whether or not they're enabled.</summary>
    public List<string> Mods { get; } = [];

    /// <summary>What couldn't be read: a broken mod.json, a missing file, a track number the game doesn't have.</summary>
    public List<string> Problems { get; } = [];

    /// <summary>The music mods folder for <paramref name="gameFile"/>: "_mods/music" beside it.</summary>
    public static string Folder(string gameFile) =>
        string.IsNullOrEmpty(gameFile) || Path.GetDirectoryName(Path.GetFullPath(gameFile)) is not { } dir
            ? null : Path.Combine(dir, "_mods", "music");

    /// <summary>The music mods beside <paramref name="gameFile"/>; none when there's no folder.</summary>
    public static MusicMods Load(string gameFile)
    {
        var mods = new MusicMods();
        if (Folder(gameFile) is not { } folder || !Directory.Exists(folder)) return mods;
        foreach (var dir in Directory.GetDirectories(folder).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            string map = Path.Combine(dir, "mod.json");
            if (!File.Exists(map)) continue;
            mods.Mods.Add(Path.GetFileName(dir));
            try { mods.Read(dir, File.ReadAllText(map)); }
            catch (Exception ex) when (ex is JsonException or IOException or FormatException or InvalidOperationException)
            {
                mods.Problems.Add($"{Path.GetFileName(dir)}/mod.json: {ex.Message}");
            }
        }
        return mods;
    }

    private void Read(string dir, string json)
    {
        string mod = Path.GetFileName(dir);
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = doc.RootElement;
        if (root.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.False) return;
        if (!root.TryGetProperty("tracks", out var tracks) || tracks.ValueKind != JsonValueKind.Object) return;
        foreach (var p in tracks.EnumerateObject())
        {
            int digits = 0;
            while (digits < p.Name.Length && char.IsAsciiDigit(p.Name[digits])) digits++;
            // Keys without a track number (music baked into a cutscene: "opening.san title") aren't played yet.
            if (digits == 0 || !int.TryParse(p.Name.AsSpan(0, digits), out int number)) continue;
            var v = p.Value;
            // A blank entry is a track still to be mapped.
            if (v.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(v.GetString())) continue;
            Track track = v.ValueKind == JsonValueKind.String
                ? new Track(mod, Path.Combine(dir, v.GetString()), 0, null, null, null, null, 500)
                : new Track(mod, Path.Combine(dir, v.GetProperty("file").GetString()),
                    Time(v, "start") ?? 0, Time(v, "end"), Time(v, "loopStart"), Time(v, "loopEnd"),
                    v.TryGetProperty("loop", out var loop) && loop.ValueKind is JsonValueKind.True or JsonValueKind.False ? loop.GetBoolean() : null,
                    v.TryGetProperty("fadeMs", out var fade) && fade.TryGetInt32(out int ms) ? ms : 500);
            if (!File.Exists(track.File))
            {
                Problems.Add($"{mod}: {p.Name}: {Path.GetFileName(track.File)} isn't there");
                continue;
            }
            _tracks.TryAdd(number, track);
        }
    }

    /// <summary>A time in seconds: a number, or "m:ss.s" / "h:mm:ss.s".</summary>
    private static double? Time(JsonElement v, string name)
    {
        if (!v.TryGetProperty(name, out var t) || t.ValueKind == JsonValueKind.Null) return null;
        if (t.ValueKind == JsonValueKind.Number) return t.GetDouble();
        double seconds = 0;
        foreach (var part in t.GetString().Split(':'))
            seconds = seconds * 60 + double.Parse(part, NumberStyles.Float, CultureInfo.InvariantCulture);
        return seconds;
    }

    /// <summary>
    /// <paramref name="track"/> as an iMUS sound the game's music player plays like its own: the stretch from
    /// start to end, a region up to the loop, the loop and the tail, a jump from the loop's end to its start
    /// (unless it doesn't loop) and the "_end" marker there.
    /// </summary>
    /// <paramref name="gameLoops"/> is whether the game's own track loops, for a track whose loop isn't given.
    public static ImusSound Build(Track track, bool gameLoops = true)
    {
        var flac = Flac.Open(track.File);
        int rate = flac.SampleRate;
        long total = flac.TotalFrames;
        long Frame(double seconds) => Math.Clamp((long)Math.Round(seconds * rate), 0, total);
        long first = Frame(track.Start);
        long last = track.End is { } e ? Frame(e) : total;
        if (last <= first) throw new InvalidDataException($"{Path.GetFileName(track.File)}: end is before start");
        long length = last - first;
        long loopStart = Math.Clamp(track.LoopStart is { } ls ? Frame(ls) - first : 0, 0, length - 1);
        long loopEnd = Math.Clamp(track.LoopEnd is { } le ? Frame(le) - first : length, loopStart + 1, length);

        var regions = new List<ImusSound.Region>();
        if (loopStart > 0) regions.Add(new(0, loopStart));
        regions.Add(new(loopStart, loopEnd - loopStart));
        if (loopEnd < length) regions.Add(new(loopEnd, length - loopEnd));
        var jumps = track.Loop ?? gameLoops ? new[] { new ImusSound.Jump(loopEnd, loopStart, 0, track.FadeMs) } : [];
        var markers = new[] { new ImusSound.Marker(0, Path.GetFileNameWithoutExtension(track.File)), new ImusSound.Marker(loopEnd, "_end") };
        // The game's music is 16-bit; a 24-bit soundtrack is shortened to it.
        if (flac.Channels > 2) throw new InvalidDataException($"{Path.GetFileName(track.File)}: {flac.Channels} channels (mono or stereo only)");
        return ImusSound.Build(rate, flac.Channels, length, regions, jumps, markers, length,
            (bytes, at) => flac.Decode16(first, length, bytes, at), track.File);
    }
}
