using MMUCS.Core.Scumm;
using MMUCS.Runtime.Scripts;
using Xunit;
using Xunit.Abstractions;

namespace MMUCS.Runtime.Tests;

/// <summary>
/// The script store finds every script the way the engine does, and each one decodes from its entry to
/// its end with known opcodes (a wrong entry or block shows up as an unknown opcode). Needs the game
/// (MMUCS_GAME).
/// </summary>
public class ScriptStoreTests(ITestOutputHelper output)
{
    private static string Game => Environment.GetEnvironmentVariable("MMUCS_GAME") is { } g && File.Exists(g) ? g : null;

    private static readonly Lazy<ScummBlock> Root = new(() => ScummProject.Load(ScummProject.FindRelatedFiles(Game), null));
    private static readonly Lazy<ScriptStore> Store = new(() => new ScriptStore(Root.Value));

    private static List<string> Problems(ScriptCode s, int end = -1)
    {
        var list = ScummScript.Disassemble(s.Code.Span, s.Entry, end);
        var problems = new List<string>();
        if (list.Count == 0) problems.Add($"{s}: no instructions");
        else if (list[^1].Unknown) problems.Add($"{s}: unknown opcode 0x{list[^1].Opcode:X2} at {list[^1].Offset}");
        foreach (var i in list)
            if (i.Target >= 0 && (i.Target < 0 || i.Target > s.Code.Length))
                problems.Add($"{s}: jump at {i.Offset} to {i.Target}, outside 0..{s.Code.Length}");
        return problems;
    }

    [Fact]
    public void Every_script_is_found_and_decodes()
    {
        if (Game == null) return;
        var store = Store.Value;
        var problems = new List<string>();

        // Every number the directory uses resolves; the unused ones (72–85 in COMI) are (0, 0).
        int globals = 0;
        var directory = RoomScripts.GlobalScripts(Root.Value);
        for (int n = 1; n < store.GlobalCount; n++)
            if (store.Global(n) is not { } g)
                Assert.Equal((0, 0L), directory[n]);
            else
            {
                globals++;
                Assert.Equal(ScriptKind.Global, g.Kind);
                Assert.Equal(n, g.Number);
                problems.AddRange(Problems(g));
            }

        int locals = 0, objects = 0, verbs = 0, rooms = 0;
        foreach (int room in store.RoomNumbers)
        {
            rooms++;
            foreach (int no in store.LocalNumbers(room))
            {
                locals++;
                Assert.InRange(no, ScriptStore.FirstLocal, int.MaxValue);
                var l = store.Local(room, no);
                Assert.Same(l, store.Find(no, room));
                problems.AddRange(Problems(l));
            }
            foreach (var s in new[] { store.RoomEntry(room), store.RoomExit(room) })
                if (s != null) problems.AddRange(Problems(s));
            foreach (var obj in store.Objects(room))
            {
                objects++;
                Assert.InRange(obj.Id, 1, ushort.MaxValue);
                Assert.Same(obj, store.Object(room, obj.Id));
                // Each verb's code runs to the next verb's start (or the end).
                var starts = obj.Verbs.Select(v => v.Offset).Distinct().Order().ToList();
                foreach (var (verb, offset) in obj.Verbs)
                {
                    verbs++;
                    var s = obj.Script(verb);
                    Assert.NotNull(s);
                    int end = starts.FirstOrDefault(o => o > offset, obj.Code.Length);
                    if (obj.EntryFor(verb) == offset) problems.AddRange(Problems(s, end));
                }
            }
        }

        output.WriteLine($"{rooms} rooms, {globals} global scripts of {store.GlobalCount - 1} numbers, {locals} local, {objects} objects with {verbs} verb entries");
        foreach (var p in problems.Take(40)) output.WriteLine(p);
        Assert.Empty(problems);
        Assert.True(globals > 100 && locals > 100 && objects > 100, "too few scripts found");
    }

    [Fact]
    public void Boot_and_start_script_numbers_resolve_as_the_engine_does()
    {
        if (Game == null) return;
        var store = Store.Value;

        var boot = store.Global(1);
        Assert.NotNull(boot);
        output.WriteLine(boot.ToString());
        Assert.Same(boot, store.Find(1, 0));
        Assert.Null(store.Global(0));
        Assert.Null(store.Global(store.GlobalCount));

        // A local number only means something in its own room.
        int room = store.RoomNumbers.First(r => store.LocalNumbers(r).Any());
        int no = store.LocalNumbers(room).First();
        Assert.NotNull(store.Find(no, room));
        int other = store.RoomNumbers.First(r => r != room && store.Local(r, no) == null);
        Assert.Null(store.Find(no, other));
    }

    [Fact]
    public void A_default_verb_answers_any_verb_after_the_ones_listed_before_it()
    {
        var obj = new ObjectCode { Id = 1, Room = 1, Code = new byte[64], Verbs = [(3, 10), (-1, 20), (5, 30)] };
        Assert.Equal(10, obj.EntryFor(3));
        Assert.Equal(20, obj.EntryFor(5)); // the default comes first
        Assert.Equal(20, obj.EntryFor(99));
        var none = new ObjectCode { Id = 2, Room = 1, Code = new byte[64], Verbs = [(3, 10)] };
        Assert.Null(none.EntryFor(4));
        Assert.Null(none.Script(4));
    }

    [Fact]
    public void A_mod_s_cel_PNG_replaces_the_costume_s_cel_in_its_own_colours_at_its_meta_offset()
    {
        if (Game == null) return;
        var root = Root.Value;
        var akos = RoomActors.CostumeBlock(root, 140);
        var game = AkosParser.Parse(akos);
        var cel = AkosDecoders.DecodeCel(game, 48, default);
        Assert.Equal("rooms/022_clring-a/costumes/akos_02A4A050", CostumeCelMods.FolderOf(akos).Replace('\\', '/'));

        // The game's cel 48 as the export draws it (shadow colours 40% black), 13 px wider on the left.
        int w = cel.Width + 13, h = cel.Height;
        var rgba = new byte[w * h * 4];
        var akpl = game.Palette.Span;
        for (int y = 0; y < cel.Height; y++)
            for (int x = 0; x < cel.Width; x++)
            {
                byte local = cel.Pixels[y * cel.Pitch + x];
                if (local == game.TransparentIndex) continue;
                int global = AkosFrames.Global(local, akpl, game.IsPassthroughPalette), o = (y * w + x + 13) * 4;
                if (global is > 0 and < 8) { rgba[o + 3] = 102; continue; }
                var c = game.ResolvedColors[global];
                (rgba[o], rgba[o + 1], rgba[o + 2], rgba[o + 3]) = ((byte)Math.Round(c.R * 255), (byte)Math.Round(c.G * 255), (byte)Math.Round(c.B * 255), 255);
            }
        string dir = Path.Combine(Path.GetTempPath(), "mmucs-mods-" + Guid.NewGuid().ToString("N"));
        string folder = Path.Combine(dir, "_mods", "Wide", CostumeCelMods.FolderOf(akos));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(dir, "_mods", "Wide", "mod.json"), """{ "enabled": true }""");
            using (var f = File.Create(Path.Combine(folder, "cel_048.png"))) MMUCS.Core.Imaging.PngWriter.Write(f, w, h, rgba);
            File.WriteAllText(Path.Combine(folder, "meta.json"), """{ "cels": { "cel_048": { "x": -13, "y": 0 } } }""");

            var mods = CostumeCelMods.Load(Path.Combine(dir, "COMI.LA0"));
            var data = new ScriptStore(root) { CostumeMods = mods }.Costume(140).Data;
            Assert.Empty(mods.Problems);
            var modded = Assert.Single(data.ModCels);
            Assert.Equal((48, -13, 0, w, h), (modded.Key, modded.Value.OffsetX, modded.Value.OffsetY, modded.Value.Surface.Width, modded.Value.Surface.Height));
            // Every pixel comes back as the costume's own colour (a costume can have two indices for one colour), shadows as its shadow colour.
            var surface = AkosFrames.GetCel(data, 48);
            for (int y = 0; y < cel.Height; y++)
                for (int x = 0; x < cel.Width; x++)
                {
                    byte want = cel.Pixels[y * cel.Pitch + x], got = surface.Pixels[y * surface.Pitch + x + 13];
                    int gw = AkosFrames.Global(want, akpl, game.IsPassthroughPalette), gg = AkosFrames.Global(got, akpl, game.IsPassthroughPalette);
                    if (want == game.TransparentIndex) Assert.Equal(game.TransparentIndex, got);
                    else if (gw is > 0 and < 8) Assert.InRange(gg, 1, 7);
                    else Assert.Equal(game.ResolvedColors[gw], game.ResolvedColors[gg]);
                }
            Assert.Equal((-13, 0), CostumeCelMods.Place(cel, surface, game.TransparentIndex) is { } at ? (at.X, at.Y) : (99, 99));

            // The throw's last frame (AKSQ 472, cel 48 at -552,-185) reaches 13 px further left; the game's costume doesn't.
            Assert.Equal(-565, AkosFrames.ComposeAt(data, [472]).Bounds.Left);
            Assert.Equal(-552, AkosFrames.ComposeAt(game, [472]).Bounds.Left);
            // A frame composed from the game's cels, as the viewers keep them, moved onto the mod's; the cel panel's cache gives the mod's cel.
            Assert.Equal(-565, AkosFrames.WithModCels(data, AkosFrames.ComposeAt(game, [472])).Bounds.Left);
            // A cel alone is where the first step drawing it puts it: cel 48 at the throw's -552,-185, moved by the mod's offset.
            var alone = Assert.Single(AkosFrames.CelInPlace(data, 48).Cels);
            Assert.Equal((-565, -185, w), (alone.X, alone.Y, alone.Width));
            Assert.Equal((-552, -185), (Assert.Single(AkosFrames.CelInPlace(game, 48).Cels).X, AkosFrames.CelInPlace(game, 48).Cels[0].Y));
            Assert.Same(surface, AkosCelCache.GetCachedCelAsync(data, 48, bakeImages: false, cacheDisabled: true, default).GetAwaiter().GetResult().Surface);
        }
        finally { Directory.Delete(dir, true); }
    }
}
