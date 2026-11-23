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
}
