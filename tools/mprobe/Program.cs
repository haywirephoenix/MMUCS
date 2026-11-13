using MMUCS.Core.Imaging;
using MMUCS.Core.Scumm;

// mprobe: MMUCS.Core from the command line. See README.md.

const string Usage = """
usage: mprobe [--game FILE] COMMAND ...

  export OUT [--scripts] [--data] [--images] [--offsets]
                       Export like File > Export into OUT. With no option: scripts and room data
                       (what tools/mscripts and tools/mroom read). --images adds backgrounds,
                       object frames and costume cels.
  zplanes ROOM OUT     Write the room's background with each z-plane tinted, one PNG per plane
                       (roomN_planeK.png): plane 1 red, 2 green, 3 blue, 4 yellow, 5 magenta, 6 cyan.
  masked ROOM OUT [--debug] [--only C,C…] [--start]
                       Write the room as the room viewer composes it: background, the actors MMUCS
                       places (position, elevation, scale, draw order), cut by their z-plane. Lists each
                       actor's costume, position, scale and z-plane. --debug paints the cut pixels magenta.
                       --only draws just those costumes. --start draws only what's there as the room
                       opens (placed on entry, not the player), as the room viewer starts.
                       ROOM "all" draws every room with a background (roomNNN_name.png) with all its
                       costumes, and writes the listings to OUT/costumes.txt, where each actor's line
                       also gives its walkbox and the scale that box would give it.
  box ROOM X Y [--player]
                       The walkboxes at a point: each box holding it or near it, with its flags and mask,
                       and the box the engine puts an actor there in (and where it moves it), as an actor
                       that follows the boxes. --player for the player's actor (player-only boxes).

The game is the first resource file (COMI.LA0); the others beside it are loaded too. It comes from
--game, else the MMUCS_GAME environment variable.
""";

var argv = args.ToList();
string game = TakeOption(argv, "--game") ?? Environment.GetEnvironmentVariable("MMUCS_GAME");
if (argv.Count == 0 || argv[0] is "-h" or "--help")
{
    Console.WriteLine(Usage);
    return 0;
}
if (string.IsNullOrEmpty(game) || !File.Exists(game))
{
    Console.Error.WriteLine("mprobe: give the game's COMI.LA0 with --game FILE or the MMUCS_GAME variable.");
    return 2;
}

var sw = System.Diagnostics.Stopwatch.StartNew();
var root = ScummProject.Load(ScummProject.FindRelatedFiles(game));
Console.Error.WriteLine($"loaded {Path.GetFileName(game)} in {sw.Elapsed.TotalSeconds:0.0} s");

try
{
    return argv[0] switch
    {
        "export" => await Export(argv),
        "zplanes" => await DrawZPlanes(argv),
        "masked" => await DrawMasked(argv),
        "box" => ShowBox(argv),
        _ => Fail($"unknown command '{argv[0]}'"),
    };
}
catch (UsageException ex)
{
    return Fail(ex.Message);
}

async Task<int> Export(List<string> a)
{
    string outDir = Positional(a, 1, "OUT");
    bool scripts = a.Contains("--scripts"), data = a.Contains("--data"), images = a.Contains("--images");
    if (!scripts && !data && !images) scripts = data = true;
    var options = new ExportOptions
    {
        Backgrounds = images, Objects = images, Costumes = images,
        Scripts = scripts, ScriptOffsets = a.Contains("--offsets"), RoomData = data,
    };
    var summary = await new ScummExporters().ExportProjectAsync(root, outDir, options);
    Console.WriteLine($"{summary.FilesWritten} files written, {summary.Failures} failed, {summary.Elapsed.TotalSeconds:0.0} s → {outDir}");
    return summary.Failures > 0 ? 1 : 0;
}

async Task<int> DrawZPlanes(List<string> a)
{
    var room = FindRoom(Positional(a, 1, "ROOM"));
    string outDir = Positional(a, 2, "OUT");
    Directory.CreateDirectory(outDir);
    var planes = ZPlanes.Room(room);
    Console.WriteLine($"room {room.RoomNo} {room.Name} {room.Width}x{room.Height}: {planes.Length} planes, the room header says {ZPlanes.Count(room)}");
    var bg = await Background(room);
    var colors = new (byte R, byte G, byte B)[] { (255, 0, 0), (0, 255, 0), (0, 0, 255), (255, 255, 0), (255, 0, 255), (0, 255, 255), (255, 255, 255) };
    for (int k = 0; k < planes.Length; k++)
    {
        if (planes[k] == null) continue;
        var img = Copy(bg);
        int set = 0;
        var c = colors[k % colors.Length];
        for (int y = 0; y < img.Height; y++)
            for (int x = 0; x < img.Width; x++)
            {
                if (!planes[k].IsSet(x, y)) continue;
                set++;
                int i = (y * img.Width + x) * 4;
                img.Data[i] = (byte)((img.Data[i] + c.R) / 2);
                img.Data[i + 1] = (byte)((img.Data[i + 1] + c.G) / 2);
                img.Data[i + 2] = (byte)((img.Data[i + 2] + c.B) / 2);
            }
        string file = Path.Combine(outDir, $"room{room.RoomNo}_plane{k + 1}.png");
        img.SavePng(file);
        Console.WriteLine($"  plane {k + 1}: {100.0 * set / (img.Width * img.Height):0.0}% set → {file}");
    }
    return 0;
}

int ShowBox(List<string> a)
{
    var room = FindRoom(Positional(a, 1, "ROOM"));
    if (!int.TryParse(Positional(a, 2, "X"), out int x) || !int.TryParse(Positional(a, 3, "Y"), out int y))
        throw new UsageException("X and Y are numbers");
    bool player = a.Contains("--player");
    var boxes = room.Walkboxes;
    Console.WriteLine($"room {room.RoomNo}: {boxes.Count} boxes, {ZPlanes.Count(room)} z-planes; point {x},{y}");
    foreach (var b in boxes)
    {
        if (b.IsPlaceholder) continue;
        var q = new[] { b.UpperLeft, b.UpperRight, b.LowerRight, b.LowerLeft };
        bool inside = RoomActors.InBox(q, x, y);
        int dx = Math.Max(0, Math.Max(q.Min(p => p.X) - x, x - q.Max(p => p.X)));
        int dy = Math.Max(0, Math.Max(q.Min(p => p.Y) - y, y - q.Max(p => p.Y)));
        if (!inside && dx * dx + dy * dy > 80 * 80) continue;
        uint f = b.Flags & 0xFF;
        string flags = $"0x{f:X2}{((f & 0x80) != 0 ? " invisible" : "")}{((f & 0x20) != 0 ? " player-only" : "")}";
        Console.WriteLine($"  box {b.Index,3}  {(inside ? "holds it" : $"{Math.Sqrt(dx * dx + dy * dy),5:0} px away")}  mask {b.Mask & 0xFF}  flags {flags}"
            + $"  ({string.Join(" ", q.Select(p => $"{p.X},{p.Y}"))})");
    }
    var (px, py, box) = RoomActors.PutInBox(boxes, x, y, player);
    Console.WriteLine($"engine: box {box} (mask {(box < boxes.Count ? boxes[box].Mask & 0xFF : 0)}){(px != x || py != y ? $", moved to {px},{py}" : "")}");
    return 0;
}

async Task<int> DrawMasked(List<string> a)
{
    string wanted = Positional(a, 1, "ROOM");
    string outDir = Positional(a, 2, "OUT");
    bool debug = a.Contains("--debug"), start = a.Contains("--start");
    // --only 122,112: draw just these costumes (to see which picture is which, and how they overlap).
    var only = TakeOption(a, "--only")?.Split(',').Select(int.Parse).ToHashSet();
    Directory.CreateDirectory(outDir);
    if (wanted != "all")
        return await DrawRoom(FindRoom(wanted), outDir, debug, start, only, Console.Out, null, boxes: false);

    // Every room: one picture each, sorted by number, and one listing to read them by.
    var rooms = root.FindChildrenRecursive(ScummTag.LFLF)
        .Select(l => RoomModel.TryCreate(l, out var r) ? r : null)
        .Where(r => r != null && r.Width > 0).OrderBy(r => r.RoomNo).ToList();
    string listing = Path.Combine(outDir, "costumes.txt");
    int failed = 0;
    using (var log = new StreamWriter(listing))
        foreach (var room in rooms)
        {
            try { await DrawRoom(room, outDir, debug, start, only, log, $"room{room.RoomNo:000}_{room.Name}.png", boxes: true); }
            catch (Exception ex) when (ex is not UsageException)
            {
                failed++;
                log.WriteLine($"room {room.RoomNo} {room.Name}: failed: {ex.Message}");
            }
            log.WriteLine();
        }
    Console.WriteLine($"{rooms.Count - failed} rooms drawn, {failed} failed → {outDir} (listing: {listing})");
    return failed > 0 ? 1 : 0;
}

async Task<int> DrawRoom(RoomModel room, string outDir, bool debug, bool start, HashSet<int> only, TextWriter log, string name, bool boxes)
{

    // The mask as the viewer builds it: the room's planes with the objects shown on entry over them.
    var states = RoomObjectStates.Initial(room);
    var shown = room.Objects.Where(o => o.ImageCount > 0 && states[o] != 0).ToList();
    var planes = ZPlanes.Compose(ZPlanes.Room(room), ZPlanes.Count(room), room.Width, room.Height,
        shown.Select(o => (o, ZPlanes.Object(o, Math.Max(0, states[o] - 1)))));

    var img = Copy(await Background(room));
    // SCUMM's draw order: by y before elevation, then actor number.
    var actors = RoomActors.Analyze(room).Where(x => x.CanDraw && (only == null || only.Contains(x.Costume)) && (!start || x.StartsShown))
        .OrderBy(x => x.SortY).ThenBy(x => x.Actor).ToList();
    int cut = 0;
    var lines = new List<string>();
    foreach (var actor in actors)
    {
        var frame = AkosFrames.Render(actor.Data, actor.Frame, out int ox, out int oy);
        if (frame == null) continue;
        float f = actor.ScaleFactor;
        int w = (int)(frame.Width * f), h = (int)(frame.Height * f);
        int x0 = actor.X - (int)(ox * f), y0 = actor.DrawY - (int)(oy * f);
        var plane = actor.ZPlane > 0 && actor.ZPlane <= planes.Length ? planes[actor.ZPlane - 1] : null;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int sx = (int)(x / f), sy = (int)(y / f), rx = x0 + x, ry = y0 + y;
                if (rx < 0 || ry < 0 || rx >= img.Width || ry >= img.Height || sx >= frame.Width || sy >= frame.Height) continue;
                int si = (sy * frame.Width + sx) * 4;
                if (frame.Data[si + 3] < 128) continue;
                int di = (ry * img.Width + rx) * 4;
                if (plane != null && plane.IsSet(rx, ry))
                {
                    cut++;
                    if (debug) { img.Data[di] = 255; img.Data[di + 1] = 0; img.Data[di + 2] = 255; img.Data[di + 3] = 255; }
                    continue;
                }
                for (int c = 0; c < 4; c++) img.Data[di + c] = frame.Data[si + c];
            }
        string who = actor.Actor > 0 ? $"actor {actor.Actor}" : actor.DressedBy >= 0 ? $"dressed by script {actor.DressedBy}" : "actor ?";
        // The box at its feet and the scale it would give an actor that follows the boxes: an actor drawn
        // at another scale either ignores the boxes or has its scale wrong.
        string box = "";
        if (boxes)
        {
            var (_, _, b) = RoomActors.PutInBox(room.Walkboxes, actor.X, actor.Y, actor.IsPlayer);
            int bs = Walkboxes.ScaleAt(room.Walkboxes, room.ScaleSlots, b, actor.X, actor.Y);
            box = $"  [box {b} scale {bs}{(bs != actor.Scale ? " ≠" : "")}]";
        }
        lines.Add($"  costume {actor.Costume,4}  {who,-22} at {actor.X},{actor.Y}" +
                      (actor.Elevation != 0 ? $" elevation {actor.Elevation}" : "") +
                      (actor.Scale != 255 ? $" scale {actor.Scale}" : "") + $"  z-plane {actor.ZPlane}" +
                      $"  {RoomDataExport.WhenText(actor.When) ?? "?"}{(actor.IsPlayer ? ", player" : "")}" +
                      (actor.Owner != null ? $"  ({actor.Owner.Name})" : "") + box);
    }
    string file = Path.Combine(outDir, name ?? $"room{room.RoomNo}_{(debug ? "debug" : start ? "start" : "masked")}.png");
    img.SavePng(file);
    // One room alone: its actors, then where the picture went. In the listing of all rooms: the room first.
    string summary = $"room {room.RoomNo} {room.Name}: {actors.Count} actors drawn, {cut} pixels cut by z-planes → {file}";
    if (boxes) log.WriteLine(summary);
    foreach (var line in lines) log.WriteLine(line);
    if (!boxes) log.WriteLine(summary);
    return 0;
}

RoomModel FindRoom(string wanted)
{
    foreach (var lflf in root.FindChildrenRecursive(ScummTag.LFLF))
        if (RoomModel.TryCreate(lflf, out var room)
            && (int.TryParse(wanted, out int no) ? room.RoomNo == no : string.Equals(room.Name, wanted, StringComparison.OrdinalIgnoreCase)))
            return room;
    throw new UsageException($"no room '{wanted}'");
}

async Task<RgbaImage> Background(RoomModel room)
{
    if (room.Width <= 0) throw new UsageException($"room {room.RoomNo} has no background");
    var bg = await ScummBackgroundCache.GetCachedBackgroundAsync(room.RoomNo, room.Rmhd, room.Room, cacheDisabled: true, 0, default);
    return bg.ExecutedImage ?? throw new UsageException($"room {room.RoomNo}'s background didn't decode");
}

static RgbaImage Copy(RgbaImage src)
{
    var img = RgbaImage.CreateEmpty(src.Width, src.Height);
    src.Data.CopyTo(img.Data, 0);
    return img;
}

static string Positional(List<string> a, int index, string name)
{
    var positional = a.Where(x => !x.StartsWith("--")).ToList();
    return index < positional.Count ? positional[index] : throw new UsageException($"{a[0]} needs {name}");
}

static string TakeOption(List<string> a, string name)
{
    int i = a.IndexOf(name);
    if (i < 0 || i + 1 >= a.Count) return null;
    string value = a[i + 1];
    a.RemoveRange(i, 2);
    return value;
}

static int Fail(string message)
{
    Console.Error.WriteLine($"mprobe: {message}");
    Console.Error.WriteLine("Run mprobe --help for the commands.");
    return 2;
}

sealed class UsageException(string message) : Exception(message);
