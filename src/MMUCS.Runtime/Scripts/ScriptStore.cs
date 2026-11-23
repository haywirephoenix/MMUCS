using System;
using System.Collections.Generic;
using MMUCS.Core.Scumm;

namespace MMUCS.Runtime.Scripts;

/// <summary>What kind of code a script is, which decides how it's found and where its variables live.</summary>
public enum ScriptKind
{
    /// <summary>A global script (SCRP), numbered below <see cref="ScriptStore.FirstLocal"/>, found through DSCR.</summary>
    Global,
    /// <summary>A room's local script (LSCR), numbered from <see cref="ScriptStore.FirstLocal"/>.</summary>
    Local,
    /// <summary>A room's entry code (ENCD), run when the room is entered.</summary>
    RoomEntry,
    /// <summary>A room's exit code (EXCD), run when the room is left.</summary>
    RoomExit,
    /// <summary>An object's verb code (OBCD / VERB), entered at one verb's offset.</summary>
    Object,
}

/// <summary>
/// One script's bytecode and where it starts. <see cref="Code"/> is the block's data, so jump targets
/// and <see cref="Entry"/> are offsets into it, as the disassembler counts them.
/// </summary>
public sealed class ScriptCode
{
    public ScriptKind Kind { get; init; }
    /// <summary>The script number (global or local), the object id, or the room number for entry and exit code.</summary>
    public int Number { get; init; }
    /// <summary>The room the code lives in (a global script's home room).</summary>
    public int Room { get; init; }
    public ReadOnlyMemory<byte> Code { get; init; }
    /// <summary>The first instruction's offset in <see cref="Code"/>.</summary>
    public int Entry { get; init; }
    /// <summary>What the script is called, or null when it has no name yet.</summary>
    public string Name { get; init; }

    public override string ToString()
    {
        string what = Kind switch
        {
            ScriptKind.Global => $"global {Number}",
            ScriptKind.Local => $"room {Room} local {Number}",
            ScriptKind.RoomEntry => $"room {Room} entry",
            ScriptKind.RoomExit => $"room {Room} exit",
            _ => $"room {Room} object {Number}",
        };
        return Name == null ? what : $"{what} ({Name})";
    }
}

/// <summary>
/// An object's code: its VERB table and the data the entries point into. <see cref="EntryFor"/> picks
/// the entry a verb runs, as the engine does.
/// </summary>
public sealed class ObjectCode
{
    public int Id { get; init; }
    public int Room { get; init; }
    public string Name { get; init; }
    /// <summary>The VERB block's data: the table, then the code.</summary>
    public ReadOnlyMemory<byte> Code { get; init; }
    /// <summary>Where <see cref="Code"/> starts in the object's OBCD block (the VERB block's offset there plus its header).</summary>
    public int CodeStart { get; init; }
    /// <summary>(verb, offset into <see cref="Code"/>) in table order; verb −1 is the default.</summary>
    public IReadOnlyList<(int Verb, int Offset)> Verbs { get; init; } = [];

    /// <summary>
    /// The offset of the code <paramref name="verb"/> runs, or null when the object doesn't take it. The
    /// table is read in order and the default (−1) matches any verb, so a default listed before a verb
    /// wins over it (ScummVM <c>getVerbEntrypoint</c>, v8).
    /// </summary>
    public int? EntryFor(int verb)
    {
        foreach (var (v, offset) in Verbs)
            if (v == verb || v == -1) return offset;
        return null;
    }

    /// <summary>The code for <paramref name="verb"/> as a script, or null.</summary>
    public ScriptCode Script(int verb)
    {
        return EntryFor(verb) is int entry
            ? new ScriptCode { Kind = ScriptKind.Object, Number = Id, Room = Room, Code = Code, Entry = entry, Name = Name }
            : null;
    }
}

/// <summary>
/// Finds the game's scripts by the numbers the scripts use: global scripts through the DSCR directory,
/// local scripts, entry and exit code and object code by room. One instance per loaded game; nothing is
/// shared between instances, so several can run side by side.
/// </summary>
public sealed class ScriptStore
{
    /// <summary>Script numbers from here on are a room's local scripts (ScummVM <c>_numGlobalScripts</c> for v8).</summary>
    public const int FirstLocal = 2000;

    private readonly Dictionary<int, ScummBlock> _rooms = [];
    private readonly IReadOnlyList<(int Room, long Offset)> _directory;
    private readonly Dictionary<int, ScriptCode> _globals = [];
    private readonly Dictionary<int, RoomCode> _roomCode = [];

    private sealed class RoomCode
    {
        public readonly Dictionary<int, ScriptCode> Locals = [];
        public readonly Dictionary<int, ObjectCode> Objects = [];
        public ScriptCode Entry, Exit;
    }

    public ScriptStore(ScummBlock root)
    {
        ArgumentNullException.ThrowIfNull(root);
        foreach (var lflf in Rooms(root))
            if (RoomNo(lflf) is int no) _rooms.TryAdd(no, lflf);
        _directory = RoomScripts.GlobalScripts(root);
    }

    private ScriptStore(IReadOnlyList<(int Room, long Offset)> directory)
    {
        _directory = directory;
    }

    /// <summary>
    /// A store holding just <paramref name="scripts"/> (global and local ones), for running code that
    /// isn't from a game file, such as tests.
    /// </summary>
    public static ScriptStore Of(params ScriptCode[] scripts) => Of(scripts, []);

    /// <summary>A store holding just <paramref name="scripts"/> and the object code <paramref name="objects"/>, by their rooms.</summary>
    public static ScriptStore Of(IReadOnlyList<ScriptCode> scripts, IReadOnlyList<ObjectCode> objects)
    {
        int globals = 1;
        foreach (var s in scripts)
            if (s.Kind == ScriptKind.Global) globals = Math.Max(globals, s.Number + 1);
        var store = new ScriptStore(new (int, long)[globals]);
        foreach (var s in scripts)
        {
            if (s.Kind == ScriptKind.Global) store._globals[s.Number] = s;
            else if (s.Kind == ScriptKind.Local)
            {
                if (!store._roomCode.TryGetValue(s.Room, out var room)) store._roomCode[s.Room] = room = new RoomCode();
                room.Locals[s.Number] = s;
            }
            else throw new ArgumentException($"{s.Kind} code can't be added on its own", nameof(scripts));
        }
        foreach (var o in objects)
        {
            if (!store._roomCode.TryGetValue(o.Room, out var room)) store._roomCode[o.Room] = room = new RoomCode();
            room.Objects[o.Id] = o;
        }
        return store;
    }

    /// <summary>The numbers of the rooms in the game.</summary>
    public IEnumerable<int> RoomNumbers => _rooms.Keys;

    /// <summary>How many global script numbers the directory has (0 is never used).</summary>
    public int GlobalCount => _directory.Count;

    /// <summary>Global script <paramref name="number"/>, or null when there is none.</summary>
    public ScriptCode Global(int number)
    {
        lock (_globals)
        {
            if (_globals.TryGetValue(number, out var cached)) return cached;
            var script = FindGlobal(number);
            _globals[number] = script;
            return script;
        }
    }

    /// <summary>Local script <paramref name="number"/> (from <see cref="FirstLocal"/>) of <paramref name="room"/>, or null.</summary>
    public ScriptCode Local(int room, int number)
    {
        return For(room)?.Locals.GetValueOrDefault(number);
    }

    /// <summary>
    /// A script by the number <c>start-script</c> uses: global below <see cref="FirstLocal"/>, otherwise
    /// local to <paramref name="currentRoom"/>.
    /// </summary>
    public ScriptCode Find(int number, int currentRoom)
    {
        return number < FirstLocal ? Global(number) : Local(currentRoom, number);
    }

    public ScriptCode RoomEntry(int room) => For(room)?.Entry;

    public ScriptCode RoomExit(int room) => For(room)?.Exit;

    /// <summary>The code of object <paramref name="id"/> in <paramref name="room"/>, or null.</summary>
    public ObjectCode Object(int room, int id)
    {
        return For(room)?.Objects.GetValueOrDefault(id);
    }

    /// <summary>The objects with code in <paramref name="room"/>.</summary>
    public IEnumerable<ObjectCode> Objects(int room)
    {
        return For(room)?.Objects.Values ?? (IEnumerable<ObjectCode>)[];
    }

    /// <summary>The local script numbers of <paramref name="room"/>.</summary>
    public IEnumerable<int> LocalNumbers(int room)
    {
        return For(room)?.Locals.Keys ?? (IEnumerable<int>)[];
    }

    private ScriptCode FindGlobal(int number)
    {
        if (number <= 0 || number >= _directory.Count) return null;
        var (room, offset) = _directory[number];
        if ((room == 0 && offset == 0) || !_rooms.TryGetValue(room, out var lflf)) return null;
        long at = lflf.Offset + offset;
        foreach (var b in Blocks.Walk(lflf, 2))
            if (b.Tag == ScummTag.SCRP && b.Offset == at)
                return new ScriptCode
                {
                    Kind = ScriptKind.Global, Number = number, Room = room, Code = b.DataMemory, Entry = 0,
                    Name = ScriptNames.Global(number),
                };
        return null;
    }

    private RoomCode For(int room)
    {
        lock (_roomCode)
        {
            if (_roomCode.TryGetValue(room, out var cached)) return cached;
            var code = _rooms.TryGetValue(room, out var lflf) ? Build(room, lflf) : null;
            _roomCode[room] = code;
            return code;
        }
    }

    private static RoomCode Build(int room, ScummBlock lflf)
    {
        var code = new RoomCode();
        foreach (var b in Blocks.Walk(lflf, 3))
        {
            switch (b.Tag)
            {
                case ScummTag.LSCR when b.DataLength >= 4:
                {
                    // v8: the number (u32), then the code (ScummVM initRoomSubBlocks).
                    int no = (int)b.DataSpan.U32LE(0);
                    code.Locals.TryAdd(no, new ScriptCode
                    {
                        Kind = ScriptKind.Local, Number = no, Room = room, Code = b.DataMemory, Entry = 4,
                        Name = ScriptNames.Local(room, no),
                    });
                    break;
                }
                case ScummTag.ENCD:
                    code.Entry ??= new ScriptCode { Kind = ScriptKind.RoomEntry, Number = room, Room = room, Code = b.DataMemory };
                    break;
                case ScummTag.EXCD:
                    code.Exit ??= new ScriptCode { Kind = ScriptKind.RoomExit, Number = room, Room = room, Code = b.DataMemory };
                    break;
                case ScummTag.OBCD when ObjectOf(room, b) is { } obj:
                    code.Objects.TryAdd(obj.Id, obj);
                    break;
            }
        }
        return code;
    }

    private static ObjectCode ObjectOf(int room, ScummBlock obcd)
    {
        var verb = obcd.FindChild(ScummTag.VERB);
        int id = RoomScripts.ObjectId(obcd.FindChild(ScummTag.CDHD));
        if (verb == null || id < 0) return null;

        // v8 VERB: (verb u32, offset u32) pairs ended by a 0 verb; offsets are into the data.
        var d = verb.DataSpan;
        var verbs = new List<(int, int)>();
        for (int p = 0; p + 8 <= d.Length; p += 8)
        {
            int v = (int)d.U32LE(p);
            if (v == 0) break;
            int offset = (int)d.U32LE(p + 4);
            if (offset >= 0 && offset < d.Length) verbs.Add((v, offset));
        }
        return new ObjectCode
        {
            Id = id, Room = room, Name = RoomScripts.ObjectName(obcd, id), Code = verb.DataMemory,
            CodeStart = verb.DataOffset - obcd.Offset, Verbs = verbs,
        };
    }

    private static int? RoomNo(ScummBlock lflf)
    {
        var meta = lflf.GetMetaDataDict();
        return meta != null && meta.TryGetValue(MetaValue.From(ScummMeta.LFLF.roomNo), out var no) ? (int)no : null;
    }

    private static IEnumerable<ScummBlock> Rooms(ScummBlock root)
    {
        foreach (var b in Blocks.Walk(root, 3))
            if (b.Tag == ScummTag.LFLF) yield return b;
    }
}
