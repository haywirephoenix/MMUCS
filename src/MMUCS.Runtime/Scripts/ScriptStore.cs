using System;
using System.Collections.Generic;
using System.Linq;
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
/// An object as a room holds it, in the room's object slots (ScummVM <c>_objs</c>): its slot (from 1, in
/// the room's OBCD order), its id, the slot of the object it belongs to and the state that object must be
/// in for this one to count (0: none), and where its image is in the room (from its IMHD; 0×0 without one).
/// </summary>
public sealed record ObjectPlace(int Slot, int Id, int Parent, int ParentState, int X, int Y, int Width, int Height)
{
    /// <summary>The way an actor at it faces, as an 8-way direction (IMHD <c>actordir</c>; ×45 for degrees).</summary>
    public int ActorDir { get; init; }

    /// <summary>Where an actor stands for each image state, relative to <see cref="X"/>,<see cref="Y"/> (IMHD hotspots).</summary>
    public IReadOnlyList<(int X, int Y)> Hotspots { get; init; } = [];

    /// <summary>
    /// Where an actor goes for it in state <paramref name="state"/> (ScummVM <c>getObjectXYPos</c>, v8): its
    /// place plus the hotspot of image <i>state</i>−1 (the first for state 0), and the way it faces there.
    /// </summary>
    public (int X, int Y, int Dir) StandAt(int state)
    {
        int i = Math.Max(0, state - 1);
        var (hx, hy) = i < Hotspots.Count ? Hotspots[i] : (0, 0);
        return (X + hx, Y + hy, ActorDir * 45);
    }

    /// <summary>Whether a point is on it, edges included (COMI's hit test, ScummVM <c>findObject</c>).</summary>
    public bool Contains(int x, int y) => X <= x && x <= X + Width && Y <= y && y <= Y + Height;
}

/// <summary>A costume the scripts can give an actor: its number, its AKOS block and what's parsed from it.</summary>
public sealed record Costume(int Number, ScummBlock Block, AkosData Data);

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
    private readonly Dictionary<int, (int, int)> _roomSizes = [];
    private readonly ScummBlock _root;
    private readonly Dictionary<int, Costume> _costumes = [];
    private Dictionary<string, int> _idsByName;

    private sealed class RoomCode
    {
        public readonly Dictionary<int, ScriptCode> Locals = [];
        public readonly Dictionary<int, ObjectCode> Objects = [];
        public readonly List<ObjectPlace> Places = [];
        public readonly Dictionary<int, byte[]> Names = [];
        public ScriptCode Entry, Exit;
    }

    public ScriptStore(ScummBlock root)
    {
        ArgumentNullException.ThrowIfNull(root);
        foreach (var lflf in Rooms(root))
            if (RoomNo(lflf) is int no) _rooms.TryAdd(no, lflf);
        _directory = RoomScripts.GlobalScripts(root);
        _root = root;
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

    /// <summary>
    /// A store holding just <paramref name="scripts"/> (global, local, room entry and exit code) and the
    /// object code <paramref name="objects"/>, by their rooms, with the rooms' sizes.
    /// </summary>
    public static ScriptStore Of(IReadOnlyList<ScriptCode> scripts, IReadOnlyList<ObjectCode> objects,
        IReadOnlyDictionary<int, (int Width, int Height)> roomSizes = null, IReadOnlyDictionary<int, IReadOnlyList<ObjectPlace>> places = null)
    {
        int globals = 1;
        foreach (var s in scripts)
            if (s.Kind == ScriptKind.Global) globals = Math.Max(globals, s.Number + 1);
        var store = new ScriptStore(new (int, long)[globals]);
        foreach (var s in scripts)
        {
            if (s.Kind == ScriptKind.Global) store._globals[s.Number] = s;
            else if (s.Kind == ScriptKind.Object) throw new ArgumentException("object code goes in objects", nameof(scripts));
            else
            {
                if (!store._roomCode.TryGetValue(s.Room, out var room)) store._roomCode[s.Room] = room = new RoomCode();
                if (s.Kind == ScriptKind.Local) room.Locals[s.Number] = s;
                else if (s.Kind == ScriptKind.RoomEntry) room.Entry = s;
                else room.Exit = s;
            }
        }
        foreach (var (room, size) in roomSizes ?? new Dictionary<int, (int, int)>()) store._roomSizes[room] = size;
        foreach (var (room, list) in places ?? new Dictionary<int, IReadOnlyList<ObjectPlace>>())
        {
            if (!store._roomCode.TryGetValue(room, out var code)) store._roomCode[room] = code = new RoomCode();
            code.Places.AddRange(list);
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

    /// <summary>
    /// The room's size in pixels from its RMHD (v8: version, width, height, object count… as u32), or null
    /// for a room that isn't in the game.
    /// </summary>
    public (int Width, int Height)? RoomSize(int room)
    {
        if (_roomSizes.TryGetValue(room, out var size)) return size;
        if (!_rooms.TryGetValue(room, out var lflf)) return null;
        foreach (var b in Blocks.Walk(lflf, 2))
            if (b.Tag == ScummTag.RMHD && b.DataLength >= 12)
                return ((int)b.DataSpan.U32LE(4), (int)b.DataSpan.U32LE(8));
        return null;
    }

    /// <summary>Costume <paramref name="number"/> (its AKOS, through DCOS), or null without one or a game file.</summary>
    public Costume Costume(int number)
    {
        if (_root == null || number <= 0) return null;
        lock (_costumes)
        {
            if (_costumes.TryGetValue(number, out var cached)) return cached;
            Costume costume = null;
            if (RoomActors.CostumeBlock(_root, number) is { } akos)
            {
                try { costume = new Costume(number, akos, AkosParser.Parse(akos)); }
                catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or NullReferenceException) { }
            }
            return _costumes[number] = costume;
        }
    }

    /// <summary>The room's walk boxes (box 0 is "no box") and scale slots, as the room file has them.</summary>
    public (IReadOnlyList<Walkbox> Boxes, IReadOnlyList<ScaleSlot> Scales) RoomBoxes(int room)
    {
        if (!_rooms.TryGetValue(room, out var lflf)) return ([], []);
        return (Walkboxes.Parse(lflf.FindChildRecursive(ScummTag.BOXD)), Walkboxes.ParseScaleSlots(lflf.FindChildRecursive(ScummTag.SCAL)));
    }

    /// <summary>The room's LFLF block, or null.</summary>
    public ScummBlock RoomBlock(int room) => _rooms.GetValueOrDefault(room);

    /// <summary>A global object's id by its name in the object table (DOBJ), or 0.</summary>
    public int ObjectId(string name) => name != null && IdsByName().TryGetValue(name, out int id) ? id : 0;

    /// <summary>The room's name from the index (RNAM), or null.</summary>
    public string RoomName(int room)
    {
        if (!_rooms.TryGetValue(room, out var lflf)) return null;
        var meta = lflf.GetMetaDataDict();
        return meta != null && meta.TryGetValue(MetaValue.From(ScummMeta.LFLF.roomName), out var name) ? (string)name : null;
    }

    /// <summary>Object <paramref name="id"/>'s name as its room has it (OBNA, tag and all), or null.</summary>
    public byte[] ObjectName(int room, int id) => For(room)?.Names.GetValueOrDefault(id);

    /// <summary>The room's objects in its object slots (empty for no room).</summary>
    public IReadOnlyList<ObjectPlace> Places(int room)
    {
        return For(room)?.Places ?? (IReadOnlyList<ObjectPlace>)[];
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
            if (code != null) AddPlaces(code, lflf);
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
                case ScummTag.OBCD:
                    if (ObjectOf(room, b) is { } obj) code.Objects.TryAdd(obj.Id, obj);
                    // The name the player sees (OBNA): "/CNNZ069/cannon", a tag and the text, ended by a 0.
                    if (b.FindChild(ScummTag.CDHD) is { DataLength: >= 6 } cdhd && b.Children.FirstOrDefault(c => c.TagName == "OBNA") is { } obna)
                        code.Names.TryAdd(cdhd.DataSpan.U16LE(4), obna.DataSpan.ToArray());
                    break;
            }
        }
        return code;
    }

    /// <summary>
    /// The room's object slots (ScummVM <c>resetRoomObjects</c>, v8): one per OBCD in file order, its id,
    /// parent and parent state from CDHD (version u32, id u16, parent, parent state); then each OBIM is
    /// matched by the name in its IMHD (v8 images have no id) and gives the place (x, y, width, height as
    /// u32 at 48–60).
    /// </summary>
    private void AddPlaces(RoomCode code, ScummBlock lflf)
    {
        var rects = new Dictionary<int, (int, int, int, int)>();
        var stands = new Dictionary<int, (int Dir, (int, int)[] Hotspots)>();
        foreach (var b in Blocks.Walk(lflf, 3))
        {
            if (b.Tag != ScummTag.OBIM || b.FindChild(ScummTag.IMHD) is not { DataLength: >= 64 } imhd) continue;
            var d = imhd.DataSpan;
            int len = d[..32].IndexOf((byte)0);
            string name = System.Text.Encoding.Latin1.GetString(d[..(len < 0 ? 32 : len)]);
            if (!IdsByName().TryGetValue(name, out int id) || !rects.TryAdd(id, (d.S32LE(48), d.S32LE(52), d.S32LE(56), d.S32LE(60)))) continue;
            // v8 IMHD: version at 40 (801; 800 in the demo, without the flags), actordir at 64, then 15 hotspots.
            int at = d.S32LE(40) == 800 ? 68 : 72;
            var hotspots = new List<(int, int)>();
            for (int i = 0; i < 15 && at + 8 * i + 8 <= d.Length; i++) hotspots.Add((d.S32LE(at + 8 * i), d.S32LE(at + 8 * i + 4)));
            stands[id] = (d.Length >= 68 ? d.S32LE(64) : 0, [.. hotspots]);
        }
        int slot = 1;
        foreach (var b in Blocks.Walk(lflf, 3))
        {
            if (b.Tag != ScummTag.OBCD || b.FindChild(ScummTag.CDHD) is not { DataLength: >= 8 } cdhd) continue;
            var d = cdhd.DataSpan;
            int id = d.U16LE(4);
            var (x, y, w, h) = rects.GetValueOrDefault(id);
            var (dir, hotspots) = stands.GetValueOrDefault(id, (0, []));
            code.Places.Add(new ObjectPlace(slot++, id, d[6], d[7], x, y, w, h) { ActorDir = dir, Hotspots = hotspots });
        }
    }

    private Dictionary<string, int> IdsByName()
    {
        if (_idsByName != null) return _idsByName;
        var map = new Dictionary<string, int>();
        if (_root != null)
            foreach (var o in RoomDataExport.ReadObjectTable(_root))
                if (!string.IsNullOrEmpty(o.Name)) map.TryAdd(o.Name, o.Id);
        return _idsByName = map;
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
