using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MMUCS.Core.Scumm;

/// <summary>When the game puts a costume in the room, as far as the scripts show.</summary>
public enum PlacementTime
{
    /// <summary>Not worked out (no placement read).</summary>
    Unknown = 0,
    /// <summary>As the room opens: the entry script, or a local script it starts, at top level.</summary>
    OnEntry = 1,
    /// <summary>As the room opens, but inside a condition: the room's residents in some game state.</summary>
    OnEntryIf = 2,
    /// <summary>Inside a cut-scene block.</summary>
    Cutscene = 3,
    /// <summary>Some other time: an object's verb, a script started from elsewhere, the exit script.</summary>
    Later = 4,
}

/// <summary>
/// Works out <see cref="PlacementTime"/> for positions in a room's scripts. A script runs as the room
/// opens when it's the entry script or a local script the entry chain starts at top level; a line in it
/// counts as unconditional at top level (indent 0). A line inside a cut-scene block is a cutscene
/// wherever it is. Positions are byte offsets into a script block's data, as <see cref="RoomActors"/>
/// scans them; each maps to the decompiled line it belongs to.
/// </summary>
internal sealed class PlacementTiming
{
    private static readonly Regex StartLocal = new(@"^(?:start-script|start-script-quick|chain-script)\b(?: [a-z]+)* (\d+)\b", RegexOptions.Compiled);

    private readonly Dictionary<ScummBlock, List<ScriptLine>> _lines = new();
    private readonly HashSet<ScummBlock> _onEntry = new();
    private readonly Dictionary<ScummBlock, List<Branch>> _branches = new();
    private readonly int _room;

    public PlacementTiming(IEnumerable<ScummBlock> scripts, int roomNo = -1)
    {
        _room = roomNo;
        var all = scripts.ToList();
        var locals = new Dictionary<int, ScummBlock>();
        foreach (var b in all.Where(b => b.Tag == ScummTag.LSCR && b.DataLength >= 4))
            locals.TryAdd((int)b.DataSpan.U32LE(0), b);

        void Walk(ScummBlock b)
        {
            if (b == null || !_onEntry.Add(b)) return;
            foreach (var line in Lines(b).Where(l => l.Indent == 0))
                if (StartLocal.Match(line.Text.Trim()) is { Success: true } m && int.TryParse(m.Groups[1].Value, out int no)
                    && no >= 2000 && locals.TryGetValue(no, out var local))
                    Walk(local);
        }
        Walk(all.FirstOrDefault(b => b.Tag == ScummTag.ENCD));
    }

    public bool RunsOnEntry(ScummBlock script) => _onEntry.Contains(script);

    private List<ScriptLine> Lines(ScummBlock b)
    {
        if (_lines.TryGetValue(b, out var lines)) return lines;
        try { lines = RoomScripts.For(b) is { } e ? RoomScripts.Decompile(e) : new(); }
        catch { lines = new(); } // a script that doesn't decompile: its lines can't be placed
        return _lines[b] = lines;
    }

    /// <summary>When code at these positions of a script runs: the latest of them (a costume set on entry
    /// but placed inside an if is placed under that condition).</summary>
    public PlacementTime At(ScummBlock script, params int[] positions)
    {
        var lines = Lines(script);
        var worst = PlacementTime.Unknown;
        foreach (int pos in positions.Where(p => p >= 0))
        {
            var time = TimeAt(lines, RunsOnEntry(script), pos);
            if (time > worst) worst = time;
        }
        return worst == PlacementTime.Unknown ? (RunsOnEntry(script) ? PlacementTime.OnEntryIf : PlacementTime.Later) : worst;
    }

    /// <summary>
    /// Whether code at these positions runs on a fresh game's first visit, as far as the conditions around
    /// it can be read: every bit variable is 0 then, and the current room is this room; other variables
    /// and function results aren't known, and an unknown condition doesn't hold anything back. False when
    /// a condition known to fail skips any of them (clring-a's "if (bit43)" puts Haggis there only once
    /// the caber toss has happened).
    /// </summary>
    public bool Reached(ScummBlock script, params int[] positions)
    {
        if (!_branches.TryGetValue(script, out var branches))
        {
            branches = RoomScripts.For(script) is { Parts.Count: > 0 } e
                ? Branches(script.DataSpan, e.Parts[0].Start, e.Parts[0].End, _room) : new();
            _branches[script] = branches;
        }
        return positions.Where(p => p >= 0).All(p => Reached(branches, p));
    }

    /// <summary>A conditional jump: where it is, where the code after it starts, where it goes, whether it's
    /// taken on a fresh game (null when that isn't known), and where an else part it skips to ends.</summary>
    internal readonly record struct Branch(int At, int Next, int Target, bool? Taken, int ElseEnd);

    /// <summary>Whether code at a position surely runs on a fresh game: every condition around it is
    /// known, and holds (the then-part of a branch not taken, the else part of one taken).</summary>
    internal static bool Surely(IReadOnlyList<Branch> branches, int pos)
    {
        foreach (var b in branches)
        {
            if (pos >= b.Next && pos < b.Target && b.Taken != false) return false;
            if (pos >= b.Target && pos < b.ElseEnd && b.Taken != true) return false;
        }
        return true;
    }

    internal static bool Reached(IReadOnlyList<Branch> branches, int pos)
    {
        foreach (var b in branches)
        {
            if (b.Taken == true && pos >= b.Next && pos < b.Target) return false;   // the part it jumps over
            if (b.Taken == false && pos >= b.Target && pos < b.ElseEnd) return false; // the else part it doesn't reach
        }
        return true;
    }

    /// <param name="classes">An object's starting classes (bit N-1 for class N), or null when not known;
    /// with it, "class-of(OBJ) is [..]" is read as the engine does.</param>
    internal static List<Branch> Branches(ReadOnlySpan<byte> code, int start, int end, int room, Func<int, uint?> classes = null)
    {
        var list = new List<Branch>();
        List<ScriptInstruction> ins;
        try { ins = ScummScript.Disassemble(code, start, end); }
        catch { return list; }
        var stack = new List<int?>();
        int? Pop()
        {
            if (stack.Count == 0) return null;
            var v = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            return v;
        }
        static int? Bin(int? a, int? b, Func<int, int, int> f) => a is int x && b is int y ? f(x, y) : null;
        var jumps = ins.Where(i => i.Opcode == 0x66).ToDictionary(i => i.Offset + i.Length, i => i.Target);
        foreach (var i in ins)
        {
            if (i.Unknown) break;
            switch (i.Opcode)
            {
                case 0x01: stack.Add(i.Operand); break;
                case 0x02:
                {
                    uint v = (uint)i.Operand;
                    // A bit variable is 0 on a fresh game; current-room (31) is this room.
                    stack.Add((v & 0x80000000) != 0 ? 0 : v == 31 && room >= 0 ? room : null);
                    break;
                }
                case 0x05: { var v = Pop(); stack.Add(v); stack.Add(v); break; }
                case 0x06: Pop(); break;
                case 0x07: { var v = Pop(); stack.Add(v is int x ? (x == 0 ? 1 : 0) : null); break; }
                case >= 0x08 and <= 0x16:
                {
                    var b = Pop();
                    var a = Pop();
                    // land / lor with one side known can still be decided.
                    if (i.Opcode == 0x12 && (a == 0 || b == 0)) { stack.Add(0); break; }
                    if (i.Opcode == 0x13 && ((a is int la && la != 0) || (b is int lb && lb != 0))) { stack.Add(1); break; }
                    stack.Add(i.Opcode switch
                    {
                        0x08 => Bin(a, b, (x, y) => x == y ? 1 : 0), 0x09 => Bin(a, b, (x, y) => x != y ? 1 : 0),
                        0x0a => Bin(a, b, (x, y) => x > y ? 1 : 0), 0x0b => Bin(a, b, (x, y) => x < y ? 1 : 0),
                        0x0c => Bin(a, b, (x, y) => x <= y ? 1 : 0), 0x0d => Bin(a, b, (x, y) => x >= y ? 1 : 0),
                        0x0e => Bin(a, b, (x, y) => x + y), 0x0f => Bin(a, b, (x, y) => x - y), 0x10 => Bin(a, b, (x, y) => x * y),
                        0x12 => Bin(a, b, (x, y) => x != 0 && y != 0 ? 1 : 0), 0x13 => Bin(a, b, (x, y) => x != 0 || y != 0 ? 1 : 0),
                        _ => null,
                    });
                    break;
                }
                case 0xd0:
                {
                    // ifClassOfIs: the list (its items, then their count) above the object. A class
                    // without 0x80 asks that the object doesn't have it, with 0x80 that it does
                    // (ScummVM's o6_ifClassOfIs).
                    var n = Pop();
                    var tested = new List<int?>();
                    for (int k = 0; k < (n ?? 0) && k < 32; k++) tested.Add(Pop());
                    var obj = Pop();
                    if (n == null || obj is not int o || classes?.Invoke(o) is not uint bits
                        || tested.Any(c => c is not int x || (x & 0x7F) is < 1 or > 32)) { stack.Add(null); break; }
                    bool holds = tested.All(c => ((bits & (1u << (((int)c & 0x7F) - 1))) != 0) == (((int)c & 0x80) != 0));
                    stack.Add(holds ? 1 : 0);
                    break;
                }
                case 0x64 or 0x65:
                {
                    var c = Pop();
                    bool? taken = c is int x ? (x != 0) == (i.Opcode == 0x64) : null;
                    int next = i.Offset + i.Length;
                    // An if's then-part ending in a jump over an else part.
                    int elseEnd = i.Target > next && jumps.TryGetValue(i.Target, out int over) && over > i.Target ? over : i.Target;
                    if (i.Target > next) list.Add(new Branch(i.Offset, next, i.Target, taken, elseEnd));
                    stack.Clear();
                    break;
                }
                default:
                {
                    // Anything else: pops what it takes, and pushes an unknown result if it has one.
                    int pops = i.Def?.Args?.Count(ch => ch == 'a') ?? 0;
                    if (i.Def?.Args?.Any(ch => ch != 'a') == true) stack.Clear();
                    else for (int k = 0; k < pops; k++) Pop();
                    if (i.Def?.Push == true) stack.Add(null);
                    break;
                }
            }
        }
        return list;
    }

    /// <summary>When the statement at a position runs, from its script's decompiled lines.</summary>
    internal static PlacementTime TimeAt(IReadOnlyList<ScriptLine> lines, bool scriptRunsOnEntry, int pos)
    {
        // The statement a position belongs to: the last line starting at or before it.
        int at = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Offset < 0) continue;
            if (lines[i].Offset > pos) break;
            at = i;
        }
        if (at < 0) return PlacementTime.Unknown;
        // The blocks around it: each earlier line indented less than the one inside it.
        int indent = lines[at].Indent;
        for (int j = at - 1; j >= 0 && indent > 0; j--)
        {
            if (lines[j].Indent >= indent) continue;
            if (lines[j].Text.TrimStart().StartsWith("cut-scene")) return PlacementTime.Cutscene;
            indent = lines[j].Indent;
        }
        if (!scriptRunsOnEntry) return PlacementTime.Later;
        return lines[at].Indent == 0 ? PlacementTime.OnEntry : PlacementTime.OnEntryIf;
    }
}
