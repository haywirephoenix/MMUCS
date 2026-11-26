using MMUCS.Core.Media;
using MMUCS.Core;
using MMUCS.Core.Imaging;
using MMUCS.Core.Scumm;

// mprobe: MMUCS.Core from the command line. See README.md.

const string Usage = """
usage: mprobe [--game FILE] COMMAND ...

  export OUT [--scripts] [--data] [--images] [--offsets] [--cutscenes] [--audio]
                       Export like File > Export into OUT. With no option: scripts and room data
                       (what tools/mscripts and tools/mroom read). --images adds backgrounds,
                       object frames and costume cels; --cutscenes the SMUSH frames (as the cutscenes
                       command); --audio the music and speech (as the audio command).
  zplanes ROOM OUT     Write the room's background with each z-plane tinted, one PNG per plane
                       (roomN_planeK.png): plane 1 red, 2 green, 3 blue, 4 yellow, 5 magenta, 6 cyan.
  masked ROOM OUT [--debug] [--only C,C…] [--start]
                       Write the room as the player and the room viewer draw it: background, the objects in
                       their states on entry, the actors MMUCS places (position, elevation, scale, draw
                       order), cut by their z-plane. Lists each
                       actor's costume, position, scale and z-plane. --debug paints the cut pixels magenta.
                       --only draws just those costumes. --start draws only what's there as the room
                       opens (placed on entry, not the player), as the room viewer starts.
                       ROOM "all" draws every room with a background (roomNNN_name.png) with all its
                       costumes, and writes the listings to OUT/costumes.txt, where each actor's line
                       also gives its walkbox and the scale that box would give it.
  box ROOM X Y [--player]
                       The walkboxes at a point: each box holding it or near it, with its flags, mask and
                       scale (its own, or its scale slot and what that gives at the point), the box the engine
                       puts an actor there in (and where it moves it), as an actor that follows the boxes, and
                       the ways out of that box by the room's box matrix. --player for the player's actor.
  fonts [N] [--char C …]
                       The game's fonts (charset N is fontN.nut): glyphs, line height, the ink and dark-edge
                       values and whether the edge is an outline all round or a drop shadow, every pixel
                       value with its count; --char draws a glyph (# ink, o edge, . transparent).
  costume N [ANIM …] [--dir D]
                       A costume's chores (AKCH) per animation and direction with their limbs; with ANIM each
                       limb's AKSQ steps: the cels a step draws (place and size from the actor) or the op.
  mods [--check]       The music mods (_mods/music/<mod>/mod.json beside the game): the tracks each replaces,
                       problems reading the maps, the game's tracks none replaces; --check decodes every
                       FLAC in them against its MD5.
  match TRACK [FILE] [--in DIR] [--chunks]
  match all --out FILE [--in DIR] [--only T,T…]
                       Where game music track TRACK (1100, or a name: Hold1) is in a FLAC, to the sample: its
                       4 s chunks found by cross-correlation, the runs of them at one offset, the game's jumps
                       in the file's time, and the mod.json entry (start, end, loopStart, loopEnd) they give.
                       Without FILE, every FLAC in the music mods (or under DIR) is ranked and the best matched.
                       --chunks lists every chunk with its score and the next best place's. Each match is
                       made plainly and whitened, keeping the higher score. "all" matches every music track
                       and writes the mod.json to FILE (its cutscene keys and other fields kept), each entry
                       with its score, mode and notes; a track not found gets a blank "file" and its best guess.
                       FLACs are looked for beside FILE unless --in says where; --only matches just those
                       tracks, keeping FILE's entries for the rest.

  audio OUT [music|voice] [--check]
                       Decode the Digital iMUSE bundles (.bun) into OUT/music/NNNN.wav and OUT/voice/CODE.wav,
                       each with a .meta.json (format, title, regions, jumps, markers, lip sync, in sample
                       frames). Sounds on both discs are written once. --check decodes and checks everything
                       without writing.
  play ID OUT.wav [--seconds N] [--rate HZ] [--hook HOOK@SECONDS …]
                       Play a bundle sound (music/1098, voice/bccb301) through MMUCS's mixer and iMUSE player,
                       offline, into a 16-bit stereo WAV: regions, loops, crossfades and markers as the game
                       plays them, each listed as it happens. --hook sets the track's hook at a time (128
                       leaves a loop). Defaults: 600 s at most, 44100 Hz (22050 reproduces the samples exactly).
  listen ID [--seconds N] [--rate HZ] [--hook HOOK@SECONDS …] [--device NAME]
                       As play, but out loud through OpenAL Soft (the default sound device). Hooks follow
                       the played clock. Reports the latency, underruns and how the played clock kept
                       time with the wall clock.
  cutscene-audio [OUT]  Decode and check every cutscene's sound (packets, leftover bytes, length against the
                       picture); with OUT, write each as OUT/NAME.wav.
  cutscenes OUT [NAME…]
                       Decode the SMUSH cutscenes (.san beside the game or in its resource folder) into
                       OUT/video/NAME/: one indexed PNG per distinct picture under frame/, and video.json
                       mapping every frame to the picture it shows. NAME picks videos (opening, finale…).

  boot [--seconds N] [--click WHAT@S …] [--right-click WHAT@S …] [--key KEY@S …] [--calls] [--every-frame]
       [--vars V,V…] [--boot N] [--limbs A,A…] [--save FILE@S …] [--load FILE] [--saves DIR]
       [--replay [FILE]]
                       Run the game's scripts headless with MMUCS.Runtime's interpreter: the boot script,
                       then N seconds of frames (default 10). Prints each script starting (▸) and ending (◂),
                       indented by nesting, rooms entered, faults and warnings; then the scripts still running,
                       some variables (--vars adds more, by number or name) and the instructions not carried
                       out yet. During frames each line shows once unless --every-frame. --calls also lists
                       those instructions as they're met, with their arguments. --click presses the left button
                       S seconds after boot (default 1) on WHAT, an object of the current room (number or name,
                       at its middle) or X,Y on the screen, and lets go 6 jiffies later; --right-click the same
                       with the right; --key presses a key (a character, esc, enter, space, f1…f9 or a code).
                       --limbs prints those actors' animation and limbs (position in the sequence, s if
                       stopped) each time they change, with the speech's lip sync while the actor talks.
                       SAVE and LOAD show the scripts' temporary save (the F1 menu's) being taken and put
                       back; QUIT a script quitting, which ends the run. --save writes a save file S seconds
                       after boot (as the game window's F6), --load loads one before the frames (as F4).
                       --saves gives the F1 menu a folder for its slots and registry settings.
                       The room's objects are listed at the end. Exits 1 if a script faulted.

The game is the first resource file (COMI.LA0); the others beside it are loaded too. It comes from
--game, else the MMUCS_GAME environment variable, else the last game either gave (kept in the app data's
mprobe-game.txt).
""";

var argv = args.ToList();
// The game: --game, else MMUCS_GAME, else the last one either gave, which is kept for next time.
string lastGameFile = Path.Combine(MMUCS.Core.CorePaths.Root, "mprobe-game.txt");
string game = TakeOption(argv, "--game") ?? Environment.GetEnvironmentVariable("MMUCS_GAME");
if (string.IsNullOrEmpty(game) && File.Exists(lastGameFile)) game = File.ReadAllText(lastGameFile).Trim();
else if (!string.IsNullOrEmpty(game) && File.Exists(game))
{
    Directory.CreateDirectory(Path.GetDirectoryName(lastGameFile));
    File.WriteAllText(lastGameFile, Path.GetFullPath(game));
}
if (argv.Count == 0 || argv[0] is "-h" or "--help")
{
    Console.WriteLine(Usage);
    return 0;
}
if (string.IsNullOrEmpty(game) || !File.Exists(game))
{
    Console.Error.WriteLine($"mprobe: give the game's COMI.LA0 with --game FILE or the MMUCS_GAME variable{(string.IsNullOrEmpty(game) ? "" : $" ({game} not found)")}.");
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
        "cutscenes" => await ExportCutscenes(argv),
        "cutscene-audio" => CutsceneAudio(argv),
        "audio" => await ExportAudio(argv),
        "play" => Play(argv),
        "listen" => Listen(argv),
        "boot" => BootCommand.Run(root, argv, TakeOption, game),
        "fonts" => FontsCommand.Run(argv, TakeOption, game),
        "costume" => CostumeCommand.Run(root, argv, TakeOption),
        "mods" => ModsCommand.Run(argv, game),
        "match" => MatchCommand.Run(argv, TakeOption, FindSound, MusicNumbers, game),
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
    bool cutscenes = a.Contains("--cutscenes"), audio = a.Contains("--audio");
    if (!scripts && !data && !images && !cutscenes && !audio) scripts = data = true;
    var options = new ExportOptions
    {
        Backgrounds = images, Objects = images, Costumes = images,
        Scripts = scripts, ScriptOffsets = a.Contains("--offsets"), RoomData = data,
        Cutscenes = cutscenes, Audio = audio, GameFile = game,
    };
    var summary = await ScummExporters.ExportProjectAsync(root, outDir, options);
    Console.WriteLine($"{summary.FilesWritten} files written, {summary.Failures} failed, {summary.Elapsed.TotalSeconds:0.0} s → {outDir}");
    return summary.Failures > 0 ? 1 : 0;
}

static List<(double At, int Hook)> TakeHooks(List<string> a)
{
    var hooks = new List<(double At, int Hook)>();
    for (string h; (h = TakeOption(a, "--hook")) != null;)
    {
        var parts = h.Split('@');
        if (parts.Length != 2 || !int.TryParse(parts[0], out int hook) || !double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out double at))
            throw new UsageException("--hook is HOOK@SECONDS, e.g. 128@300");
        hooks.Add((at, hook));
    }
    hooks.Sort();
    return hooks;
}

int Listen(List<string> a)
{
    var hooks = TakeHooks(a);
    double seconds = double.TryParse(TakeOption(a, "--seconds"), System.Globalization.CultureInfo.InvariantCulture, out var sec) ? sec : 600;
    int rate = int.TryParse(TakeOption(a, "--rate"), out var r) ? r : 44100;
    string deviceName = TakeOption(a, "--device");
    string id = Positional(a, 1, "ID (music/1098, voice/bccb301)");

    var sound = FindSound(id) ?? throw new UsageException($"no sound {id} in the bundles");
    var player = new MMUCS.Core.Media.ImusPlayer(sound);
    var mixer = new MMUCS.Core.Media.Mixer(rate);
    using var output = new MMUCS.Media.OpenAL.OpenALOutput(mixer, deviceName);
    Console.WriteLine($"{id}: {sound.Title ?? "(no title)"} → {output.Name}, {rate} Hz, latency {output.Latency.TotalMilliseconds:0} ms");
    // Events happen as the mixer reads ahead; they're heard one latency later.
    player.EventRaised += e => Console.WriteLine(
        $"  mixed {mixer.Time.TotalSeconds,8:0.000} s  {e.Kind,-11} frame {e.Frame,9}" +
        (e.Kind is MMUCS.Core.Media.ImusEvent.EventKind.Jump or MMUCS.Core.Media.ImusEvent.EventKind.JumpSkipped ? $" → {e.ToFrame} (hook {e.Hook})" : "") +
        (e.Text != null ? $"  \"{e.Text}\"" : ""));
    var voice = mixer.Play(player, MMUCS.Core.Media.MixGroup.Music);

    // Starting fills the device queue, so hooks for the very start must be set before it.
    int nextHook = 0;
    for (; nextHook < hooks.Count && hooks[nextHook].At <= 0; nextHook++)
    {
        player.Hook = hooks[nextHook].Hook;
        Console.WriteLine($"  before start  hook set to {hooks[nextHook].Hook}");
    }
    var clock = System.Diagnostics.Stopwatch.StartNew();
    output.Start();
    double maxDrift = 0;
    while (output.PlayedTime().TotalSeconds < seconds && !voice.Finished)
    {
        double played = output.PlayedTime().TotalSeconds;
        if (played > 0.5) maxDrift = Math.Max(maxDrift, Math.Abs(clock.Elapsed.TotalSeconds - played));
        while (nextHook < hooks.Count && played >= hooks[nextHook].At)
        {
            player.Hook = hooks[nextHook].Hook;
            Console.WriteLine($"  heard {played,8:0.000} s  hook set to {hooks[nextHook].Hook}");
            nextHook++;
        }
        Thread.Sleep(10);
    }
    // Let what is queued play out before stopping.
    Thread.Sleep(output.Latency + TimeSpan.FromMilliseconds(50));
    output.Stop();
    Console.WriteLine($"played {output.PlayedTime().TotalSeconds:0.000} s in {clock.Elapsed.TotalSeconds:0.000} s of wall time, " +
                      $"played clock vs wall clock at most {maxDrift * 1000:0} ms apart, {output.Underruns} underruns, {mixer.ClippedSamples} clipped samples");
    return output.Underruns > 0 ? 1 : 0;
}

int Play(List<string> a)
{
    var hooks = TakeHooks(a);
    double seconds = double.TryParse(TakeOption(a, "--seconds"), System.Globalization.CultureInfo.InvariantCulture, out var sec) ? sec : 600;
    int rate = int.TryParse(TakeOption(a, "--rate"), out var r) ? r : 44100;
    string id = Positional(a, 1, "ID (music/1098, voice/bccb301)");
    string outFile = Positional(a, 2, "OUT.wav");

    var sound = FindSound(id) ?? throw new UsageException($"no sound {id} in the bundles");
    var player = new MMUCS.Core.Media.ImusPlayer(sound);
    var mixer = new MMUCS.Core.Media.Mixer(rate);
    Console.WriteLine($"{id}: {sound.Title ?? "(no title)"}  {sound.SampleRate} Hz  {sound.Channels} ch  {sound.FrameCount} frames  → mixer at {rate} Hz");
    player.EventRaised += e => Console.WriteLine(
        $"  {mixer.Time.TotalSeconds,8:0.000} s  {e.Kind,-11} frame {e.Frame,9}" +
        (e.Kind is MMUCS.Core.Media.ImusEvent.EventKind.Jump or MMUCS.Core.Media.ImusEvent.EventKind.JumpSkipped ? $" → {e.ToFrame} (hook {e.Hook})" : "") +
        (e.Text != null ? $"  \"{e.Text}\"" : ""));
    var voice = mixer.Play(player, MMUCS.Core.Media.MixGroup.Music);

    const int block = 1024;
    var buf = new float[block * 2];
    using var pcm = new MemoryStream();
    long limit = (long)(seconds * rate);
    int nextHook = 0;
    while (mixer.Position < limit && !voice.Finished)
    {
        while (nextHook < hooks.Count && mixer.Time.TotalSeconds >= hooks[nextHook].At)
        {
            player.Hook = hooks[nextHook].Hook;
            Console.WriteLine($"  {mixer.Time.TotalSeconds,8:0.000} s  hook set to {hooks[nextHook].Hook}");
            nextHook++;
        }
        mixer.Read(buf);
        foreach (float f in buf)
        {
            short v = (short)Math.Clamp((int)MathF.Round(f * 32767f), short.MinValue, short.MaxValue);
            pcm.WriteByte((byte)v);
            pcm.WriteByte((byte)(v >> 8));
        }
    }
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
    using (var fs = File.Create(outFile))
        MMUCS.Core.Audio.WavWriter.Write(fs, pcm.GetBuffer().AsSpan(0, (int)pcm.Length), 2, rate, 16);
    Console.WriteLine($"rendered {mixer.Time.TotalSeconds:0.000} s ({mixer.Position} frames), {(voice.Finished ? "sound ended" : "stopped at the time limit")}, {mixer.ClippedSamples} clipped samples → {outFile}");
    return 0;
}

// The music tracks' numbers in the bundles (music/1100: 1100), on every disc.
IEnumerable<int> MusicNumbers()
{
    foreach (var file in GameFiles.Find(game, ".bun"))
    {
        if (MMUCS.Core.Audio.AudioExporter.KindOf(file) != "music") continue;
        using var bundle = MMUCS.Core.Audio.Bundle.Open(file);
        foreach (var e in bundle.Entries)
            if (MMUCS.Core.Audio.AudioExporter.IdOf("music", e.Name) is var id && int.TryParse(id.AsSpan(6), out int n)) yield return n;
    }
}

MMUCS.Core.Audio.ImusSound FindSound(string id)
{
    foreach (var file in GameFiles.Find(game, ".bun"))
    {
        string kind = MMUCS.Core.Audio.AudioExporter.KindOf(file);
        if (!id.StartsWith(kind + "/")) continue;
        using var bundle = MMUCS.Core.Audio.Bundle.Open(file);
        foreach (var e in bundle.Entries)
            if (MMUCS.Core.Audio.AudioExporter.IdOf(kind, e.Name) == id)
                return MMUCS.Core.Audio.ImusSound.Parse(MMUCS.Core.Audio.BundleCodec.Decompress(bundle.Read(e)));
    }
    return null;
}

async Task<int> ExportAudio(List<string> a)
{
    string outDir = Positional(a, 1, "OUT");
    bool check = a.Contains("--check");
    var kinds = a.Where(x => !x.StartsWith("--")).Skip(2).Select(k => k.ToLowerInvariant()).ToHashSet();
    var bundles = GameFiles.Find(game, ".bun")
        .Where(b => kinds.Count == 0 || kinds.Contains(MMUCS.Core.Audio.AudioExporter.KindOf(b)))
        .ToList();
    if (bundles.Count == 0) throw new UsageException("no .bun bundles beside the game or in its resource folder");

    MMUCS.Core.Diagnostics.Log.Message += (_, m) => Console.Error.WriteLine("  " + m);
    var seen = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
    int bad = 0;
    foreach (var file in bundles)
    {
        var s = await MMUCS.Core.Audio.AudioExporter.ExportBundleAsync(file, outDir, seen, write: !check);
        bad += s.Failures + s.MapProblems + s.Conflicts;
        Console.WriteLine($"{s.Bundle,-13} {s.Entries,5} entries  {s.Written,5} {(check ? "decoded" : "written")}  {s.Duplicates,4} on an earlier disc  " +
            $"{s.Skipped} skipped  {s.Failures} failed  {s.PcmBytes / 1048576.0,7:0.0} MB PCM  {s.Elapsed.TotalSeconds,5:0.0} s  " +
            $"checks: over-read max {s.MaxOverRead} B, map problems {s.MapProblems}, disc conflicts {s.Conflicts}");
    }
    if (!check) Console.WriteLine($"→ {outDir}");
    return bad > 0 ? 1 : 0;
}

int CutsceneAudio(List<string> a)
{
    string outDir = a.Where(x => !x.StartsWith("--")).Skip(1).FirstOrDefault();
    int bad = 0;
    foreach (var file in MMUCS.Core.Smush.SmushVideo.FindAll(game))
    {
        var video = MMUCS.Core.Smush.SmushVideo.Load(file);
        var track = MMUCS.Core.Smush.SmushAudio.Decode(video);
        double videoSeconds = video.Frames.Count / (double)video.Header.FrameRate;
        if (track == null) { Console.WriteLine($"{video.Name,-10} no sound"); continue; }
        bool ok = track.LeftoverBytes == 0;
        if (!ok) bad++;
        Console.WriteLine($"{video.Name,-10} {track.Chunks,5} chunks  {track.Packets,5} packets  {track.Escapes,7} escaped  leftover {track.LeftoverBytes} B  " +
                          $"sound {track.Duration.TotalSeconds,7:0.000} s  picture {videoSeconds,7:0.000} s  ({track.Duration.TotalSeconds - videoSeconds:+0.000;-0.000} s)");
        if (outDir != null)
        {
            Directory.CreateDirectory(outDir);
            using var fs = File.Create(Path.Combine(outDir, video.Name + ".wav"));
            MMUCS.Core.Audio.WavWriter.Write(fs, track.Pcm, track.Channels, track.SampleRate, 16);
        }
    }
    return bad > 0 ? 1 : 0;
}

async Task<int> ExportCutscenes(List<string> a)
{
    string outDir = Positional(a, 1, "OUT");
    var wanted = a.Where(x => !x.StartsWith("--")).Skip(2).Select(n => n.ToLowerInvariant()).ToHashSet();
    var files = MMUCS.Core.Smush.SmushVideo.FindAll(game)
        .Where(f => wanted.Count == 0 || wanted.Contains(Path.GetFileNameWithoutExtension(f).ToLowerInvariant()))
        .ToList();
    if (files.Count == 0) throw new UsageException(wanted.Count == 0 ? "no .san files beside the game or in its resource folder" : "no cutscene by that name");

    MMUCS.Core.Diagnostics.Log.Message += (_, m) => Console.Error.WriteLine("  " + m);
    int problems = 0;
    foreach (var file in files)
    {
        var s = await MMUCS.Core.Smush.SmushExporter.ExportAsync(file, outDir);
        problems += s.Problems;
        Console.WriteLine($"{s.Name,-10} {s.Frames,5} frames  {s.ImagesWritten,5} images  {s.Problems} problems  {s.Elapsed.TotalSeconds,5:0.0} s  " +
            $"unread: {s.FramesWithUnreadBytes} frames (max {s.MaxUnreadBytes} B)  outside copies: {s.OutsideCopies}");
    }
    Console.WriteLine($"→ {Path.Combine(outDir, "video")}");
    return problems > 0 ? 1 : 0;
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
        string scale = b.ScaleSlot == 0 ? $"scale {b.Scale}"
            : $"scale slot {b.ScaleSlot} ({Walkboxes.ScaleAt(boxes, room.ScaleSlots, b.Index, x, y)} here)";
        Console.WriteLine($"  box {b.Index,3}  {(inside ? "holds it" : $"{Math.Sqrt(dx * dx + dy * dy),5:0} px away")}  mask {b.Mask & 0xFF}  flags {flags}  {scale}"
            + $"  ({string.Join(" ", q.Select(p => $"{p.X},{p.Y}"))})");
    }
    if (room.ScaleSlots.Count > 0)
        Console.WriteLine("scale slots (SCAL; the scripts can change them): " +
                          string.Join(", ", room.ScaleSlots.Select((sl, i) => (sl, i)).Where(t => t.sl.Y1 != t.sl.Y2).Select(t => $"{t.i + 1}: {t.sl.Scale1} at y {t.sl.Y1} to {t.sl.Scale2} at y {t.sl.Y2}")));
    var (px, py, box) = RoomActors.PutInBox(boxes, x, y, player);
    Console.WriteLine($"engine: box {box} (mask {(box < boxes.Count ? boxes[box].Mask & 0xFF : 0)}){(px != x || py != y ? $", moved to {px},{py}" : "")}");
    if (room.Lflf?.FindChildRecursive(ScummTag.BOXM) is { } boxm && box > 0)
    {
        var ways = Enumerable.Range(1, boxes.Count - 1).Where(to => to != box)
            .Select(to => (To: to, Via: Walkboxes.NextBox(boxm.DataSpan, box, to)))
            .Select(w => w.Via < 0 ? $"{w.To} none" : w.Via == w.To ? $"{w.To} direct" : $"{w.To} via {w.Via}");
        Console.WriteLine($"ways from box {box} (BOXM): {string.Join(", ", ways)}");
    }
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
    // As the player draws it (MMUCS.Player's RoomScene, which the Room viewer draws with too): the objects
    // in their states on entry, then the actors in SCUMM's order (by y before elevation, then actor number),
    // each scaled as the engine scales it and cut by its z-plane (the room's planes with the shown objects' over them).
    var states = RoomObjectStates.Initial(room);
    int StateOf(RoomObject o) => states.GetValueOrDefault(o);
    var actors = RoomActors.Analyze(room).Where(x => x.CanDraw && (only == null || only.Contains(x.Costume)) && (!start || x.StartsShown))
        .OrderBy(x => x.SortY).ThenBy(x => x.Actor).ToList();
    var scene = await MMUCS.Player.Rooms.RoomSceneBuilder.BuildAsync(room, StateOf, MMUCS.Player.Rooms.RoomSceneBuilder.Actors(actors));
    if (scene.Background == null) throw new UsageException($"room {room.RoomNo} has no background");

    string file = Path.Combine(outDir, name ?? $"room{room.RoomNo}_{(debug ? "debug" : start ? "start" : "masked")}.png");
    using (var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(room.Width, room.Height)))
    {
        surface.Canvas.Clear(SkiaSharp.SKColors.Black);
        scene.Draw(surface.Canvas, new SkiaSharp.SKSamplingOptions(SkiaSharp.SKFilterMode.Nearest), cutColor: debug ? SkiaSharp.SKColors.Magenta : null);
        using var png = surface.Snapshot().Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        await File.WriteAllBytesAsync(file, png.ToArray());
    }

    // How many of the actors' pixels the planes hide.
    var planes = MMUCS.Player.Rooms.RoomScene.Planes(room, MMUCS.Player.Rooms.RoomSceneBuilder.Shown(room, StateOf));
    int cut = 0;
    var lines = new List<string>();
    foreach (var actor in actors)
    {
        if (MMUCS.Player.Rooms.RoomSceneBuilder.ActorFrame(actor) is not { } f) continue;
        var plane = actor.ZPlane > 0 && actor.ZPlane <= planes.Length ? planes[actor.ZPlane - 1] : null;
        if (plane != null)
            for (int y = 0; y < f.Image.Height; y++)
                for (int x = 0; x < f.Image.Width; x++)
                    if (f.Image.Data[(y * f.Image.Width + x) * 4 + 3] >= 128 && plane.IsSet(f.X + x, f.Y + y)) cut++;
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
                      (actor.Scale != 255 ? $" scale {actor.Scale}" : "") + (actor.Mirrored ? " mirrored" : "") + $"  z-plane {actor.ZPlane}" +
                      $"  {RoomDataExport.WhenText(actor.When) ?? "?"}{(actor.IsPlayer ? ", player" : "")}" +
                      (actor.Owner != null ? $"  ({actor.Owner.Name})" : "") + box);
    }
    // One room alone: its actors, then where the picture went. In the listing of all rooms: the room first.
    string summary = $"room {room.RoomNo} {room.Name}: {scene.Objects.Count} objects and {lines.Count} actors drawn, {cut} actor pixels cut by z-planes → {file}";
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
