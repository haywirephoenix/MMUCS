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
  masked ROOM OUT [--debug]
                       Write the room as the room viewer composes it: background, the actors MMUCS
                       places (position, elevation, scale, draw order), cut by their z-plane. Lists each
                       actor's costume, position, scale and z-plane. --debug paints the cut pixels magenta.

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

async Task<int> DrawMasked(List<string> a)
{
    var room = FindRoom(Positional(a, 1, "ROOM"));
    string outDir = Positional(a, 2, "OUT");
    bool debug = a.Contains("--debug");
    Directory.CreateDirectory(outDir);

    // The mask as the viewer builds it: the room's planes with the objects shown on entry over them.
    var states = RoomObjectStates.Initial(room);
    var shown = room.Objects.Where(o => o.ImageCount > 0 && states[o] != 0).ToList();
    var planes = ZPlanes.Compose(ZPlanes.Room(room), ZPlanes.Count(room), room.Width, room.Height,
        shown.Select(o => (o, ZPlanes.Object(o, Math.Max(0, states[o] - 1)))));

    var img = Copy(await Background(room));
    // SCUMM's draw order: by y before elevation, then actor number.
    var actors = RoomActors.Analyze(room).Where(x => x.CanDraw).OrderBy(x => x.Y).ThenBy(x => x.Actor).ToList();
    int cut = 0;
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
        Console.WriteLine($"  costume {actor.Costume,4}  {who,-22} at {actor.X},{actor.Y}" +
                          (actor.Elevation != 0 ? $" elevation {actor.Elevation}" : "") +
                          (actor.Scale != 255 ? $" scale {actor.Scale}" : "") + $"  z-plane {actor.ZPlane}" +
                          (actor.Owner != null ? $"  ({actor.Owner.Name})" : ""));
    }
    string file = Path.Combine(outDir, $"room{room.RoomNo}_{(debug ? "debug" : "masked")}.png");
    img.SavePng(file);
    Console.WriteLine($"room {room.RoomNo} {room.Name}: {actors.Count} actors drawn, {cut} pixels cut by z-planes → {file}");
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
