using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MMUCS.Core.Diagnostics;

namespace MMUCS.Core.Audio;

/// <summary>What exporting one bundle produced, plus decoder checks.</summary>
public sealed record AudioExportSummary(string Bundle, int Entries, int Written, int Duplicates, int Skipped, int Failures,
    long PcmBytes, TimeSpan Elapsed)
{
    /// <summary>Sounds that were on an earlier disc too but differ from it; written with the bundle name added.</summary>
    public int Conflicts { get; init; }
    /// <summary>The most bytes any block's bit reader wanted past its input (0 or 1 is normal).</summary>
    public int MaxOverRead { get; init; }
    /// <summary>Sounds whose map points outside their data, or that use map events the parser doesn't know.</summary>
    public int MapProblems { get; init; }
}

/// <summary>
/// Unpacks bundles into editable files, following the asset IDs in docs/bundle-format.md:
/// <code>
/// out/music/1098.wav        musdisk*.bun: the number the file name starts with ("1098-C~1.IMX")
/// out/music/1098.meta.json  format, title, regions, jumps, markers and stop, in sample frames
/// out/voice/bccb301.wav     voxdisk*.bun: the line's code, also the /BCCB301/ code of its subtitle
/// out/voice/bccb301.meta.json  as above, with the lip sync (mouth width and height over time)
/// </code>
/// Sounds on both discs are written once (compared by content). musdisk's PRELOAD is skipped: it only
/// repeats the start of each track.
/// </summary>
public static class AudioExporter
{
    /// <summary>The asset namespace a bundle's sounds go in.</summary>
    public static string KindOf(string bundlePath)
    {
        string n = Path.GetFileName(bundlePath).ToLowerInvariant();
        return n.StartsWith("mus") ? "music" : n.StartsWith("vox") ? "voice" : "sound";
    }

    /// <summary>The asset ID of a bundle entry: music by its number, everything else by its name.</summary>
    public static string IdOf(string kind, string entryName)
    {
        string stem = Path.GetFileNameWithoutExtension(entryName);
        if (kind == "music")
        {
            int digits = 0;
            while (digits < stem.Length && char.IsAsciiDigit(stem[digits])) digits++;
            if (digits > 0 && int.TryParse(stem.AsSpan(0, digits), out int number)) return $"music/{number:D4}";
        }
        var clean = new string([.. stem.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_')]);
        return $"{kind}/{clean}";
    }

    /// <param name="seen">IDs already written by earlier bundles (and their content hash), for skipping per-disc copies.</param>
    /// <param name="write">False decodes and checks everything without writing files.</param>
    public static async Task<AudioExportSummary> ExportBundleAsync(string bundlePath, string outputFolder,
        ConcurrentDictionary<string, string> seen, bool write = true, Action<int> entryDone = null,
        CancellationToken token = default)
    {
        var sw = Stopwatch.StartNew();
        using var bundle = Bundle.Open(bundlePath);
        string kind = KindOf(bundlePath);
        string source = Path.GetFileName(bundlePath);
        string bundleTag = Path.GetFileNameWithoutExtension(bundlePath).ToLowerInvariant();

        int written = 0, duplicates = 0, skipped = 0, failures = 0, conflicts = 0, maxOverRead = 0, mapProblems = 0, done = 0;
        long pcm = 0;

        var options = new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) };
        await Parallel.ForEachAsync(bundle.Entries, options, async (entry, ct) =>
        {
            try
            {
                var stored = bundle.Read(entry);
                if (stored.Length < 4 || !(stored.AsSpan(0, 4).SequenceEqual("COMP"u8) || stored.AsSpan(0, 4).SequenceEqual("iMUS"u8)))
                {
                    Interlocked.Increment(ref skipped);
                    return;
                }

                string id = IdOf(kind, entry.Name);
                string hash = Convert.ToHexString(SHA256.HashData(stored));
                string previous = seen.GetOrAdd(id, hash);
                if (!ReferenceEquals(previous, hash))
                {
                    if (previous == hash) { Interlocked.Increment(ref duplicates); return; }
                    Interlocked.Increment(ref conflicts);
                    Log.Warn($"{source}: {entry.Name} differs from the copy on another disc; written as {id}-{bundleTag}");
                    id = $"{id}-{bundleTag}";
                }

                var stream = BundleCodec.Decompress(stored, out var stats);
                InterlockedMax(ref maxOverRead, stats.OverReads);
                var sound = ImusSound.Parse(stream);

                string problem = Check(sound);
                if (problem != null)
                {
                    Interlocked.Increment(ref mapProblems);
                    Log.Warn($"{source}: {entry.Name}: {problem}");
                }
                Interlocked.Add(ref pcm, sound.DataLength);

                if (write)
                {
                    string wav = Path.Combine(outputFolder, id + ".wav");
                    Directory.CreateDirectory(Path.GetDirectoryName(wav)!);
                    await using (var fs = File.Create(wav))
                        WavWriter.Write(fs, sound.Samples, sound.Channels, sound.SampleRate, sound.Bits);
                    WriteMeta(Path.Combine(outputFolder, id + ".meta.json"), id, source, entry.Name, sound);
                }
                Interlocked.Increment(ref written);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Interlocked.Increment(ref failures);
                Log.Warn($"{source}: {entry.Name} failed: {ex.Message}");
            }
            finally
            {
                entryDone?.Invoke(Interlocked.Increment(ref done));
            }
        });

        return new AudioExportSummary(source, bundle.Entries.Count, written, duplicates, skipped, failures, pcm, sw.Elapsed)
        {
            Conflicts = conflicts,
            MaxOverRead = maxOverRead,
            MapProblems = mapProblems,
        };
    }

    /// <summary>Whether the map makes sense against the data: null when it does.</summary>
    private static string Check(ImusSound s)
    {
        long start = s.DataOffset, end = (long)s.DataOffset + s.DataLength;
        if (s.Bits is not (8 or 16)) return $"{s.Bits}-bit samples";
        if (s.Channels is not (1 or 2)) return $"{s.Channels} channels";
        if (s.UnknownEvents.Count > 0) return "unknown map events " + string.Join(", ", s.UnknownEvents.Distinct());
        // Speech regions often run one byte past the data (an odd length of 16-bit samples): an authoring quirk.
        foreach (var r in s.Regions)
            if (r.Offset < start || r.Offset + r.Length > end + 1) return $"region {r.Offset}+{r.Length} outside the data ({start}–{end})";
        foreach (var j in s.Jumps)
            if (j.Offset < start || j.Offset > end || j.Destination < start || j.Destination > end) return $"jump {j.Offset}→{j.Destination} outside the data";
        if (s.Stop is long stop && (stop < start || stop > end + 8)) return $"stop {stop} outside the data ({start}–{end})";
        return null;
    }

    private static void WriteMeta(string path, string id, string source, string entry, ImusSound s)
    {
        using var fs = File.Create(path);
        using var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true });
        long Frame(long offset) => s.ToFrame(offset);
        long Frames(long bytes) => bytes / s.BytesPerFrame;

        w.WriteStartObject();
        w.WriteString("id", id);
        w.WriteString("source", source);
        w.WriteString("entry", entry);
        if (s.Title is { } title) w.WriteString("name", title);
        w.WriteString("codec", "pcm");
        w.WriteNumber("sampleRate", s.SampleRate);
        w.WriteNumber("channels", s.Channels);
        w.WriteNumber("bits", s.Bits);
        w.WriteNumber("frames", s.FrameCount);
        w.WriteString("positions", "sample frames from the start of the audio");

        w.WriteStartArray("regions");
        foreach (var r in s.Regions)
        {
            w.WriteStartObject();
            w.WriteNumber("start", Frame(r.Offset));
            w.WriteNumber("length", Frames(r.Length));
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("jumps");
        foreach (var j in s.Jumps)
        {
            w.WriteStartObject();
            w.WriteNumber("at", Frame(j.Offset));
            w.WriteNumber("to", Frame(j.Destination));
            w.WriteNumber("hook", j.Hook);
            w.WriteNumber("fadeMs", j.FadeMs);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("markers");
        foreach (var m in s.Markers)
        {
            w.WriteStartObject();
            w.WriteNumber("at", Frame(m.Offset));
            w.WriteString("text", m.Text);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        if (s.Syncs.Count > 0)
        {
            // Lip sync: the mouth's width and height (0–127) from each time on, in ms (60 Hz ticks of 16 ms in
            // the file), as ImusSound.LipSync reads them.
            w.WriteStartArray("syncs");
            foreach (var y in s.Syncs)
            {
                w.WriteStartObject();
                w.WriteNumber("at", Frame(y.Offset));
                w.WritePropertyName("mouth");
                w.WriteRawValue("[" + string.Join(",", Array.ConvertAll(y.Data,
                    d => $"{{\"ms\":{(d >> 16) * 16},\"width\":{(d >> 8) & 0x7F},\"height\":{d & 0x7F}}}")) + "]",
                    skipInputValidation: true);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }

        if (s.Stop is long stop) w.WriteNumber("stop", Frame(stop));
        w.WriteEndObject();
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)) &&
               Interlocked.CompareExchange(ref target, value, current) != current) { }
    }
}

/// <summary>Writes PCM as a RIFF WAVE file.</summary>
public static class WavWriter
{
    public static void Write(Stream output, ReadOnlySpan<byte> pcm, int channels, int sampleRate, int bits)
    {
        int blockAlign = channels * bits / 8;
        using var w = new BinaryWriter(output, System.Text.Encoding.ASCII, leaveOpen: true);
        w.Write("RIFF"u8);
        w.Write(36 + pcm.Length);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)channels);
        w.Write(sampleRate);
        w.Write(sampleRate * blockAlign);
        w.Write((short)blockAlign);
        w.Write((short)bits);
        w.Write("data"u8);
        w.Write(pcm.Length);
        w.Write(pcm);
        if ((pcm.Length & 1) != 0) w.Write((byte)0);
    }
}
