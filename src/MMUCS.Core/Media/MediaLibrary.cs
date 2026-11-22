using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MMUCS.Core.Audio;
using MMUCS.Core.Diagnostics;
using MMUCS.Core.Smush;

namespace MMUCS.Core.Media;

public enum MediaKind { Cutscene, Music, Voice }

/// <summary>
/// One playable thing: a cutscene, a music track or a speech line, by its asset ID
/// (<c>video/opening</c>, <c>music/1098</c>, <c>voice/bccb301</c>).
/// </summary>
public sealed record MediaItem(MediaKind Kind, string Id, string Title, TimeSpan Duration, string File, int Entry)
{
    /// <summary>What a list shows: the ID's last part, then the title when it adds something.</summary>
    public string Label
    {
        get
        {
            string key = Id[(Id.IndexOf('/') + 1)..];
            return string.IsNullOrEmpty(Title) || Title == key ? key : $"{key}  {Title}";
        }
    }

    public string DurationText => Duration.TotalHours >= 1 ? Duration.ToString(@"h\:mm\:ss") : Duration.ToString(@"m\:ss");
}

/// <summary>
/// Everything playable in a game's loose files: the .san cutscenes and the sounds in the .bun bundles
/// (sounds on both discs listed once). Scanning reads each cutscene's header and each sound's first
/// block only, so it takes well under a second.
/// </summary>
public static class MediaLibrary
{
    public static IReadOnlyList<MediaItem> Scan(string gameFile, CancellationToken token = default)
    {
        var items = new ConcurrentBag<MediaItem>();

        foreach (var san in SmushVideo.FindAll(gameFile))
        {
            try
            {
                var h = SmushVideo.ReadHeader(san);
                string id = "video/" + Path.GetFileNameWithoutExtension(san).ToLowerInvariant();
                // The curated title, else where the game starts it (disc and room).
                string title = CutsceneOrder.TryGet(id, out _, out var known)
                    ? (!string.IsNullOrWhiteSpace(known.Title) ? known.Title : $"disc {known.Disc} · {known.Room}")
                    : null;
                items.Add(new MediaItem(MediaKind.Cutscene, id, title,
                    TimeSpan.FromSeconds(h.FrameCount / (double)h.FrameRate), san, -1));
            }
            catch (Exception ex) { Log.Warn($"Media library: {Path.GetFileName(san)}: {ex.Message}"); }
        }

        var seen = new ConcurrentDictionary<string, bool>();
        foreach (var bun in GameFiles.Find(gameFile, ".bun"))
        {
            token.ThrowIfCancellationRequested();
            string kind = AudioExporter.KindOf(bun);
            var mediaKind = kind == "music" ? MediaKind.Music : MediaKind.Voice;
            using var bundle = Bundle.Open(bun);
            Parallel.ForEach(bundle.Entries, new ParallelOptions { CancellationToken = token }, e =>
            {
                string id = AudioExporter.IdOf(kind, e.Name);
                if (!seen.TryAdd(id, true)) return;
                try
                {
                    var stored = bundle.Read(e);
                    if (!stored.AsSpan(0, 4).SequenceEqual("COMP"u8) && !stored.AsSpan(0, 4).SequenceEqual("iMUS"u8))
                    {
                        seen.TryRemove(id, out _);
                        return;
                    }
                    var head = ImusSound.Parse(BundleCodec.DecompressFirstBlock(stored, out long full));
                    long frames = Math.Max(0, full - head.DataOffset) / head.BytesPerFrame;
                    string title = head.Title;
                    // Music titles start with their number ("1098-Credits1"); the number is shown anyway.
                    int dash = title?.IndexOf('-') ?? -1;
                    if (mediaKind == MediaKind.Music && dash > 0 && title[..dash].All(char.IsAsciiDigit)) title = title[(dash + 1)..];
                    items.Add(new MediaItem(mediaKind, id, title, TimeSpan.FromSeconds((double)frames / head.SampleRate), bun, e.Index));
                }
                catch (Exception ex) { Log.Warn($"Media library: {Path.GetFileName(bun)} {e.Name}: {ex.Message}"); }
            });
        }

        // Cutscenes in the order they play (Names/cutscenes.json), any it doesn't list after them; sounds by ID.
        return items
            .OrderBy(i => i.Kind)
            .ThenBy(i => i.Kind == MediaKind.Cutscene && CutsceneOrder.TryGet(i.Id, out int order, out _) ? order : int.MaxValue)
            .ThenBy(i => i.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Loads a music track or speech line in full.</summary>
    public static ImusSound LoadSound(MediaItem item)
    {
        if (item.Kind == MediaKind.Cutscene) throw new ArgumentException("a cutscene is not a bundle sound");
        using var bundle = Bundle.Open(item.File);
        return ImusSound.Parse(BundleCodec.Decompress(bundle.Read(bundle.Entries[item.Entry])));
    }

    public static SmushVideo LoadVideo(MediaItem item) =>
        item.Kind == MediaKind.Cutscene ? SmushVideo.Load(item.File) : throw new ArgumentException("not a cutscene");
}
