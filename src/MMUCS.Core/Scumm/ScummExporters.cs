using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MMUCS.Core.Diagnostics;
using MMUCS.Core.Imaging;

namespace MMUCS.Core.Scumm;

/// <summary>What a batch export should produce.</summary>
public sealed class ExportOptions
{
    public bool Backgrounds { get; set; } = true;
    public bool Objects { get; set; } = true;
    public bool Costumes { get; set; } = true;

    /// <summary>Write one background per room palette instead of only the default palette.</summary>
    public bool AllBackgroundPalettes { get; set; }

    /// <summary>Shading options applied to costume cels (same as the AKOS viewer's shader).</summary>
    public CelExportConfig CelConfig { get; set; } = new();
}

public readonly record struct ExportProgress(int Done, int Total, string Current);

public sealed record ExportSummary(int FilesWritten, int Failures, TimeSpan Elapsed, bool Cancelled, string OutputFolder);

/// <summary>
/// Batch PNG export of a loaded project. Layout:
/// <code>
/// out/rooms/009_cannon/background.png            (or background_pal1.png … with AllBackgroundPalettes)
/// out/rooms/009_cannon/objects/003_cannon-grate_f0.png
/// out/rooms/009_cannon/costumes/akos_00BE5A10/cel_000.png
/// </code>
/// Rooms that appear in more than one file (e.g. per-disc copies) get the file name appended.
/// Individual failures are logged and counted; they don't stop the export.
/// </summary>
public class ScummExporters
{
    private sealed record RoomJob(RoomModel Room, string Folder, List<ScummBlock> Costumes);

    public async Task<ExportSummary> ExportProjectAsync(
        ScummBlock root,
        string outputFolder,
        ExportOptions options,
        IProgress<ExportProgress> progress = null,
        CancellationToken token = default)
    {
        var sw = Stopwatch.StartNew();
        int written = 0, failures = 0, done = 0;

        var jobs = PlanRooms(root, outputFolder);
        int total = jobs.Sum(j =>
            (options.Backgrounds ? 1 : 0)
            + (options.Objects ? j.Room.Objects.Count : 0)
            + (options.Costumes ? j.Costumes.Count : 0));

        void Report(string current) => progress?.Report(new ExportProgress(done, total, current));

        try
        {
            foreach (var job in jobs)
            {
                token.ThrowIfCancellationRequested();
                string roomLabel = Path.GetFileName(job.Folder);

                if (options.Backgrounds)
                {
                    Report($"{roomLabel}: background");
                    var (w, f) = await ExportBackgroundsAsync(job, options.AllBackgroundPalettes, token);
                    written += w; failures += f; done++;
                }

                if (options.Objects)
                {
                    foreach (var obj in job.Room.Objects)
                    {
                        token.ThrowIfCancellationRequested();
                        Report($"{roomLabel}: object {obj}");
                        var (w, f) = await ExportObjectAsync(obj, Path.Combine(job.Folder, "objects"), token);
                        written += w; failures += f; done++;
                    }
                }

                if (options.Costumes)
                {
                    foreach (var akosBlock in job.Costumes)
                    {
                        token.ThrowIfCancellationRequested();
                        Report($"{roomLabel}: costume @0x{akosBlock.Offset:X8}");
                        var (w, f) = await ExportCostumeAsync(akosBlock, Path.Combine(job.Folder, "costumes"), options.CelConfig, token);
                        written += w; failures += f; done++;
                    }
                }
            }
            Report("Done");
            return new ExportSummary(written, failures, sw.Elapsed, false, outputFolder);
        }
        catch (OperationCanceledException)
        {
            return new ExportSummary(written, failures, sw.Elapsed, true, outputFolder);
        }
    }

    /// <summary>Counts what an export would cover, for the wizard's summary.</summary>
    public static (int Rooms, int Objects, int Costumes) Survey(ScummBlock root)
    {
        int rooms = 0, objects = 0, costumes = 0;
        foreach (var lflf in root.FindChildrenRecursive(ScummTag.LFLF))
        {
            if (!RoomModel.TryCreate(lflf, out var room)) continue;
            rooms++;
            objects += room.Objects.Count;
            costumes += lflf.Children.Count(c => c.Tag == ScummTag.AKOS);
        }
        return (rooms, objects, costumes);
    }

    private static List<RoomJob> PlanRooms(ScummBlock root, string outputFolder)
    {
        var jobs = new List<RoomJob>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var lflf in root.FindChildrenRecursive(ScummTag.LFLF))
        {
            if (!RoomModel.TryCreate(lflf, out var room)) continue;

            string baseName = room.RoomNo > 0
                ? $"{room.RoomNo:D3}{(string.IsNullOrEmpty(room.Name) ? "" : "_" + Sanitize(room.Name))}"
                : $"room_{room.Room.Offset:X8}";
            string name = baseName;
            if (!usedNames.Add(name))
            {
                string file = Sanitize(FileRootName(lflf));
                name = $"{baseName}_{file}";
                for (int n = 2; !usedNames.Add(name); n++) name = $"{baseName}_{file}_{n}";
            }

            var costumes = lflf.Children.Where(c => c.Tag == ScummTag.AKOS).ToList();
            jobs.Add(new RoomJob(room, Path.Combine(outputFolder, "rooms", name), costumes));
        }
        return jobs;
    }

    private static async Task<(int written, int failed)> ExportBackgroundsAsync(RoomJob job, bool allPalettes, CancellationToken token)
    {
        var room = job.Room;
        // Some LFLFs (e.g. inventory/object libraries) have a 0-width RMHD and no background at all.
        if (room.Width <= 0 || room.Height <= 0) return (0, 0);
        int written = 0, failed = 0;
        int count = allPalettes ? room.PaletteCount : 1;

        for (int pal = 0; pal < count; pal++)
        {
            try
            {
                // cacheDisabled: avoid keeping every room's full-colour image in the RAM cache.
                var bg = await ScummBackgroundCache.GetCachedBackgroundAsync(
                    room.RoomNo, room.Rmhd, room.Room, cacheDisabled: true, pal, token);
                if (bg.ExecutedImage == null) { failed++; continue; }

                string file = allPalettes && room.PaletteCount > 1 ? $"background_pal{pal}.png" : "background.png";
                bg.ExecutedImage.SavePng(Path.Combine(job.Folder, file));
                written++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                failed++;
                Log.Warn($"Background export failed for {job.Folder}: {ex.Message}");
            }
        }
        return (written, failed);
    }

    private static async Task<(int written, int failed)> ExportObjectAsync(RoomObject obj, string folder, CancellationToken token)
    {
        if (obj.ImageCount <= 0 || !ObimDecoders.TryGetObimInfo(obj.Block, out var info)) return (0, 0);

        // BOMP objects have one block per frame; SMAP objects decode a single image.
        int frames = info.TotalFrames > 0 ? info.TotalFrames : 1;
        int written = 0, failed = 0;
        string stem = $"{obj.Index:D3}_{Sanitize(obj.ToString())}";

        for (int f = 0; f < frames; f++)
        {
            try
            {
                var result = await ObimCache.GetCachedObimAsync(obj.Block, f, info.DefaultPaletteIndex, bakeImage: true, token);
                if (result.ExecutedImage == null) { failed++; continue; }
                result.ExecutedImage.SavePng(Path.Combine(folder, $"{stem}_f{f}.png"));
                written++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                failed++;
                Log.Warn($"Object export failed for {stem} frame {f}: {ex.Message}");
            }
        }
        return (written, failed);
    }

    private static async Task<(int written, int failed)> ExportCostumeAsync(ScummBlock akosBlock, string folder, CelExportConfig config, CancellationToken token)
    {
        AkosData akos;
        try
        {
            akos = await Task.Run(() => new AkosParser().Parse(akosBlock), token);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"Costume parse failed @0x{akosBlock.Offset:X8}: {ex.Message}");
            return (0, 1);
        }

        return await ExportCelsAsync(akos, config, Path.Combine(folder, $"akos_{akosBlock.Offset:X8}"), token);
    }

    /// <summary>Exports every cel of one costume with the given shading options.</summary>
    public static async Task<(int written, int failed)> ExportCelsAsync(AkosData akos, CelExportConfig config, string targetDir, CancellationToken token = default)
    {
        int written = 0, failed = 0;
        for (int i = 0; i < akos.CelOffsets.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var cel = await AkosCelCache.GetCachedCelAsync(akos, i, bakeImages: true, cacheDisabled: true, token);
                var image = IndexedRenderer.RenderWithConfig(cel.ShaderDataImage, akos.ResolvedColors, config);
                if (image == null) continue; // empty (0×0) cels are normal
                image.SavePng(Path.Combine(targetDir, $"cel_{i:D3}.png"));
                written++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                failed++;
                Log.Warn($"Cel {i} export failed @0x{akos.Offset:X8}: {ex.Message}");
            }
        }
        return (written, failed);
    }

    /// <summary>Single-costume export used by File ▸ Export costume cels.</summary>
    public async Task ExportAkosCelsAsync(AkosData akos, CelExportConfig userConfig, string targetDir) =>
        await ExportCelsAsync(akos, userConfig, targetDir);

    /// <summary>Name of the file root (e.g. COMI.LA2) that contains <paramref name="block"/>.</summary>
    private static string FileRootName(ScummBlock block)
    {
        var b = block;
        while (b.Parent != null && b.Parent.FileData != null) b = b.Parent;
        return b.TagName ?? "file";
    }

    private static string Sanitize(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "unnamed";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(s.Length);
        foreach (char c in s.Trim()) sb.Append(Array.IndexOf(invalid, c) >= 0 || c == ' ' ? '_' : c);
        return sb.ToString();
    }
}
