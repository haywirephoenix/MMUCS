using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MMUCS.Core.Imaging;
using MMUCS.Core.Scumm;
using SkiaSharp;

/// <summary>
/// <c>mprobe backgrounds DIR [--scale N] [--out MOD]</c>: finds the room each image in DIR (PNG or JPEG, a
/// repainted or upscaled background) shows. Each image, shrunk by the scale (default 4) to the game's size, is
/// compared with every room's background at every place it fits, by the correlation of their brightness in
/// 8-pixel cells, then lined up to the pixel in the best room. An image smaller than its room (half of a wide one)
/// is placed where it matches. Lists each image's room, place and score with the runner-up, then the rooms
/// several images landed in and the rooms none did. Images that match no background well, or repeat a better one,
/// are matched against the object images. <c>--out MOD</c> makes MOD a hi-res mod (mod.json's "scale"): each
/// room's images, at their places and the scale, go to MOD/rooms/&lt;room&gt;/background.png, an image that is a
/// whole object's to MOD/rooms/&lt;room&gt;/objects/, and the rest to MOD/_unplaced.
/// </summary>
internal static class BackgroundsCommand
{
    private const int Cell = 8;

    private sealed record Room(string Folder, RoomModel Model, int Width, int Height, float[] Grey, float[] Cells);

    /// <summary>An object image's frame in grey, with which pixels are opaque.</summary>
    private sealed record Frame(Room Room, RoomObject Object, int Index, int Width, int Height, float[] Grey, bool[] Opaque);

    /// <summary>Below this an image isn't taken as its room's background.</summary>
    private const double Sure = 0.95;

    private sealed record Match(string File, int Width, int Height, float[] Grey, Room Room, int X, int Y, double Score, Room Second, double SecondScore);

    public static int Run(ScummBlock root, List<string> a, Func<List<string>, string, string> takeOption)
    {
        string scaleText = takeOption(a, "--scale");
        int scale = scaleText == null ? 4 : int.TryParse(scaleText, out int s) && s > 0 ? s : throw new UsageException($"--scale {scaleText}: a whole number");
        string outDir = takeOption(a, "--out");
        var positional = a.Where(x => !x.StartsWith("--")).ToList();
        string dir = positional.Count > 1 ? positional[1] : throw new UsageException("backgrounds needs DIR");
        if (!Directory.Exists(dir)) throw new UsageException($"{dir} isn't a folder");

        var all = LoadRooms(root);
        var rooms = all.Where(r => r.Grey != null).ToList();
        Console.WriteLine($"{rooms.Count} rooms with a background, {all.Count - rooms.Count} without");

        var files = Directory.EnumerateFiles(dir)
            .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg")
            .OrderBy(f => LeadingNumber(Path.GetFileName(f))).ThenBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        var matches = new List<Match>();
        foreach (var file in files)
        {
            using var bitmap = Decode(file);
            if (bitmap == null)
            {
                Console.WriteLine($"{Path.GetFileName(file)}: can't be read");
                continue;
            }
            int w = bitmap.Width / scale, h = bitmap.Height / scale;
            var grey = Shrink(Grey(bitmap), bitmap.Width, bitmap.Height, scale, w, h);
            var cells = Shrink(grey, w, h, Cell, w / Cell, h / Cell);

            // Every room, at every cell it fits: the best place in each, then the best two rooms.
            var scored = rooms.Select(r => (Room: r, Best: BestPlace(cells, w / Cell, h / Cell, r.Cells, r.Width / Cell, r.Height / Cell, 0, 0, int.MaxValue, 1)))
                .OrderByDescending(t => t.Best.Score).ToList();
            var (room, coarse) = scored[0];
            // To the pixel, within a cell of the coarse place.
            var fine = BestPlace(grey, w, h, room.Grey, room.Width, room.Height, coarse.X * Cell, coarse.Y * Cell, Cell, 1);
            var m = new Match(file, w, h, grey, room, fine.X, fine.Y, fine.Score, scored.Count > 1 ? scored[1].Room : null, scored.Count > 1 ? scored[1].Best.Score : 0);
            matches.Add(m);
            string place = w == room.Width && h == room.Height ? "" : $" at {m.X},{m.Y} ({w}×{h} of {room.Width}×{room.Height})";
            string size = w > room.Width || h > room.Height ? $" — image is {w}×{h}, room {room.Width}×{room.Height}" : "";
            Console.WriteLine($"{Path.GetFileName(file),-22} {room.Folder,-20} {m.Score:0.000}{place}{size}   next {m.Second?.Folder} {m.SecondScore:0.000}");
        }

        // A room's images: the best covers it, others that land elsewhere in it fill the rest, the ones overlapping a
        // better one are left over.
        var placed = new Dictionary<Room, List<Match>>();
        var unplaced = new List<Match>();
        foreach (var group in matches.GroupBy(m => m.Room))
        {
            var kept = new List<Match>();
            foreach (var m in group.OrderByDescending(m => m.Score))
                if (m.Score < Sure || kept.Any(k => Overlap(k, m) > 0.25)) unplaced.Add(m);
                else kept.Add(m);
            if (kept.Count > 0) placed[group.Key] = kept;
        }
        Console.WriteLine();
        foreach (var (room, kept) in placed.Where(p => p.Value.Count > 1).OrderBy(p => p.Key.Folder))
            Console.WriteLine($"{room.Folder}: {string.Join(" + ", kept.Select(k => $"{Path.GetFileName(k.File)} at {k.X},{k.Y}"))}, {Coverage(room, kept):0%} covered");
        var frames = new Dictionary<Room, List<Frame>>();
        var objectImages = new List<(Match Match, Frame Frame)>();
        foreach (var m in unplaced.OrderBy(m => LeadingNumber(Path.GetFileName(m.File))))
        {
            string over = placed.TryGetValue(m.Room, out var k) && k.FirstOrDefault(k => Overlap(k, m) > 0.25) is { } better
                ? $", overlaps {Path.GetFileName(better.File)}" : $", under {Sure}";
            Console.WriteLine($"left over: {Path.GetFileName(m.File)} ({m.Room.Folder} {m.Score:0.000} at {m.X},{m.Y}{over})");
            var o = Objects(m, all, frames);
            // The whole image is the object's: written where the export writes the object's, drawn at the mod's scale.
            if (o.Frame is { } f && o.Score >= Sure && o.X == 0 && o.Y == 0 && Math.Abs(f.Width - m.Width) <= 1 && Math.Abs(f.Height - m.Height) <= 1)
                objectImages.Add((m, f));
        }
        var none = rooms.Where(r => !placed.ContainsKey(r)).Select(r => r.Folder).ToList();
        Console.WriteLine($"rooms with no image ({none.Count}): {string.Join(", ", none)}");

        if (outDir == null) return 0;
        SetScale(outDir, scale);
        foreach (var (room, kept) in placed)
        {
            string folder = Path.Combine(outDir, "rooms", room.Folder);
            Directory.CreateDirectory(folder);
            var rgba = new byte[room.Width * scale * room.Height * scale * 4];
            foreach (var k in kept)
            {
                using var bitmap = Decode(k.File);
                Blit(bitmap, rgba, room.Width * scale, room.Height * scale, k.X * scale, k.Y * scale);
            }
            using (var stream = File.Create(Path.Combine(folder, "background.png")))
                PngWriter.Write(stream, room.Width * scale, room.Height * scale, rgba);
            Console.WriteLine($"wrote {Path.Combine("rooms", room.Folder, "background.png")} from {string.Join(", ", kept.Select(k => Path.GetFileName(k.File)))}");
        }
        foreach (var (m, f) in objectImages)
        {
            string file = Path.Combine("rooms", f.Room.Folder, "objects", ScummExporters.ObjectFrameFile(f.Object, f.Index));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(outDir, file)));
            using var bitmap = Decode(m.File);
            using (var stream = File.Create(Path.Combine(outDir, file)))
                PngWriter.Write(stream, bitmap.Width, bitmap.Height, bitmap.GetPixelSpan());
            Console.WriteLine($"wrote {file} from {Path.GetFileName(m.File)}");
        }
        var rest = unplaced.Where(m => objectImages.All(o => o.Match != m)).ToList();
        if (rest.Count > 0)
        {
            string folder = Path.Combine(outDir, "_unplaced");
            Directory.CreateDirectory(folder);
            foreach (var m in rest) File.Copy(m.File, Path.Combine(folder, Path.GetFileName(m.File)), true);
            Console.WriteLine($"copied {rest.Count} images matching no room or object to _unplaced");
        }
        return 0;
    }

    /// <summary>Gives MOD/mod.json the scale, so the game draws its images as a hi-res mod's, keeping what else it says.</summary>
    private static void SetScale(string outDir, int scale)
    {
        Directory.CreateDirectory(outDir);
        string file = Path.Combine(outDir, "mod.json");
        System.Text.Json.Nodes.JsonObject json = null;
        if (File.Exists(file))
            try { json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file)) as System.Text.Json.Nodes.JsonObject; }
            catch (System.Text.Json.JsonException) { }
        json ??= new() { ["enabled"] = true };
        json["scale"] = scale;
        File.WriteAllText(file, json.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
        Console.WriteLine($"mod.json: scale {scale}");
    }

    private static List<Room> LoadRooms(ScummBlock root)
    {
        var rooms = new List<Room>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lflf in root.FindChildrenRecursive(ScummTag.LFLF))
        {
            if (!RoomModel.TryCreate(lflf, out var room)) continue;
            string folder = ScummExporters.RoomFolderName(room);
            if (!seen.Add(folder)) continue;
            // A room without a background (the icons') is kept for its objects.
            if (room.Width <= 0 || room.Height <= 0)
            {
                rooms.Add(new Room(folder, room, 0, 0, null, null));
                continue;
            }
            var bg = ScummBackgroundCache.GetCachedBackgroundAsync(room.RoomNo, room.Rmhd, room.Room, cacheDisabled: true, 0, CancellationToken.None).GetAwaiter().GetResult();
            if (bg.ExecutedImage is not { } image) continue;
            var grey = new float[image.Width * image.Height];
            for (int i = 0; i < grey.Length; i++)
                grey[i] = 0.299f * image.Data[i * 4] + 0.587f * image.Data[i * 4 + 1] + 0.114f * image.Data[i * 4 + 2];
            rooms.Add(new Room(folder, room, image.Width, image.Height, grey, Shrink(grey, image.Width, image.Height, Cell, image.Width / Cell, image.Height / Cell)));
        }
        return rooms;
    }

    /// <summary>
    /// What a left-over image shows of the game's object images: the objects of its room, at their places, it
    /// matches better than the background there (a background with those objects' states drawn in), and the object
    /// frame of any room, at least a quarter of its size, that it best matches anywhere (an object's image on its own).
    /// </summary>
    private static (Frame Frame, int X, int Y, double Score, bool Inside) Objects(Match m, List<Room> rooms, Dictionary<Room, List<Frame>> cache)
    {
        foreach (var f in FramesOf(m.Room, cache))
        {
            int x = f.Object.X - m.X, y = f.Object.Y - m.Y;
            if (x < 0 || y < 0 || x + f.Width > m.Width || y + f.Height > m.Height || f.Width * f.Height < 256) continue;
            double withObject = Masked(m.Grey, m.Width, f.Grey, f.Opaque, f.Width, f.Height, x, y);
            var under = new float[f.Width * f.Height];
            for (int r = 0; r < f.Height; r++)
                Array.Copy(m.Room.Grey, (f.Object.Y + r) * m.Room.Width + f.Object.X, under, r * f.Width, f.Width);
            double withBackground = Masked(m.Grey, m.Width, under, f.Opaque, f.Width, f.Height, x, y);
            if (withObject > 0.8 && withObject > withBackground + 0.05)
                Console.WriteLine($"    shows {ScummExporters.ObjectFrameFile(f.Object, f.Index)} at {f.Object.X},{f.Object.Y} ({f.Width}×{f.Height}): {withObject:0.000}, the background there {withBackground:0.000}");
        }

        var best = (Frame: (Frame)null, X: 0, Y: 0, Score: double.NegativeInfinity, Inside: false);
        foreach (var room in rooms)
            foreach (var f in FramesOf(room, cache))
            {
                if (f.Width * f.Height * 4 < m.Width * m.Height) continue;
                // The smaller of the two slid over the larger, in cells, then to the pixel.
                bool inside = f.Width <= m.Width && f.Height <= m.Height;
                if (!inside && (f.Width < m.Width || f.Height < m.Height)) continue;
                var (big, bw, bh, small, sw, sh, mask) = inside
                    ? (m.Grey, m.Width, m.Height, f.Grey, f.Width, f.Height, f.Opaque)
                    : (f.Grey, f.Width, f.Height, m.Grey, m.Width, m.Height, null);
                var (cx, cy, _) = SlideMasked(Shrink(big, bw, bh, Cell, bw / Cell, bh / Cell), bw / Cell, bh / Cell,
                    Shrink(small, sw, sh, Cell, sw / Cell, sh / Cell), mask == null ? null : ShrinkMask(mask, sw, sh), sw / Cell, sh / Cell, 0, 0, int.MaxValue);
                var (x, y, score) = SlideMasked(big, bw, bh, small, mask, sw, sh, cx * Cell, cy * Cell, Cell);
                if (score > best.Score) best = (f, x, y, score, inside);
            }
        if (best.Frame is { } b)
            Console.WriteLine($"    best object: {b.Room.Folder}/objects/{ScummExporters.ObjectFrameFile(b.Object, b.Index)} ({b.Width}×{b.Height}, placed at {b.Object.X},{b.Object.Y}) {best.Score:0.000}, " +
                              (best.Inside ? $"at {best.X},{best.Y} in the image" : $"the image at {best.X},{best.Y} in it"));
        return best;
    }

    private static List<Frame> FramesOf(Room room, Dictionary<Room, List<Frame>> cache)
    {
        if (cache.TryGetValue(room, out var list)) return list;
        list = [];
        foreach (var obj in room.Model.Objects)
        {
            if (obj.ImageCount <= 0 || !ObimDecoders.TryGetObimInfo(obj.Block, out var info)) continue;
            int count = info.TotalFrames > 0 ? info.TotalFrames : 1;
            for (int i = 0; i < count; i++)
            {
                ObimDecoders.ObimResult result;
                try { result = ObimCache.GetCachedObimAsync(obj.Block, i, info.DefaultPaletteIndex, bakeImage: true, CancellationToken.None).GetAwaiter().GetResult(); }
                catch (Exception) { continue; }
                if (result.ExecutedImage is not { } image || image.Width < Cell || image.Height < Cell) continue;
                var grey = new float[image.Width * image.Height];
                var opaque = new bool[grey.Length];
                for (int p = 0; p < grey.Length; p++)
                {
                    grey[p] = 0.299f * image.Data[p * 4] + 0.587f * image.Data[p * 4 + 1] + 0.114f * image.Data[p * 4 + 2];
                    opaque[p] = image.Data[p * 4 + 3] >= 128;
                }
                list.Add(new Frame(room, obj, i, image.Width, image.Height, grey, opaque));
            }
        }
        return cache[room] = list;
    }

    /// <summary>A cell is opaque when most of its pixels are.</summary>
    private static bool[] ShrinkMask(bool[] mask, int w, int h)
    {
        int ow = w / Cell, oh = h / Cell;
        var dst = new bool[ow * oh];
        for (int y = 0; y < oh; y++)
            for (int x = 0; x < ow; x++)
            {
                int n = 0;
                for (int dy = 0; dy < Cell; dy++)
                    for (int dx = 0; dx < Cell; dx++) if (mask[(y * Cell + dy) * w + x * Cell + dx]) n++;
                dst[y * ow + x] = n * 2 > Cell * Cell;
            }
        return dst;
    }

    /// <summary>The correlation of <paramref name="small"/>'s opaque pixels with <paramref name="big"/>'s at (<paramref name="ox"/>,<paramref name="oy"/>).</summary>
    private static double Masked(float[] big, int bw, float[] small, bool[] mask, int sw, int sh, int ox, int oy)
    {
        double si = 0, sr = 0, sii = 0, srr = 0, sir = 0, n = 0;
        for (int y = 0; y < sh; y++)
        {
            int si0 = y * sw, bi0 = (y + oy) * bw + ox;
            for (int x = 0; x < sw; x++)
            {
                if (mask != null && !mask[si0 + x]) continue;
                double p = small[si0 + x], q = big[bi0 + x];
                si += p; sr += q; sii += p * p; srr += q * q; sir += p * q; n++;
            }
        }
        if (n < 16) return 0;
        double cov = sir - si * sr / n, vi = sii - si * si / n, vr = srr - sr * sr / n;
        return vi <= 1e-6 || vr <= 1e-6 ? 0 : cov / Math.Sqrt(vi * vr);
    }

    /// <summary>Where <paramref name="small"/> best matches inside <paramref name="big"/>, within <paramref name="reach"/> of (<paramref name="cx"/>,<paramref name="cy"/>).</summary>
    private static (int X, int Y, double Score) SlideMasked(float[] big, int bw, int bh, float[] small, bool[] mask, int sw, int sh, int cx, int cy, int reach)
    {
        int maxX = bw - sw, maxY = bh - sh;
        var best = (X: 0, Y: 0, Score: double.NegativeInfinity);
        if (maxX < 0 || maxY < 0 || sw <= 0 || sh <= 0) return best;
        for (int oy = Math.Max(0, cy - reach); oy <= Math.Min(maxY, (long)cy + reach); oy++)
            for (int ox = Math.Max(0, cx - reach); ox <= Math.Min(maxX, (long)cx + reach); ox++)
            {
                double score = Masked(big, bw, small, mask, sw, sh, ox, oy);
                if (score > best.Score) best = (ox, oy, score);
            }
        return best;
    }

    private static SKBitmap Decode(string file)
    {
        using var decoded = SKBitmap.Decode(file);
        if (decoded == null) return null;
        var bitmap = new SKBitmap(new SKImageInfo(decoded.Width, decoded.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using var canvas = new SKCanvas(bitmap);
        canvas.DrawBitmap(decoded, 0, 0);
        return bitmap;
    }

    private static float[] Grey(SKBitmap bitmap)
    {
        var data = bitmap.GetPixelSpan();
        var grey = new float[bitmap.Width * bitmap.Height];
        for (int i = 0; i < grey.Length; i++)
            grey[i] = 0.299f * data[i * 4] + 0.587f * data[i * 4 + 1] + 0.114f * data[i * 4 + 2];
        return grey;
    }

    /// <summary>The <paramref name="w"/>×<paramref name="h"/> image's <paramref name="by"/>-pixel squares averaged, <paramref name="ow"/>×<paramref name="oh"/> of them.</summary>
    private static float[] Shrink(float[] src, int w, int h, int by, int ow, int oh)
    {
        var dst = new float[Math.Max(0, ow * oh)];
        for (int y = 0; y < oh; y++)
            for (int x = 0; x < ow; x++)
            {
                float sum = 0;
                for (int dy = 0; dy < by; dy++)
                {
                    int row = (y * by + dy) * w + x * by;
                    for (int dx = 0; dx < by; dx++) sum += src[row + dx];
                }
                dst[y * ow + x] = sum / (by * by);
            }
        return dst;
    }

    /// <summary>
    /// Where the <paramref name="iw"/>×<paramref name="ih"/> image best matches the room, by correlation, at places
    /// within <paramref name="reach"/> of (<paramref name="cx"/>,<paramref name="cy"/>). An image larger than the room
    /// is compared where they overlap.
    /// </summary>
    private static (int X, int Y, double Score) BestPlace(float[] img, int iw, int ih, float[] room, int rw, int rh, int cx, int cy, int reach, int step)
    {
        int maxX = Math.Max(0, rw - iw), maxY = Math.Max(0, rh - ih);
        int x0 = Math.Max(0, cx - reach), x1 = (int)Math.Min(maxX, (long)cx + reach);
        int y0 = Math.Max(0, cy - reach), y1 = (int)Math.Min(maxY, (long)cy + reach);
        int w = Math.Min(iw, rw), h = Math.Min(ih, rh);
        var best = (X: 0, Y: 0, Score: double.NegativeInfinity);
        if (w <= 0 || h <= 0) return best;
        for (int oy = y0; oy <= y1; oy += step)
            for (int ox = x0; ox <= x1; ox += step)
            {
                double si = 0, sr = 0, sii = 0, srr = 0, sir = 0;
                for (int y = 0; y < h; y++)
                {
                    int ii = y * iw, ri = (y + oy) * rw + ox;
                    for (int x = 0; x < w; x++)
                    {
                        double p = img[ii + x], q = room[ri + x];
                        si += p; sr += q; sii += p * p; srr += q * q; sir += p * q;
                    }
                }
                double n = w * h, cov = sir - si * sr / n, vi = sii - si * si / n, vr = srr - sr * sr / n;
                double score = vi <= 1e-6 || vr <= 1e-6 ? 0 : cov / Math.Sqrt(vi * vr);
                if (score > best.Score) best = (ox, oy, score);
            }
        return best;
    }

    private static double Overlap(Match a, Match b)
    {
        int w = Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X);
        int h = Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y);
        return w <= 0 || h <= 0 ? 0 : (double)w * h / Math.Min(a.Width * a.Height, b.Width * b.Height);
    }

    private static double Coverage(Room room, List<Match> kept)
    {
        var covered = new bool[room.Width * room.Height];
        foreach (var k in kept)
            for (int y = k.Y; y < Math.Min(room.Height, k.Y + k.Height); y++)
                for (int x = k.X; x < Math.Min(room.Width, k.X + k.Width); x++) covered[y * room.Width + x] = true;
        return covered.Count(c => c) / (double)covered.Length;
    }

    private static void Blit(SKBitmap bitmap, byte[] dst, int dw, int dh, int ox, int oy)
    {
        var src = bitmap.GetPixelSpan();
        int w = Math.Min(bitmap.Width, dw - ox), h = Math.Min(bitmap.Height, dh - oy);
        for (int y = 0; y < h; y++)
            src.Slice(y * bitmap.Width * 4, w * 4).CopyTo(dst.AsSpan(((oy + y) * dw + ox) * 4, w * 4));
    }

    private static int LeadingNumber(string name)
    {
        int n = 0, i = 0;
        while (i < name.Length && char.IsDigit(name[i])) n = n * 10 + (name[i++] - '0');
        return i == 0 ? int.MaxValue : n;
    }
}
