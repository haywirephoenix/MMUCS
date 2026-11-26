using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace MMUCS.Core.Scumm;

/// <summary>
/// One of a room's costumes: the actor the room's scripts dress in it and where they put it (when
/// that can be read), and the object (hotspot) it belongs to, matched by overlap or linked by hand.
/// </summary>
public sealed class RoomActor
{
    /// <summary>
    /// Actor number from the scripts, or 0 when no script sets this costume with literals. A costume can
    /// have an actor and no position: a script dresses the actor but places it through variables.
    /// </summary>
    public int Actor { get; init; }
    /// <summary>
    /// Every actor this entry stands for, when one script dresses several actors in this costume on
    /// the same spot to layer its chores (banjo's strings, one actor each); otherwise just <see cref="Actor"/>.
    /// </summary>
    public IReadOnlyList<int> Actors { get; init; }
    public int Costume { get; init; }
    public ScummBlock Akos { get; init; }
    /// <summary>Actor origin in the room (valid when <see cref="HasPosition"/>).</summary>
    public int X { get; init; }
    public int Y { get; init; }
    /// <summary>
    /// How far above <see cref="Y"/> the actor is drawn (actorOps elevation). Scripts use it to draw an
    /// actor high on screen while a large Y keeps it in front of the others (cannon-v's cannon).
    /// </summary>
    public int Elevation { get; init; }
    /// <summary>Where the actor's origin is drawn: <see cref="Y"/> lifted by <see cref="Elevation"/>.</summary>
    public int DrawY => Y - Elevation;
    /// <summary>
    /// Actors the script that places this costume takes out of the room for a while as it plays (put-actor B
    /// in room 0, then back): the costume draws them meanwhile (barber's costume 122 draws Bill getting up,
    /// so the script removes Bill's actor and puts it back after).
    /// </summary>
    public IReadOnlyList<int> Removes { get; init; } = [];
    /// <summary>
    /// The y it's drawn in order of: <see cref="Y"/>, as the engine sorts, except that a costume is drawn in
    /// front of the actors it <see cref="Removes"/>. The two only share the screen for the moment before the
    /// script removes them, so the viewer shows the costume as it plays.
    /// </summary>
    public int SortY { get; set; }
    /// <summary>actorOps scale: 255 is full size. The frame is scaled about the actor's origin.</summary>
    public int Scale { get; init; } = 255;
    public float ScaleFactor => Math.Clamp(Scale, 1, 255) / 255f;
    /// <summary>
    /// The z-plane that masks it (0 for none): its always-zclip plane, or with the default (and after
    /// ignore-boxes / follow-boxes) the mask of the walkbox it stands in, as the engine does.
    /// </summary>
    public int ZPlane { get; init; }
    public bool HasPosition { get; init; }
    public AkosData Data { get; init; }
    /// <summary>The frame shown in the room (the stand/init chore), or null when it draws nothing.</summary>
    public AkosFrame Frame { get; init; }
    /// <summary>Drawn flipped, as the engine draws a costume facing west (<see cref="AkosFrames.IsMirrored"/>).</summary>
    public bool Mirrored { get; init; }
    /// <summary>The object this costume belongs to, if any.</summary>
    public RoomObject Object { get; set; }
    /// <summary>True when <see cref="Object"/> comes from a manual link rather than overlap.</summary>
    public bool IsLinked { get; set; }
    /// <summary>
    /// For an actor whose other costume is matched to an object: that object (the character it
    /// plays). It names this costume too, without claiming the hotspot.
    /// </summary>
    public RoomObject Character { get; set; }
    /// <summary>
    /// The script that dressed it, when the room's script passed the actor to another script that sets
    /// the costume (start-script 88(local2, …)); -1 when the costume is set where it's placed.
    /// </summary>
    public int DressedBy { get; init; } = -1;
    /// <summary>
    /// When the game puts it in the room: as the room opens (unconditionally or under a condition), in a
    /// cutscene, or later. A costume placed several ways counts its earliest.
    /// </summary>
    public PlacementTime When { get; set; }
    /// <summary>
    /// Whether it's there as the room opens: placed on entry, not the player's actor, and the one costume
    /// its actor starts in when the setup offers several (Bill's 112 or 114 by condition).
    /// </summary>
    public bool StartsShown { get; set; }
    /// <summary>
    /// Only there for a moment: every script that puts it here takes its actor out of the room again
    /// (put-actor A in-room 0) before the actor's next costume, unconditionally (McMutton's arms in
    /// gb-deck's idle loop, put for 21 frames now and then). Never shown at the start.
    /// </summary>
    public bool Passing { get; set; }
    /// <summary>
    /// Put in the room drawing nothing: the pose it's put in is empty, and the frame shown is borrowed from
    /// an animation another script plays on it later (smglcave's diamond sparkle, once the diamond is on
    /// the table). Never shown at the start.
    /// </summary>
    public bool StartsBlank { get; set; }
    /// <summary>
    /// Whether a fresh game's first visit reaches a placement of it on entry: no condition around it is
    /// known to fail with every bit variable at 0. False keeps it hidden at the start (residents that
    /// only turn up once the story has moved on).
    /// </summary>
    public bool FirstVisit { get; set; }
    /// <summary>The player's actor (dressed in most rooms): never matched to a hotspot by overlap.</summary>
    public bool IsPlayer { get; init; }

    /// <summary>The object that names this costume: its own, or its character's.</summary>
    public RoomObject Owner => Object ?? Character;

    public bool CanDraw => HasPosition && Frame is { IsEmpty: false };

    /// <summary>Frame bounds in room coordinates (when it can be drawn).</summary>
    public (int Left, int Top, int Right, int Bottom)? RoomBounds
    {
        get
        {
            if (!CanDraw) return null;
            var (Left, Top, Right, Bottom) = Frame.Bounds;
            float f = ScaleFactor;
            return ((int)(Left * f) + X, (int)(Top * f) + DrawY, (int)Math.Ceiling(Right * f) + X, (int)Math.Ceiling(Bottom * f) + DrawY);
        }
    }

    private string ActorText => Actors is { Count: > 1 } all ? $"actors {string.Join(", ", all)}" : $"actor {Actor}";

    public string Label => Actor <= 0 ? $"costume {Costume}{(DressedBy >= 0 ? $"  (dressed by script {DressedBy})" : "")}"
        : IsPlayer ? $"costume {Costume}  (player, {ActorText})"
        : Character != null && Object == null ? $"costume {Costume}  ({ActorText}, {Character.Name})"
        : $"costume {Costume}  ({ActorText})";

    public override string ToString()
    {
        return HasPosition ? $"{Label} at {X},{Y}{(Elevation != 0 ? $" elevation {Elevation}" : "")}" : Label;
    }
}

/// <summary>
/// Finds the costumes of a room and the objects they belong to. Objects don't reference costumes:
/// the room's scripts set an actor's costume (actorOps setCurActor / setCostume) and put it in the
/// room (putActorAtXY). This reads those calls when their arguments are literal pushes, resolves
/// costume numbers through the index's DCOS directory, and pairs each actor with the hotspot its
/// frame covers. Manual links (room / object name → costume) override the matching.
/// </summary>
public static partial class RoomActors
{
    private const byte OpPushWord = 0x01, OpPushVar = 0x02, OpPutActorAtXY = 0xA1, OpAnimateActor = 0xA4, OpActorOps = 0xAC;
    private const byte SubSetCostume = 0x64, SubDefault = 0x6D, SubElevation = 0x6E, SubScale = 0x73, SubNeverZClip = 0x74,
        SubAlwaysZClip = 0x75, SubIgnoreBoxes = 0x76, SubFollowBoxes = 0x77, SubSetCurActor = 0x7A, SubFace = 0x81;

    /// <summary>The engine's "use the walkbox's mask" clip value (v8 scripts write 255 for it).</summary>
    public const int ClipByBox = 100;

    /// <summary>
    /// Ops that change an actor's z-clip, as ScummVM's o8_actorOps: default and follow-boxes set it to
    /// <see cref="ClipByBox"/> and follow the boxes; ignore-boxes does too but leaves the boxes; never-zclip
    /// sets 0 (no mask); always-zclip N sets N (a byte: 255 and -1 mean by box). (position, actor key, clip or
    /// null, ignore-boxes or null).
    /// </summary>
    private static List<(int Pos, int Actor, int? Clip, bool? Ignore)> ClipOps(ReadOnlySpan<byte> s)
    {
        var ops = new List<(int, int, int?, bool?)>();
        int cur = 0;
        for (int p = 5; p + 1 < s.Length; p++)
        {
            if (s[p] != OpActorOps) continue;
            byte sub = s[p + 1];
            if (sub == SubSetCurActor) { cur = ActorArg(s, p, out int a) ? a : 0; continue; }
            if (cur == 0) continue;
            switch (sub)
            {
                case SubDefault or SubFollowBoxes: ops.Add((p, cur, ClipByBox, false)); break;
                case SubIgnoreBoxes: ops.Add((p, cur, ClipByBox, true)); break;
                case SubNeverZClip: ops.Add((p, cur, 0, null)); break;
                // The engine keeps the clip in a byte, so -1 is 255 too: "by box" (logo's actor 11, McMutton).
                case SubAlwaysZClip when Const(s, p, out int n): ops.Add((p, cur, (n & 0xFF) == 255 ? ClipByBox : n & 0xFF, null)); break;
            }
        }
        return ops;
    }

    private static (int Clip, bool Ignore) FoldClip(List<(int Pos, int Actor, int? Clip, bool? Ignore)> ops, int actor, int after, int before, (int Clip, bool Ignore) state)
    {
        foreach (var (Pos, Actor, Clip, Ignore) in ops)
            if (Actor == actor && Pos > after && Pos < before)
                state = (Clip ?? state.Clip, Ignore ?? state.Ignore);
        return state;
    }

    /// <summary>The mask of a walkbox (0 when the room has no such box), as ScummVM's getMaskFromBox.</summary>
    private static int MaskOf(RoomModel room, int box)
    {
        return box >= 0 && box < room.Walkboxes.Count ? (int)(room.Walkboxes[box].Mask & 0xFF) : 0;
    }

    private const uint BoxPlayerOnly = 0x20, BoxInvisible = 0x80;

    /// <summary>
    /// Where the engine puts an actor placed at x,y that follows the boxes, and the walkbox it stands in:
    /// ScummVM's Actor::adjustXYToBeInBox (v7+). Boxes are searched from the last down to box 1 (box 0 is
    /// "no box"), skipping invisible ones; the first that holds the point wins, otherwise the actor moves
    /// to the closest point on the closest box, within 30, then 80 pixels, then anywhere. An actor that
    /// ignores the boxes stays where it's put, in box 0. Every put-actor in the current room does this
    /// (putActor → showActor / adjustActorPos), so a costume put outside the boxes is drawn on their edge.
    /// </summary>
    public static (int X, int Y, int Box) PutInBox(IReadOnlyList<Walkbox> boxes, int x, int y, bool isPlayer)
    {
        var abr = (X: x, Y: y, Box: 0);
        int numBoxes = boxes.Count - 1;
        if (numBoxes < 1) return abr;
        foreach (int threshold in new[] { 30, 80, 0 })
        {
            int bestDist = int.MaxValue, bestBox = 0;
            for (int box = numBoxes; box >= 1; box--)
            {
                var b = boxes[box];
                uint flags = b.Flags & 0xFF;
                if ((flags & BoxInvisible) != 0 && !((flags & BoxPlayerOnly) != 0 && !isPlayer)) continue;
                var q = new[] { b.UpperLeft, b.UpperRight, b.LowerRight, b.LowerLeft };
                if (threshold > 0 && QuickReject(q, x, y, threshold)) continue;
                if (InBox(q, x, y)) return (x, y, box);
                int dist = ClosestPtOnBox(q, x, y, out int tx, out int ty);
                if (dist < bestDist)
                {
                    abr.X = tx;
                    abr.Y = ty;
                    if (dist == 0) return (tx, ty, box);
                    bestDist = dist;
                    bestBox = box;
                }
            }
            if (threshold == 0 || (long)threshold * threshold >= bestDist) return (abr.X, abr.Y, bestBox);
        }
        return abr;
    }

    private static bool QuickReject(BoxPoint[] q, int x, int y, int t)
    {
        return q.All(p => x - t > p.X) || q.All(p => x + t < p.X) || q.All(p => y - t > p.Y) || q.All(p => y + t < p.Y);
    }

    /// <summary>ScummVM's compareSlope: p3 is on or to one side of the line p1 → p2.</summary>
    private static bool Slope(BoxPoint p1, BoxPoint p2, BoxPoint p3)
    {
        return (long)(p2.Y - p1.Y) * (p3.X - p1.X) <= (long)(p3.Y - p1.Y) * (p2.X - p1.X);
    }

    /// <summary>ScummVM's checkXYInBoxBounds for v7+ (corners UL, UR, LR, LL), including its line-box case.</summary>
    public static bool InBox(BoxPoint[] q, int x, int y)
    {
        if (q.All(c => x < c.X) || q.All(c => x > c.X) || q.All(c => y < c.Y) || q.All(c => y > c.Y)) return false;
        BoxPoint ul = q[0], ur = q[1], lr = q[2], ll = q[3], p = new(x, y);
        if ((ul == ur && lr == ll) || (ul == ll && ur == lr))
            if (SqrDist(p, ClosestPtOnLine(ul, lr, p)) <= 4) return true;
        if (Slope(ul, ur, p) && Slope(ur, lr, p) && Slope(lr, ll, p) && Slope(ll, ul, p)) return true;
        return Slope(ul, ll, p) && Slope(ll, lr, p) && Slope(lr, ur, p) && Slope(ur, ul, p);
    }

    private static int SqrDist(BoxPoint a, BoxPoint b)
    {
        int dx = Math.Abs(a.X - b.X), dy = Math.Abs(a.Y - b.Y);
        return dx >= 0x1000 || dy >= 0x1000 ? 0xFFFFFF : dx * dx + dy * dy;
    }

    private static int ClosestPtOnBox(BoxPoint[] q, int x, int y, out int ox, out int oy)
    {
        var p = new BoxPoint(x, y);
        int best = 0xFFFFFF;
        ox = x; oy = y;
        for (int i = 0; i < 4; i++)
        {
            var t = ClosestPtOnLine(q[i], q[(i + 1) % 4], p);
            int d = SqrDist(p, t);
            if (d < best) { best = d; ox = t.X; oy = t.Y; }
        }
        return best;
    }

    /// <summary>ScummVM's closestPtOnLine, integer arithmetic and all.</summary>
    private static BoxPoint ClosestPtOnLine(BoxPoint s, BoxPoint e, BoxPoint p)
    {
        int lx = e.X - s.X, ly = e.Y - s.Y;
        BoxPoint r;
        if (e.X == s.X) r = new(s.X, p.Y);
        else if (e.Y == s.Y) r = new(p.X, s.Y);
        else
        {
            int dist = lx * lx + ly * ly;
            if (Math.Abs(lx) > Math.Abs(ly))
            {
                int a = s.X * ly / lx, b = p.X * lx / ly, c = (a + b - s.Y + p.Y) * ly * lx / dist;
                r = new(c, c * ly / lx - a + s.Y);
            }
            else
            {
                int a = s.Y * lx / ly, b = p.Y * ly / lx, c = (a + b - s.X + p.X) * ly * lx / dist;
                r = new(c * lx / ly - a + s.X, c);
            }
        }
        if (Math.Abs(ly) < Math.Abs(lx))
        {
            if (lx > 0) { if (r.X < s.X) r = s; else if (r.X > e.X) r = e; }
            else { if (r.X > s.X) r = s; else if (r.X < e.X) r = e; }
        }
        else
        {
            if (ly > 0) { if (r.Y < s.Y) r = s; else if (r.Y > e.Y) r = e; }
            else { if (r.Y > s.Y) r = s; else if (r.Y < e.Y) r = e; }
        }
        return r;
    }

    private const byte OpAdd = 0x0E, OpSub = 0x0F, OpMul = 0x10, OpDiv = 0x11;
    private const byte OpGetObjectX = 0xEB, OpGetObjectY = 0xEC;
    /// <summary>put-actor's "in-room" for "the room it's in" (the decompiler shows in-room 2147483647).</summary>
    private const int SameRoom = int.MaxValue;

    /// <summary>
    /// An argument "x-of(A)" / "y-of(A)" (<paramref name="op"/>), optionally plus or minus a constant, ending at
    /// <paramref name="end"/>: the actor key, the offset, and where the argument starts.
    /// </summary>
    private static bool OffsetOf(ReadOnlySpan<byte> s, int end, byte op, out int actor, out int delta, out int start)
    {
        actor = delta = 0;
        start = end;
        int opAt = end - 1;
        if (end - 1 > 0 && s[end - 1] is OpAdd or OpSub && Const(s, end - 1, out int k, out int kStart) && kStart - 1 >= 0 && s[kStart - 1] == op)
        {
            delta = s[end - 1] == OpAdd ? k : -k;
            opAt = kStart - 1;
        }
        else if (end - 1 < 0 || s[end - 1] != op) return false;
        return ActorArg(s, opAt, out actor, out start);
    }

    private static readonly object Sync = new();
    private static readonly ConditionalWeakTable<ScummBlock, CostumeDirectory> Directories = [];
    private static readonly ConditionalWeakTable<ScummBlock, List<RoomActor>> Analyses = [];
    private static readonly ConditionalWeakTable<ScummBlock, StrongBox<int>> Players = [];
    /// <summary>AKOS block → name of the object it belongs to (filled as rooms are analysed).</summary>
    private static readonly ConditionalWeakTable<ScummBlock, string> AkosObjectNames = [];

    private static Dictionary<string, int> _links = [];

    /// <summary>
    /// Manual links, keyed by <see cref="LinkKey"/>: a costume number, or 0 for "no costume"
    /// (suppresses a wrong match). The dictionary is used as is, so changes made through
    /// <see cref="SetLink"/> land in the caller's (e.g. persisted) instance.
    /// </summary>
    public static Dictionary<string, int> Links
    {
        get => _links;
        set
        {
            lock (Sync)
            {
                _links = value ?? [];
                Analyses.Clear();
            }
        }
    }

    public static string LinkKey(int roomNo, string objectName)
    {
        return $"{roomNo}/{objectName}";
    }

    /// <summary>Links an object to a costume (0 = none), or back to automatic matching (null).</summary>
    public static void SetLink(RoomModel room, RoomObject obj, int? costume)
    {
        lock (Sync)
        {
            string key = LinkKey(room.RoomNo, obj.Name);
            if (costume is int c) _links[key] = c;
            else _links.Remove(key);
            Analyses.Remove(room.Lflf);
        }
    }

    public static int? GetLink(RoomModel room, RoomObject obj)
    {
        lock (Sync)
            return _links.TryGetValue(LinkKey(room.RoomNo, obj.Name), out int c) ? c : null;
    }

    /// <summary>Costume <paramref name="costume"/>'s AKOS block, through the index's DCOS (any block of the game finds it), or null.</summary>
    public static ScummBlock CostumeBlock(ScummBlock anyBlock, int costume) => costume <= 0 ? null : DirectoryFor(anyBlock).Resolve(costume, null);

    /// <summary>Costume number → AKOS block, from the index file's DCOS.</summary>
    private sealed class CostumeDirectory
    {
        private readonly Dictionary<int, (int Room, uint Offset)> _entries = [];
        private readonly Dictionary<int, ScummBlock> _lflfByRoom = [];
        private readonly Dictionary<(int Room, long Offset), int> _byLocation = [];

        public CostumeDirectory(ScummBlock root)
        {
            ScummBlock dcos = null;
            foreach (var b in Walk(root, 3))
            {
                if (b.Tag == ScummTag.DCOS) dcos ??= b;
                if (b.Tag == ScummTag.LFLF && RoomNo(b) is int no) _lflfByRoom.TryAdd(no, b);
            }
            if (dcos == null) return;

            var d = dcos.DataSpan;
            if (d.Length < 2) return;
            int n = d.U16LE(0);
            // v8 stores the count as 32 bits; older versions as 16.
            int header = d.Length >= 4 + n * 5 && d.U16LE(2) == 0 ? 4 : 2;
            if (d.Length < header + n * 5) return;
            for (int i = 0; i < n; i++)
            {
                var e = (Room: (int)d[header + i], Offset: d.U32LE(header + n + i * 4));
                _entries[i] = e;
                if (e.Room != 0 || e.Offset != 0) _byLocation.TryAdd((e.Room, e.Offset), i);
            }
        }

        public ScummBlock Resolve(int costume, ScummBlock lflf)
        {
            if (!_entries.TryGetValue(costume, out var e)) return null;
            if (!_lflfByRoom.TryGetValue(e.Room, out var target)) target = null;
            target ??= lflf;
            if (target == null) return null;
            long abs = target.Offset + e.Offset;
            foreach (var c in target.Children)
                if (c.Tag == ScummTag.AKOS && c.Offset == abs) return c;
            return null;
        }

        public int? CostumeOf(ScummBlock akos)
        {
            var lflf = akos.Parent;
            if (lflf == null || RoomNo(lflf) is not int room) return null;
            return _byLocation.TryGetValue((room, akos.Offset - lflf.Offset), out int c) ? c : null;
        }
    }

    private static int? RoomNo(ScummBlock lflf)
    {
        var meta = lflf.GetMetaDataDict();
        return meta != null && meta.TryGetValue(MetaValue.From(ScummMeta.LFLF.roomNo), out var no) ? (int)no : null;
    }

    private static IEnumerable<ScummBlock> Walk(ScummBlock b, int depth)
    {
        yield return b;
        if (depth == 0) yield break;
        foreach (var c in b.Children)
            foreach (var x in Walk(c, depth - 1))
                yield return x;
    }

    private static ScummBlock Root(ScummBlock b)
    {
        while (b.Parent != null) b = b.Parent;
        return b;
    }

    private static readonly ConditionalWeakTable<ScummBlock, Dictionary<int, IReadOnlyList<ParamCostume>>> Dressers = [];

    /// <summary>Global script number → the costumes it dresses its parameters in, for every such script in the game.</summary>
    private static Dictionary<int, IReadOnlyList<ParamCostume>> DressersFor(ScummBlock any)
    {
        lock (Sync)
            return Dressers.GetValue(Root(any), root =>
            {
                var map = new Dictionary<int, IReadOnlyList<ParamCostume>>();
                foreach (var b in Walk(root, 4).Where(b => b.Tag == ScummTag.SCRP))
                {
                    if (ParamCostumes(b.DataSpan) is not { Count: > 0 } pcs) continue;
                    var title = RoomScripts.For(b)?.Title;
                    if (title != null && title.StartsWith("Global script ") && int.TryParse(title[14..], out int no))
                        map.TryAdd(no, pcs);
                }
                return map;
            });
    }

    /// <summary>A global script's number (its SCRP's title), or null for a room's own script.</summary>
    private static int? GlobalNumber(ScummBlock script)
    {
        if (script.Tag != ScummTag.SCRP) return null;
        var title = RoomScripts.For(script)?.Title;
        return title != null && title.StartsWith("Global script ") && int.TryParse(title[14..], out int no) ? no : null;
    }

    /// <summary>Whether a script gives <paramref name="actor"/> an elevation or a default (which clears it) before <paramref name="before"/>.</summary>
    private static bool SetsElevationOrDefault(ReadOnlySpan<byte> s, int actor, int before)
    {
        int cur = 0;
        int end = before < 0 ? s.Length : Math.Min(before, s.Length);
        for (int p = 5; p + 1 < end; p++)
        {
            if (s[p] != OpActorOps) continue;
            if (s[p + 1] == SubSetCurActor) cur = ActorArg(s, p, out int a) ? a : 0;
            else if (cur == actor && s[p + 1] is SubElevation or SubDefault) return true;
        }
        return false;
    }

    private sealed record ScriptLinks(Dictionary<int, List<ScummBlock>> Starters, Dictionary<int, List<ScummBlock>> Entering);

    private static readonly ConditionalWeakTable<ScummBlock, ScriptLinks> ScriptLinkTables = [];

    /// <summary>
    /// For each global script, the rooms (LFLFs) whose scripts start or chain it; for each room number, the global
    /// scripts (SCRPs) that enter it (current-room N).
    /// </summary>
    private static ScriptLinks LinksFor(ScummBlock any)
    {
        lock (Sync)
            return ScriptLinkTables.GetValue(Root(any), root =>
            {
                var starters = new Dictionary<int, List<ScummBlock>>();
                var entering = new Dictionary<int, List<ScummBlock>>();
                foreach (var lflf in root.FindChildrenRecursive(ScummTag.LFLF))
                    foreach (var b in Walk(lflf, 4).Where(b => IsScriptTag(b.Tag)))
                    {
                        foreach (int n in StartedScripts(b.DataSpan))
                        {
                            if (!starters.TryGetValue(n, out var rooms)) starters[n] = rooms = [];
                            if (!rooms.Contains(lflf)) rooms.Add(lflf);
                        }
                        if (b.Tag != ScummTag.SCRP) continue;
                        foreach (int r in RoomsEntered(b.DataSpan).Select(e => e.Room).Distinct())
                        {
                            if (!entering.TryGetValue(r, out var globals)) entering[r] = globals = [];
                            globals.Add(b);
                        }
                    }
                return new ScriptLinks(starters, entering);
            });
    }

    private const byte OpLoadRoom = 0x9D;

    /// <summary>The rooms a script enters (current-room N, N a constant), with where.</summary>
    private static List<(int Pos, int Room)> RoomsEntered(ReadOnlySpan<byte> s)
    {
        var rooms = new List<(int, int)>();
        for (int p = 5; p < s.Length; p++)
            if (s[p] == OpLoadRoom && Const(s, p, out int room) && room > 0) rooms.Add((p, room));
        return rooms;
    }

    /// <summary>The scripts a script starts or chains to, where the number is a constant.</summary>
    private static HashSet<int> StartedScripts(ReadOnlySpan<byte> s)
    {
        var started = new HashSet<int>();
        for (int p = 10; p < s.Length; p++)
        {
            if (s[p] is not (OpStartScript or OpStartScriptQuick or OpChainScript)) continue;
            // Pushed: [flags,] script, the arguments, their count; read back to front.
            if (!Const(s, p, out int count, out int end) || count is < 0 or > 16) continue;
            bool ok = true;
            for (int i = count - 1; i >= 0 && ok; i--)
                ok = Const(s, end, out _, out int start) ? (end = start) >= 0 : ActorArg(s, end, out _, out end);
            if (ok && Const(s, end, out int script) && script > 0) started.Add(script);
        }
        return started;
    }

    [ThreadStatic] private static HashSet<ScummBlock> _carrying;

    /// <summary>
    /// The elevation <paramref name="actor"/> already has when it's placed in room <paramref name="roomNo"/> by a script
    /// that leaves it alone: the one the rooms it comes from give it as they open, when they agree; null when they
    /// don't, or give none. It comes from the rooms that start <paramref name="global"/> (the placing script, when it's
    /// a global one), and from those that start a global script entering this room without an elevation or default
    /// for the actor first.
    /// </summary>
    private static int? CarriedElevation(ScummBlock lflf, int roomNo, int? global, int actor)
    {
        var links = LinksFor(lflf);
        var from = new List<ScummBlock>();
        if (global is int g && links.Starters.GetValueOrDefault(g) is { } direct) from.AddRange(direct);
        foreach (var enter in links.Entering.GetValueOrDefault(roomNo) ?? [])
        {
            var code = enter.DataSpan;
            int at = RoomsEntered(code).First(e => e.Room == roomNo).Pos;
            if (SetsElevationOrDefault(code, actor, at) || GlobalNumber(enter) is not int n) continue;
            from.AddRange(links.Starters.GetValueOrDefault(n) ?? []);
        }
        if (from.Count == 0) return null;

        _carrying ??= [];
        if (!_carrying.Add(lflf)) return null; // rooms that lead into each other
        try
        {
            var found = new HashSet<int>();
            foreach (var starter in from.Distinct())
            {
                if (ReferenceEquals(starter, lflf) || _carrying.Contains(starter) || !RoomModel.TryCreate(starter, out var room)) continue;
                foreach (var a in Analyze(room).Where(a => a.Actor == actor && a.HasPosition && a.When is PlacementTime.OnEntry or PlacementTime.OnEntryIf))
                    found.Add(a.Elevation);
            }
            return found.Count == 1 && found.First() != 0 ? found.First() : null;
        }
        finally { _carrying.Remove(lflf); }
    }

    private static CostumeDirectory DirectoryFor(ScummBlock b)
    {
        lock (Sync) return Directories.GetValue(Root(b), r => new CostumeDirectory(r));
    }

    private static bool IsScriptTag(uint tag)
    {
        return tag is ScummTag.LSCR or ScummTag.ENCD or ScummTag.EXCD or ScummTag.SCRP or ScummTag.VERB;
    }

    /// <summary>
    /// The player's actor: the one the scripts dress in costumes in the most rooms (Guybrush, actor 1,
    /// in COMI). Its special-animation costumes overlap hotspots by chance, so overlap doesn't name
    /// them. -1 when no actor stands out.
    /// </summary>
    public static int PlayerActor(ScummBlock any)
    {
        var root = Root(any);
        lock (Sync)
            if (Players.TryGetValue(root, out var known)) return known.Value;

        var rooms = new Dictionary<int, int>();
        foreach (var lflf in Walk(root, 3).Where(b => b.Tag == ScummTag.LFLF))
        {
            var actors = new HashSet<int>();
            foreach (var script in Walk(lflf, 4))
                if (IsScriptTag(script.Tag))
                    foreach (var (Pos, Actor, Costume) in CostumeSets(script.DataSpan))
                        if (Actor > 0) actors.Add(Actor);
            foreach (int a in actors) rooms[a] = rooms.GetValueOrDefault(a) + 1;
        }
        var ranked = rooms.OrderByDescending(kv => kv.Value).ToList();
        int player = ranked.Count > 0 && ranked[0].Value >= 5 && (ranked.Count == 1 || ranked[0].Value >= 2 * ranked[1].Value)
            ? ranked[0].Key : -1;
        lock (Sync) Players.AddOrUpdate(root, new StrongBox<int>(player));
        return player;
    }

    /// <summary>The costume number of an AKOS block, from the index (null without an index).</summary>
    public static int? CostumeNumber(ScummBlock akos)
    {
        return akos?.Tag == ScummTag.AKOS ? DirectoryFor(akos).CostumeOf(akos) : null;
    }

    /// <summary>The name of the object an AKOS belongs to, once its room has been analysed.</summary>
    public static string ObjectNameOf(ScummBlock akos)
    {
        lock (Sync) return akos != null && AkosObjectNames.TryGetValue(akos, out var n) ? n : null;
    }

    /// <summary>Analyses every room under <paramref name="root"/> (names AKOS blocks for the hierarchy).</summary>
    public static void AnalyzeAll(ScummBlock root, AkosParser parser = null)
    {
        parser ??= new AkosParser();
        foreach (var lflf in Walk(root, 3).Where(b => b.Tag == ScummTag.LFLF).ToList())
        {
            if (!lflf.Children.Any(c => c.Tag == ScummTag.AKOS)) continue;
            if (RoomModel.TryCreate(lflf, out var room))
            {
                try { Analyze(room, parser); }
                catch { /* a malformed room shouldn't stop the others */ }
            }
        }
    }

    /// <summary>
    /// The room's costumes, one entry per AKOS in the room (more if one costume is matched to several
    /// objects), with the objects they belong to. Cached per room until the links change.
    /// </summary>
    public static List<RoomActor> Analyze(RoomModel room, AkosParser parser = null)
    {
        var lflf = room?.Lflf;
        if (lflf == null) return [];
        lock (Sync)
            if (Analyses.TryGetValue(lflf, out var cached)) return cached;

        parser ??= new AkosParser();
        var directory = DirectoryFor(lflf);
        var akosBlocks = lflf.Children.Where(c => c.Tag == ScummTag.AKOS).ToList();
        var data = new Dictionary<ScummBlock, (AkosData Data, AkosFrame Frame)>();
        (AkosData, AkosFrame) Load(ScummBlock akos)
        {
            if (data.TryGetValue(akos, out var v)) return v;
            var d = AkosParser.Parse(akos);
            int chore = AkosFrames.FindDefaultChore(d);
            var f = chore >= 0 ? AkosFrames.ComposeAfterInit(d, chore) : null;
            return data[akos] = (d, f is { IsEmpty: false } ? f : null);
        }

        // Script pairs (actor, costume, position) for the room's own costumes.
        int player = PlayerActor(lflf);
        var scripts = Walk(lflf, 4).Where(b => IsScriptTag(b.Tag)).ToList();
        // Scripts that dress the actor passed to them: the game's global ones and this room's local ones.
        var globalDressers = DressersFor(lflf);
        var localDressers = new Dictionary<int, IReadOnlyList<ParamCostume>>();
        foreach (var b in scripts.Where(b => b.Tag == ScummTag.LSCR && b.DataLength >= 4))
            if (ParamCostumes(b.DataSpan) is { Count: > 0 } pcs) localDressers[(int)b.DataSpan.U32LE(0)] = pcs;
        IReadOnlyList<ParamCostume> Dresser(int no) =>
            (no >= 2000 ? localDressers : globalDressers).TryGetValue(no, out var pcs) ? pcs : null;
        var scans = scripts.Select(b => ScanScript(b.DataSpan, room.RoomNo, Dresser)).ToList();
        // Animations the room's scripts play on each actor, and which costumes each actor wears here.
        var roomAnims = new Dictionary<int, List<int>>();
        foreach (var b in scripts)
            foreach (var (_, actor, anim) in Animations(b.DataSpan))
                if (anim is >= 0 and < 1000) (roomAnims.TryGetValue(actor, out var l) ? l : roomAnims[actor] = []).Add(anim);
        var wears = scans.SelectMany(x => x).GroupBy(x => x.Actor).ToDictionary(g => g.Key, g => g.Select(x => x.Costume).Distinct().Count());
        // Every actor a script dresses in each costume, placed or not.
        var wearers = new Dictionary<int, List<int>>();
        foreach (var b in scripts)
            foreach (var (Pos, Actor, Costume) in CostumeSets(b.DataSpan).Where(c => c.Actor > 0))
            {
                var list = wearers.TryGetValue(Costume, out var l) ? l : wearers[Costume] = [];
                if (!list.Contains(Actor)) list.Add(Actor);
            }

        var placed = new List<RoomActor>();
        var scriptOf = new Dictionary<RoomActor, int>();
        var seen = new HashSet<(int, int, int, int)>();
        int planes = ZPlanes.Count(room);
        // When each placement runs (as the room opens, in a cutscene, later); a costume placed several
        // ways gets its earliest, so McMutton placed on entry and in a cutscene counts as on entry.
        var timing = new PlacementTiming(scripts, room.RoomNo, FreshGame.For(lflf));
        var earliest = new Dictionary<(int, int, int, int), PlacementTime>();
        var firstVisit = new HashSet<(int, int, int, int)>();
        var stays = new HashSet<(int, int, int, int)>();
        static (int, int, int, int) Key(int actor, int costume, int x, int y) => actor > 0 ? (actor, costume, 0, 0) : (actor, costume, x, y);
        foreach (var (si, list) in scans.Select((list, i) => (i, list)))
            foreach (var pl in list)
            {
                var t = timing.At(scripts[si], pl.CostumePos, pl.PlacePos);
                var key = Key(pl.Actor, pl.Costume, pl.X, pl.Y);
                if (!earliest.TryGetValue(key, out var e) || t < e) earliest[key] = t;
                if (!pl.Passing) stays.Add(key);
                if (t is PlacementTime.OnEntry or PlacementTime.OnEntryIf && timing.Reached(scripts[si], pl.CostumePos, pl.PlacePos))
                    firstVisit.Add(key);
            }
        // The z-clip and ignore-boxes the room's entry scripts leave on each actor: the engine keeps them on
        // the actor, so a cutscene that dresses and puts it without touching them draws it that way too
        // (armsdlr's local 2001 ends with Kenny at always-zclip 2; script 287 puts him in costume 252).
        var clipOps = scripts.Select(b => ClipOps(b.DataSpan)).ToList();
        var entryClip = new Dictionary<int, (int Clip, bool Ignore)?>();
        for (int i = 0; i < scripts.Count; i++)
        {
            if (!timing.RunsOnEntry(scripts[i])) continue;
            foreach (int a in clipOps[i].Select(op => op.Actor).Where(a => a > 0).Distinct())
            {
                var end = FoldClip(clipOps[i], a, -1, int.MaxValue, (ClipByBox, false));
                entryClip[a] = entryClip.TryGetValue(a, out var known) && known != end ? null : end;
            }
        }
        foreach (var (si, (actor, costume, sx, sy, elevation, scriptAnim, dir, via, scale, scanClip, scanIgnore, followsAtPut, removes, _, _, _, animChoices)) in scans.SelectMany((list, i) => list.Select(e => (i, e)))
                     // A put that follows its costume first: it says how the actor is set up (smglcave's
                     // 388 is set up with ignore-boxes and scale 255 before its put in local 2012, while
                     // local 2004 only puts the player there before dressing it, in an override).
                     .OrderBy(x => x.e.PlacePos > x.e.CostumePos ? 0 : 1))
        {
            // The animation the script starts; if a fresh game skips it, the one in the else part it reaches.
            int anim = animChoices is { Length: > 1 } && !timing.Reached(scripts[si], animChoices[0].Pos)
                       && animChoices.Skip(1).FirstOrDefault(a => timing.Reached(scripts[si], a.Pos)) is { Pos: > 0 } reached
                ? reached.Anim : scriptAnim;
            // An actor number wears a costume once; an actor in a variable is a different actor at
            // each spot it's put (the fortbase chickens), so each spot is kept.
            if (!seen.Add(Key(actor, costume, sx, sy))) continue;
            // A script that leaves the actor's z-clip and boxes alone draws it as the entry scripts left it.
            var (clip, ignoreBoxes) = (scanClip, scanIgnore);
            if (actor > 0 && !timing.RunsOnEntry(scripts[si]) && !clipOps[si].Any(op => op.Actor == actor)
                && (clip, ignoreBoxes) == (ClipByBox, false) && !followsAtPut && entryClip.GetValueOrDefault(actor) is { } left)
                (clip, ignoreBoxes) = left;
            var akos = directory.Resolve(costume, lflf);
            if (akos == null || !ReferenceEquals(akos.Parent, lflf)) continue;
            var (d, f) = Load(akos);
            // An actor that follows the boxes when it's put is moved into one, as the engine does. Otherwise
            // it's drawn where it's put (a close-up at 0,0), in the box the engine would pick there; a later
            // follow-boxes, which close-ups set once they're done, doesn't move the picture. Ignoring the
            // boxes in the end means box 0.
            var (bx, by, box) = PutInBox(room.Walkboxes, sx, sy, actor == player);
            // The player walks the boxes unless its script says otherwise; no room setup script turns
            // that off for it (plndrmap puts Guybrush next to his line of boxes, and he stands on it).
            // Only when the script leaves its z-clip alone and doesn't put it at the origin: a pose with
            // never-zclip, or a full-screen close-up at 0,0, isn't the player walking.
            bool follows = followsAtPut || (actor == player && !ignoreBoxes && clip == ClipByBox && (sx, sy) != (0, 0));
            var (x, y) = follows ? (bx, by) : (sx, sy);
            // Put outside every box without a follow-boxes before it: the engine would have moved it, so
            // it ignores the boxes (set by the room's setup script), and stands in box 0.
            if (ignoreBoxes || (!follows && (bx, by) != (sx, sy))) box = 0;
            // Show the animation the script starts (e.g. a full-screen close-up whose stand chore is
            // empty). If that's blank and so is the default pose (the costume starts hidden), use
            // another animation the room plays on the same actor, as long as the actor wears only this
            // costume here (banjo's close-up appears from a loop in another script).
            bool borrowed = false;
            if (Animated(d, anim, dir) is { } started) f = started;
            else if (f == null && wears.GetValueOrDefault(actor) == 1 && roomAnims.TryGetValue(actor, out var others))
            {
                f = others.Distinct().Select(o => Animated(d, o, dir)).FirstOrDefault(x => x != null);
                borrowed = f != null;
            }
            // A script that dresses and puts an actor without giving it an elevation or a default draws it at the
            // elevation it already has: the engine keeps it on the actor. Where the actor comes from another room
            // through a global script (this one, or one that enters this room), that's what that room leaves on it:
            // the barber shop's Haggis at -30, still there at the caber toss (global 197 enters room 22, whose
            // local 2001 puts him there).
            int lift = elevation;
            if (lift == 0 && actor > 0
                && !SetsElevationOrDefault(scripts[si].DataSpan, actor, scans[si].FirstOrDefault(e => e.Actor == actor && e.Costume == costume).PlacePos)
                && CarriedElevation(lflf, room.RoomNo, GlobalNumber(scripts[si]), actor) is int carried)
                lift = carried;
            var entry = new RoomActor
            {
                Actor = Math.Max(0, actor), Costume = costume, Akos = akos, X = x, Y = y, Elevation = lift, HasPosition = true,
                // An actor in a box is drawn at the box's scale, whatever its script set: the engine
                // works it out again at every draw (setupActorScale). Only ignoring the boxes keeps it.
                Data = d, Frame = f, Mirrored = AkosFrames.IsMirrored(d, dir), IsPlayer = actor == player, DressedBy = via, SortY = y,
                Scale = box > 0 ? Walkboxes.ScaleAt(room.Walkboxes, room.ScaleSlots, box, x, y) : scale,
                Removes = removes ?? [],
                When = earliest.GetValueOrDefault(Key(actor, costume, sx, sy)),
                FirstVisit = firstVisit.Contains(Key(actor, costume, sx, sy)),
                Passing = !stays.Contains(Key(actor, costume, sx, sy)),
                StartsBlank = borrowed,
                // Clamped to the room's top plane, like the engine.
                ZPlane = Math.Clamp(clip == ClipByBox ? MaskOf(room, box) : clip, 0, planes),
            };
            placed.Add(entry);
            scriptOf[entry] = si;
        }
        placed = MergeLayers(placed, scriptOf);
        // A costume is drawn in front of the actors its script takes out of the room while it plays.
        foreach (var p in placed.Where(p => p.Removes.Count > 0))
            foreach (var o in placed.Where(o => o.Actor > 0 && o.Actor != p.Actor && p.Removes.Contains(o.Actor)))
                p.SortY = Math.Max(p.SortY, o.Y + 1);

        // Image-less hotspots, except exits: a room's doors are walk-through areas, never a costume
        // (full-room overlays such as waterlines overlap them by chance).
        var candidates = room.Objects.Where(o => o.ImageCount == 0 && !IsExit(o)).ToList();
        MatchObjects([.. placed.Where(p => !p.IsPlayer)], candidates, a => a.RoomBounds);

        // Spots: an actor kept in a variable is a different actor at each spot it's put (the fortbase
        // chickens), and an actor dressed by a call may get one of several costumes (script 88 picks one
        // at random). Each spot shows one, preferring one that matched a hotspot.
        static bool AtSpot(RoomActor p) => p.Actor == 0 || p.DressedBy >= 0;
        // A dresser that picks the facing at random (script 88: face 90 or 270, a costume for each) shows
        // the one drawn as it's stored, its first branch; the hotspot matched to any of them goes with it.
        var spots = placed.Where(AtSpot)
            .GroupBy(p => (p.Actor, p.X, p.Y, p.DressedBy))
            .Select(g =>
            {
                var pick = g.Where(p => p.CanDraw).OrderBy(p => p.Mirrored).FirstOrDefault() ?? g.First();
                pick.Object ??= g.FirstOrDefault(p => p.Object != null)?.Object;
                return pick;
            })
            .ToHashSet();
        foreach (var dropped in placed.Where(p => AtSpot(p) && !spots.Contains(p))) dropped.Object = null;
        // Costumes a script called from this room may dress an actor in, for those placed nowhere.
        var offeredBy = new Dictionary<int, int>();
        foreach (var b in scripts)
            foreach (var (Pos, Script, Actor, Costume, Scale) in DressingCalls(b.DataSpan, Dresser))
                offeredBy.TryAdd(Costume.Costume, Script);

        // One entry per AKOS: its matched placements, else its first placement, else an unplaced entry;
        // plus the spots where an actor in a variable wears it.
        var result = new List<RoomActor>();
        foreach (var akos in akosBlocks)
        {
            var mine = placed.Where(p => ReferenceEquals(p.Akos, akos) && !AtSpot(p)).ToList();
            var matched = mine.Where(p => p.Object != null).ToList();
            var atSpots = placed.Where(p => ReferenceEquals(p.Akos, akos) && spots.Contains(p)).ToList();
            if (matched.Count > 0) result.AddRange(matched);
            else if ((mine.FirstOrDefault(p => p.CanDraw) ?? mine.FirstOrDefault()) is { } first) result.Add(first);
            result.AddRange(atSpots);
            if (mine.Count == 0 && atSpots.Count == 0 && directory.CostumeOf(akos) is int costume)
            {
                var (d, f) = Load(akos);
                var worn = wearers.GetValueOrDefault(costume);
                result.Add(new RoomActor
                {
                    Actor = worn?[0] ?? 0, Actors = worn, Costume = costume, Akos = akos, Data = d, Frame = f,
                    DressedBy = offeredBy.TryGetValue(costume, out int by) ? by : -1,
                });
            }
        }

        ApplyLinks(room, result, directory, Load);

        // What's there as the room opens: costumes placed on entry, on a fresh game's first visit, the
        // player aside, one per actor (an unconditional one first).
        bool OnEntry(RoomActor r) =>
            r.HasPosition && !r.IsPlayer && r.FirstVisit && !r.Passing && !r.StartsBlank && r.When is PlacementTime.OnEntry or PlacementTime.OnEntryIf;
        foreach (var g in result.Where(r => r.Actor > 0 && OnEntry(r)).GroupBy(r => r.Actor))
            g.OrderBy(r => r.When).First().StartsShown = true;
        // An actor kept in a variable goes to spots picked as the game runs: show as many as a first visit
        // puts, at random (fortbase's chickens: "flock-size = flock-size + 2", then that many of 16 spots).
        var fresh = FreshGame.For(lflf);
        foreach (var g in result.Where(r => r.Actor == 0 && r.Actors == null && OnEntry(r) && scriptOf.ContainsKey(r)).GroupBy(r => scriptOf[r]))
            foreach (var r in g.OrderBy(_ => Random.Shared.Next()).Take(FirstVisitCount(scripts[g.Key].DataSpan, fresh)))
                r.StartsShown = true;

        lock (Sync)
        {
            foreach (var akos in akosBlocks)
            {
                var owner = result.FirstOrDefault(r => ReferenceEquals(r.Akos, akos) && r.Object != null)
                         ?? result.FirstOrDefault(r => ReferenceEquals(r.Akos, akos) && r.Character != null);
                AkosObjectNames.AddOrUpdate(akos, owner?.Owner.Name);
            }
            Analyses.AddOrUpdate(lflf, result);
        }
        return result;
    }

    private static void ApplyLinks(RoomModel room, List<RoomActor> result, CostumeDirectory directory,
        Func<ScummBlock, (AkosData, AkosFrame)> load)
    {
        Dictionary<string, int> links;
        lock (Sync) links = new Dictionary<string, int>(_links);
        string prefix = $"{room.RoomNo}/";
        foreach (var obj in room.Objects)
        {
            if (!links.TryGetValue(prefix + obj.Name, out int costume)) continue;
            // The object gives up whatever it was matched to.
            foreach (var r in result.Where(r => ReferenceEquals(r.Object, obj))) { r.Object = null; r.IsLinked = false; }
            if (costume <= 0) continue;

            var akos = directory.Resolve(costume, room.Lflf);
            if (akos == null || !ReferenceEquals(akos.Parent, room.Lflf)) continue;
            // Prefer an entry of that costume that isn't someone else's; otherwise add one.
            var entry = result.FirstOrDefault(r => ReferenceEquals(r.Akos, akos) && (r.Object == null || !r.IsLinked) && r.CanDraw)
                     ?? result.FirstOrDefault(r => ReferenceEquals(r.Akos, akos) && (r.Object == null || !r.IsLinked));
            if (entry == null)
            {
                var (d, f) = load(akos);
                entry = new RoomActor { Costume = costume, Akos = akos, Data = d, Frame = f };
                result.Add(entry);
            }
            entry.Object = obj;
            entry.IsLinked = true;
        }
    }

    /// <summary>A pushed literal ending just before <paramref name="end"/>: push-word is 5 bytes.</summary>
    private static bool Lit(ReadOnlySpan<byte> s, int end, out int v)
    {
        v = 0;
        int p = end - 5;
        if (p < 0 || end > s.Length || s[p] != OpPushWord) return false;
        v = s.S32LE(p + 1);
        return true;
    }

    /// <summary>
    /// A constant expression whose code ends just before <paramref name="end"/>: a literal, or literals
    /// joined by + - * / (the scripts write "492 + 300"). <paramref name="start"/> is where its code begins,
    /// i.e. where the argument before it ends.
    /// </summary>
    internal static bool Const(ReadOnlySpan<byte> s, int end, out int v, out int start, int depth = 0)
    {
        v = 0;
        start = end;
        if (end <= 0 || end > s.Length || depth > 8) return false;
        byte op = s[end - 1];
        if (op is OpAdd or OpSub or OpMul or OpDiv
            && Const(s, end - 1, out int right, out int mid, depth + 1)
            && Const(s, mid, out int left, out int first, depth + 1))
        {
            int? r = op switch
            {
                OpAdd => left + right,
                OpSub => left - right,
                OpMul => left * right,
                _ => right != 0 ? left / right : null,
            };
            if (r is int value)
            {
                v = value;
                start = first;
                return true;
            }
        }
        if (!Lit(s, end, out v)) return false;
        start = end - 5;
        return true;
    }

    internal static bool Const(ReadOnlySpan<byte> s, int end, out int v)
    {
        return Const(s, end, out v, out _);
    }

    /// <summary>A variable as an actor key: always negative, so it can't meet a literal actor number.</summary>
    private static int VarKey(int var)
    {
        return int.MinValue + (var & 0x7FFFFFFF);
    }

    private const int LocalVar = 0x40000000;

    /// <summary>
    /// An actor argument ending just before <paramref name="end"/>: a constant (the actor number) or a
    /// pushed variable (its <see cref="VarKey"/>). A script that keeps an actor number in a variable
    /// uses the same variable for the costume and the placement, so the key pairs them.
    /// </summary>
    private static bool ActorArg(ReadOnlySpan<byte> s, int end, out int key, out int start)
    {
        if (Const(s, end, out key, out start)) return true;
        start = end - 5;
        if (start >= 0 && end <= s.Length && s[start] == OpPushVar)
        {
            key = VarKey(s.S32LE(start + 1));
            return true;
        }
        key = 0;
        start = end;
        return false;
    }

    private static bool ActorArg(ReadOnlySpan<byte> s, int end, out int key)
    {
        return ActorArg(s, end, out key, out _);
    }

    /// <summary>
    /// What a script does to the actor passed in one of its parameters: the costumes it may dress it in,
    /// each with the facing set before it and the first animation played after. COMI's fortbase chickens
    /// are dressed this way by global script 88, which picks one of four costumes at random.
    /// </summary>
    /// <param name="WhenParam">A parameter the costume depends on (-1 for none): the script sets it only when
    /// that parameter is nonzero (<paramref name="WhenTrue"/>) or zero. Script 88 picks costumes 69/70 for a
    /// nonzero third argument and 67/68 for zero.</param>
    /// <param name="Scale">The scale it sets after the costume (-1 for none), or <paramref name="ScaleParam"/>, the
    /// parameter it takes the scale from (script 88: "actor local0 scale local1").</param>
    /// <param name="Clip">Its z-clip once the script has set it up (<see cref="ClipByBox"/> when it doesn't), and
    /// <paramref name="IgnoreBoxes"/>, whether it then ignores the walkboxes.</param>
    internal readonly record struct ParamCostume(int Param, int Costume, int Anim, int Dir, int WhenParam = -1, bool WhenTrue = false,
        int Scale = -1, int ScaleParam = -1, int Clip = ClipByBox, bool IgnoreBoxes = false);

    private const byte OpIf = 0x64, OpIfNot = 0x65, OpJump = 0x66, OpNot = 0x07;

    /// <summary>
    /// The parameter test that decides whether code at <paramref name="at"/> runs: the nearest
    /// "if (localK)" / "if (!localK)" whose then-part holds it, or whose else-part does (reached by the
    /// jump, with the then-part ending in a jump over it). Null when no such test guards it.
    /// </summary>
    private static (int Param, bool WhenTrue)? Guard(ReadOnlySpan<byte> s, int at)
    {
        for (int j = at - 1; j >= 6; j--)
        {
            if (s[j] is not (OpIf or OpIfNot) || j + 5 > s.Length) continue;
            int target = j + 5 + s.S32LE(j + 1);
            if (target <= j || target > s.Length) continue;
            bool negated = s[j - 1] == OpNot;
            int condEnd = negated ? j - 1 : j;
            if (condEnd < 5 || s[condEnd - 5] != OpPushVar) continue;
            int var = s.S32LE(condEnd - 4);
            if ((var & LocalVar) == 0 || (var & ~LocalVar) > 15) continue;
            // if jumps when its condition is true, ifNot when it's false.
            bool jumpsWhenTrue = (s[j] == OpIf) != negated;
            if (at < target) return (var & ~LocalVar, !jumpsWhenTrue);            // the part the jump skips
            if (target - 5 > j && s[target - 5] == OpJump && target + s.S32LE(target - 4) > at)
                return (var & ~LocalVar, jumpsWhenTrue);                            // the else part it jumps to
        }
        return null;
    }

    /// <summary>Whether code at <paramref name="at"/> is skipped by an unconditional forward jump after
    /// <paramref name="from"/> (the end of an if's then-part jumping over its else-part).</summary>
    /// <param name="elseParts">Not counting the jump at the end of an if's then-part when the if comes
    /// after <paramref name="from"/>: code in its else part runs instead of the then-part, not never.</param>
    internal static bool JumpedOver(ReadOnlySpan<byte> s, int from, int at, bool elseParts = false)
    {
        for (int j = from + 1; j < at && j + 5 <= s.Length; j++)
            if (s[j] == OpJump && j + 5 + s.S32LE(j + 1) is int target && target > at && target <= s.Length
                && !(elseParts && EndsThenPart(s, from, j)))
                return true;
        return false;
    }

    /// <summary>Whether the jump at <paramref name="j"/> ends the then-part of an if after <paramref name="from"/>
    /// (the if's condition jumps to just after it, to the else part).</summary>
    private static bool EndsThenPart(ReadOnlySpan<byte> s, int from, int j)
    {
        for (int k = from + 1; k < j && k + 5 <= s.Length; k++)
            if (s[k] is OpIf or OpIfNot && k + 5 + s.S32LE(k + 1) == j + 5) return true;
        return false;
    }

    /// <summary>Whether code at <paramref name="at"/> sits inside a condition or a loop that starts after
    /// <paramref name="from"/> (town's "do { … put-actor 18 in-room 0 } while (true)" runs once the
    /// costume has been handed to another actor).</summary>
    internal static bool Guarded(ReadOnlySpan<byte> s, int from, int at)
    {
        for (int j = from + 1; j < at && j + 5 <= s.Length; j++)
            if (s[j] is OpIf or OpIfNot && j + 5 + s.S32LE(j + 1) is int target && target > at && target <= s.Length)
                return true;
        // A loop: a jump after it back to between the two.
        for (int j = at + 1; j + 5 <= s.Length; j++)
            if (s[j] is OpIf or OpIfNot or OpJump && j + 5 + s.S32LE(j + 1) is int back && back > from && back <= at)
                return true;
        return false;
    }

    /// <summary>Calls that start a script with arguments: start-script, start-script-quick, chain-script.</summary>
    private const byte OpStartScript = 0x79, OpStartScriptQuick = 0x7A, OpChainScript = 0x7D;

    /// <summary>The costumes a script's code dresses its parameters in (empty when it doesn't).</summary>
    internal static List<ParamCostume> ParamCostumes(ReadOnlySpan<byte> s)
    {
        var result = new List<ParamCostume>();
        var sets = CostumeSets(s);
        var faces = new List<(int Pos, int Actor, int Dir)>();
        // Scale ops on an actor: a constant, or a parameter (its VarKey) to take it from.
        var scales = new List<(int Pos, int Actor, bool IsConst, int Value)>();
        int cur = 0;
        for (int p = 5; p + 1 < s.Length; p++)
        {
            if (s[p] != OpActorOps) continue;
            if (s[p + 1] == SubSetCurActor) cur = ActorArg(s, p, out int a) ? a : 0;
            else if (s[p + 1] == SubFace && cur != 0 && Const(s, p, out int degrees)) faces.Add((p, cur, degrees));
            else if (s[p + 1] == SubScale && cur != 0 && ActorArg(s, p, out int sc))
                scales.Add((p, cur, Const(s, p, out _), sc));
        }
        var anims = Animations(s);
        var clips = ClipOps(s);
        foreach (var (Pos, Actor, Costume) in sets)
        {
            if (Actor >= 0) continue;
            int var = Actor - int.MinValue;
            if ((var & LocalVar) == 0) continue;           // a global variable, not a parameter
            int param = var & ~LocalVar;
            if (param > 15) continue;
            // The script branches between costumes, so the facing is the last one set before this costume
            // and the animation the first one played after it, whichever branch they're in.
            int dir = faces.Where(f => f.Actor == Actor && f.Pos < Pos).Select(f => f.Dir).DefaultIfEmpty(180).Last();
            int anim = anims.Where(x => x.Actor == Actor && x.Pos > Pos && x.Anim is >= 0 and < 1000).Select(x => x.Anim).DefaultIfEmpty(-1).First();
            var guard = Guard(s, Pos);
            // The first scale set on it after the costume, whichever branch it's in.
            int scale = -1, scaleParam = -1;
            if (scales.FirstOrDefault(x => x.Actor == Actor && x.Pos > Pos) is { Pos: > 0 } sc)
            {
                if (sc.IsConst) scale = sc.Value;
                else if (((sc.Value - int.MinValue) & LocalVar) != 0) scaleParam = (sc.Value - int.MinValue) & ~LocalVar;
            }
            // Its clip after the whole setup, whichever branch each op is in.
            var (clip, ignore) = FoldClip(clips, Actor, -1, int.MaxValue, (ClipByBox, false));
            if (!result.Any(r => r.Param == param && r.Costume == Costume))
                result.Add(new ParamCostume(param, Costume, anim, dir, guard?.Param ?? -1, guard?.WhenTrue ?? false, scale, scaleParam, clip, ignore));
        }
        return result;
    }

    /// <summary>
    /// Calls in a script to scripts that dress a parameter: (position, script number, the actor key passed
    /// for that parameter, what the callee does with it). Only calls whose script number and arguments
    /// are constants or variables can be read.
    /// </summary>
    private static List<(int Pos, int Script, int Actor, ParamCostume Costume, int? Scale)> DressingCalls(
        ReadOnlySpan<byte> s, Func<int, IReadOnlyList<ParamCostume>> dressers)
    {
        var calls = new List<(int, int, int, ParamCostume, int?)>();
        if (dressers == null) return calls;
        for (int p = 10; p < s.Length; p++)
        {
            if (s[p] is not (OpStartScript or OpStartScriptQuick or OpChainScript)) continue;
            // Pushed: [flags,] script, the arguments, their count; read back to front.
            if (!Const(s, p, out int count, out int end) || count is < 1 or > 16) continue;
            var args = new int[count];
            var constant = new bool[count];
            bool ok = true;
            for (int i = count - 1; i >= 0 && ok; i--)
            {
                constant[i] = Const(s, end, out args[i], out int start);
                ok = constant[i] ? (end = start) >= 0 : ActorArg(s, end, out args[i], out end);
            }
            if (!ok || !Const(s, end, out int script) || script <= 0) continue;
            var dressed = dressers(script);
            if (dressed == null) continue;
            foreach (var pc in dressed)
            {
                if (pc.Param >= count) continue;
                // A costume the callee only sets for some value of an argument we can see isn't this one.
                if (pc.WhenParam >= 0 && pc.WhenParam < count && constant[pc.WhenParam] && (args[pc.WhenParam] != 0) != pc.WhenTrue) continue;
                // The scale: the callee's constant, or the argument it takes it from when that's a constant.
                // A 0 there means "no scale" (script 88 then sets 255), as 0 would draw nothing.
                int? scale = pc.Scale >= 0 ? pc.Scale
                    : pc.ScaleParam >= 0 && pc.ScaleParam < count && constant[pc.ScaleParam] ? (args[pc.ScaleParam] > 0 ? args[pc.ScaleParam] : 255)
                    : null;
                calls.Add((p, script, args[pc.Param], pc, scale));
            }
        }
        return calls;
    }

    /// <summary>
    /// The script's costume placements: actor key (an actor number, or a variable's <see cref="VarKey"/>),
    /// costume, position, elevation, the animation the script starts for it (do-animation after the
    /// costume is set; -1 for none) with the facing in degrees (south unless a face op says otherwise)
    /// that picks its chore, and the script that dressed it when that was a call (-1 otherwise).
    /// Positions may be constant expressions ("320,492 + 300"); the elevation is the last one set
    /// between the costume and the placement. With <paramref name="dressers"/>, a call to a script that
    /// dresses its parameter (start-script 88(local2, …)) counts as dressing the actor passed.
    /// </summary>
    internal readonly record struct Placement(int Actor, int Costume, int X, int Y, int Elevation, int Anim, int Dir, int Via, int Scale,
        int Clip, bool IgnoreBoxes, bool FollowsAtPut = false, int[] Removes = null, int CostumePos = -1, int PlacePos = -1,
        bool Passing = false, (int Pos, int Anim)[] AnimChoices = null);

    internal static List<Placement> ScanScript(
        ReadOnlySpan<byte> s, int roomNo, Func<int, IReadOnlyList<ParamCostume>> dressers = null)
    {
        var costumes = CostumeSets(s).Select(c => (c.Pos, c.Actor, c.Costume, Anim: (int?)null, Dir: (int?)null, Via: -1, Scale: (int?)null,
            Clip: ((int Clip, bool Ignore)?)null)).ToList();
        foreach (var (pos, script, actor, pc, scale) in DressingCalls(s, dressers))
            costumes.Add((pos, actor, pc.Costume, pc.Anim, pc.Dir, script, scale, (pc.Clip, pc.IgnoreBoxes)));
        var clips = ClipOps(s);
        costumes.Sort((a, b) => a.Pos.CompareTo(b.Pos));
        var anims = Animations(s);
        var faces = new List<(int Pos, int Actor, int Dir)>(); // Dir in degrees
        var elevations = new List<(int Pos, int Actor, int Elevation)>();
        var scales = new List<(int Pos, int Actor, int Scale)>();
        var defaults = new List<(int Pos, int Actor)>();
        int curActor = 0;
        for (int p = 5; p + 1 < s.Length; p++)
        {
            if (s[p] != OpActorOps) continue;
            if (s[p + 1] == SubSetCurActor) curActor = ActorArg(s, p, out int ca) ? ca : 0;
            else if (s[p + 1] == SubFace && curActor != 0 && Const(s, p, out int degrees)) faces.Add((p, curActor, degrees));
            else if (s[p + 1] == SubElevation && curActor != 0 && Const(s, p, out int elevation)) elevations.Add((p, curActor, elevation));
            else if (s[p + 1] == SubScale && curActor != 0 && Const(s, p, out int scale) && scale > 0) scales.Add((p, curActor, scale));
            else if (s[p + 1] == SubDefault && curActor != 0) defaults.Add((p, curActor));
        }
        var places = new List<(int Pos, int Actor, int X, int Y)>();
        // Re-placements relative to where the actor is: put-actor A at x-of(A) + dx, y-of(A) + dy.
        var moves = new List<(int Pos, int Actor, int Dx, int Dy)>();
        // Placements in other rooms (a cutscene that visits several): a re-placement after one isn't ours.
        var elsewhere = new List<(int Pos, int Actor)>();
        // Actors taken out of the room (put-actor B at … in-room 0).
        var removals = new List<(int Pos, int Actor)>();

        for (int p = 20; p < s.Length; p++)
        {
            if (s[p] == OpPutActorAtXY)
            {
                // Arguments are pushed actor, x, y, room: read them back to front.
                int yEnd;
                bool sameRoom = false;
                if (Const(s, p, out int r, out int roomStart))
                {
                    sameRoom = r == SameRoom;
                    if (r != roomNo && !sameRoom)
                    {
                        if (Const(s, roomStart, out _, out int oxEnd) && Const(s, oxEnd, out _, out int oaEnd) && ActorArg(s, oaEnd, out int oa) && oa != 0)
                        {
                            elsewhere.Add((p, oa));
                            if (r == 0) removals.Add((p, oa));
                        }
                        continue;
                    }
                    yEnd = roomStart;
                }
                else if (s[p - 5] == OpPushVar) yEnd = p - 5; // usually the current room
                else continue;
                if (OffsetOf(s, yEnd, OpGetObjectY, out int ya, out int dy, out int yStart)
                    && OffsetOf(s, yStart, OpGetObjectX, out int xa, out int dx, out int xStart)
                    && ActorArg(s, xStart, out int ma) && ma != 0 && ma == xa && ma == ya)
                {
                    moves.Add((p, ma, dx, dy));
                    continue;
                }
                if (sameRoom) continue;
                // 0,0 counts too: full-room overlays (waterlines) sit at the room's origin. Parking an
                // actor out of the way is done with room 0, which the room check already rejects.
                if (Const(s, yEnd, out int y, out int xEnd) && Const(s, xEnd, out int x, out int aEnd)
                    && ActorArg(s, aEnd, out int a) && a != 0)
                    places.Add((p, a, x, y));
            }
        }

        var pairs = new List<Placement>();
        var code = s.ToArray();
        foreach (var (Pos, Actor, Costume, Anim, Dir, Via, Scale, Clip) in costumes)
        {
            // Code on this costume's path: not on a branch a goto after it jumps over.
            bool OnPath(int pos) => !JumpedOver(code, Pos, pos);
            // Until the actor's next costume change in this script (a call offering several costumes
            // changes it once). A change on a sibling branch doesn't end it: "if … { costume 166; goto L }
            // else { costume 12 }  L: scale 140; put-actor" scales and places both (voodoo-e's skull).
            int until = int.MaxValue, untilCostume = -1;
            foreach (var o in costumes)
                if (o.Actor == Actor && o.Pos > Pos && o.Pos < until && !JumpedOver(s, Pos, o.Pos)) (until, untilCostume) = (o.Pos, o.Costume);
            // The same costume set again before the actor is put: that later set is the one the put wears
            // (brbr-int's Rottingham: costume 124, always-zclip 2, costume 124, ignore-boxes, put, then
            // always-zclip 2 again; read from the first, the window ends before the last z-clip).
            if (untilCostume == Costume && !places.Any(pl => pl.Actor == Actor && pl.Pos > Pos && pl.Pos < until)) continue;
            // Set again after the put, it's still the same picture: its z-clip keeps changing until another
            // costume (armsdlr's Kenny: costume 251, ignore-boxes, put, … costume 251, always-zclip 2).
            int clipUntil = untilCostume != Costume ? until
                : costumes.Where(o => o.Actor == Actor && o.Pos > until && o.Costume != Costume && !JumpedOver(code, Pos, o.Pos))
                          .Select(o => o.Pos).DefaultIfEmpty(int.MaxValue).Min();
            // Below 1000 an animation number is a chore; 1000 and up are turn/stop commands.
            var onPath = anims.Where(a => a.Actor == Actor && a.Pos > Pos && a.Pos < until && OnPath(a.Pos) && a.Anim is >= 0 and < 1000).ToList();
            int anim = Anim ?? onPath.Select(a => a.Anim).DefaultIfEmpty(-1).First();
            // That animation first, then the ones in else parts it skips ("if (bit168) { … do-animation 12 7 }
            // else { do-animation 12 3 }" in cannon): when a fresh game skips the first, the room takes the
            // next it reaches.
            var animChoices = Anim != null || onPath.Count == 0 ? null
                : onPath.Take(1).Concat(anims.Where(a => a.Actor == Actor && a.Pos > onPath[0].Pos && a.Pos < until && a.Anim is >= 0 and < 1000
                                                          && !OnPath(a.Pos) && !JumpedOver(code, Pos, a.Pos, elseParts: true)))
                        .Select(a => (a.Pos, a.Anim)).ToArray();
            // A costume change keeps the actor's facing and scale; only "actor A default" resets them (to
            // 180 and 255). So what this script set before the costume, on the way to it, still holds:
            // quiksand's chicken, dressed by script 88 at scale 160, then face 270, then costume 185.
            bool Before(int pos) => pos < Pos && !JumpedOver(code, pos, Pos);
            int since = defaults.Where(d => d.Actor == Actor && Before(d.Pos)).Select(d => d.Pos).DefaultIfEmpty(-1).Max();
            bool Inherits(int pos) => pos > since && Before(pos);
            var dressedBefore = Via < 0
                ? costumes.Where(o => o.Actor == Actor && o.Via >= 0 && Inherits(o.Pos)).ToList()
                : [];
            int lastDress = dressedBefore.Select(o => o.Pos).DefaultIfEmpty(-1).Max();
            var dressAt = dressedBefore.Where(o => o.Pos == lastDress).ToList();
            int? priorDir = faces.Where(f => f.Actor == Actor && f.Pos > lastDress && Inherits(f.Pos)).Select(f => (int?)f.Dir).LastOrDefault()
                            ?? (dressAt.Select(o => o.Dir).Distinct().Count() == 1 ? dressAt[0].Dir : null);
            int? priorScale = scales.Where(e => e.Actor == Actor && e.Pos > lastDress && Inherits(e.Pos)).Select(e => (int?)e.Scale).LastOrDefault()
                              ?? dressAt.Select(o => o.Scale).FirstOrDefault(x => x != null);
            int dir = Dir ?? faces.Where(f => f.Actor == Actor && f.Pos > Pos && f.Pos < until && OnPath(f.Pos)).Select(f => (int?)f.Dir)
                .FirstOrDefault() ?? priorDir ?? 180;

            // The first placement of the actor while it wears this costume (after the costume is set,
            // before its next change); otherwise, for an actor number, the nearest one, preferring
            // those after. A variable holds different actors at different times, so it needs the former.
            (int Pos, int Actor, int X, int Y)? best = places
                .Where(pl => pl.Actor == Actor && pl.Pos > Pos && pl.Pos < until && OnPath(pl.Pos))
                .Select(pl => ((int, int, int, int)?)pl).FirstOrDefault();
            int bestDist = int.MaxValue;
            if (best == null && Actor > 0)
                foreach (var pl in places)
                {
                    if (pl.Actor != Actor) continue;
                    int dist = pl.Pos >= Pos ? pl.Pos - Pos : (Pos - pl.Pos) * 2;
                    if (dist < bestDist) { bestDist = dist; best = pl; }
                }
            if (best is { } b)
            {
                // "elevation E, then put-actor A at x-of(A), y-of(A) + E" after the placement: the picture
                // stays where it was, but the actor now stands E lower, which is the y the engine sorts by
                // (cannon's Wally: put at 0,0, then y 381 with elevation 381, in front of Guybrush at 328).
                // Only a re-placement right after an elevation is that idiom; others are steps of a scene that
                // plays later (clifface's Guybrush climbs with y-of(1) + 33).
                int nextPlace = places.Select(pl => (pl.Pos, pl.Actor)).Concat(elsewhere)
                    .Where(pl => pl.Actor == Actor && pl.Pos > b.Pos).Select(pl => pl.Pos).DefaultIfEmpty(int.MaxValue).Min();
                var followUps = new List<(int Pos, int Actor, int Dx, int Dy)>();
                if (b.Pos > Pos)
                {
                    int prev = b.Pos;
                    foreach (var m in moves.Where(m => m.Actor == Actor && m.Pos > b.Pos && m.Pos < Math.Min(until, nextPlace)))
                    {
                        if (!elevations.Any(e => e.Actor == Actor && e.Pos > prev && e.Pos < m.Pos)) break;
                        followUps.Add(m);
                        prev = m.Pos;
                    }
                }
                int lastPut = followUps.Count > 0 ? followUps[^1].Pos : b.Pos;
                b = (b.Pos, b.Actor, b.X + followUps.Sum(m => m.Dx), b.Y + followUps.Sum(m => m.Dy));
                // Only an elevation set while it wears this costume, before this placement, moves the
                // picture. The common "elevation E, then put-actor at x-of(a), y-of(a) + E" re-places the
                // actor after the literal placement so it's drawn where it was (only its depth changes).
                bool inWindow = b.Pos > Pos && b.Pos < until;
                int elevation = !inWindow ? 0 : elevations.Where(e => e.Actor == Actor && e.Pos > Pos && e.Pos < lastPut && OnPath(e.Pos))
                                                          .Select(e => e.Elevation).DefaultIfEmpty(0).Last();
                // Scale the same way: set while it wears this costume, before this placement; or what the
                // call that dressed it passes.
                int scale = !inWindow ? Scale ?? priorScale ?? 255
                    : scales.Where(e => e.Actor == Actor && e.Pos > Pos && e.Pos < b.Pos && OnPath(e.Pos))
                            .Select(e => e.Scale).DefaultIfEmpty(Scale ?? priorScale ?? 255).Last();
                // The z-clip it's drawn with: what this script sets on it until its next costume (it applies
                // whenever it's drawn, so after the placement too). A dressing call starts from what the
                // callee set up; otherwise from the start of the script, from the engine's default.
                // After the placement, follow-boxes and default don't count: that's how a script hands the
                // actor back to walking once its scene is done (lc-deck's captain is put with always-zclip
                // 5, and follow-boxes comes at the end).
                // A put after the costume counts even past another costume set on a sibling branch
                // (barber's "if … costume 114 else costume 112", then always-zclip 3 and the put for both).
                int putAt = b.Pos > Pos ? b.Pos : Pos;
                // Between the costume and the put, only what's on its path (not the sibling branch's default).
                var path = clips.Where(op => op.Pos <= Pos || OnPath(op.Pos)).ToList();
                var (clip, ignore) = Clip is { } start
                    ? FoldClip(path, Actor, Pos, putAt, start)
                    : FoldClip(path, Actor, -1, putAt, (ClipByBox, false));
                (clip, ignore) = FoldClip([.. clips.Where(op => op.Ignore != false && !JumpedOver(code, putAt, op.Pos))], Actor, putAt, clipUntil, (clip, ignore));
                // Whether it surely follows the boxes when it's put: then the engine moves it into one. Only
                // what this script says counts (follow-boxes or default before the put); a room's setup
                // script often sets ignore-boxes on an actor that a cutscene elsewhere puts at 0,0.
                bool? ignoreAtPut = null;
                foreach (var op in path)
                    if (op.Actor == Actor && op.Pos < b.Pos && op.Ignore is bool ig) ignoreAtPut = ig;
                if (Clip is { Ignore: true } && b.Pos > Pos
                    && !clips.Any(op => op.Actor == Actor && op.Pos > Pos && op.Pos < b.Pos && op.Ignore != null))
                    ignoreAtPut = true;  // the dressing script set ignore-boxes (script 88)
                // Other actors it takes out of the room for a while as this costume plays, putting them back
                // before the actor's next costume: the costume draws them meanwhile (barber's 122 draws Bill
                // getting up). Removals that aren't undone are a scene ending, not this.
                var removes = b.Pos > Pos
                    ? [.. removals.Where(rm => rm.Actor != Actor && rm.Actor > 0 && rm.Pos > b.Pos && rm.Pos < until
                                           && places.Any(pl => pl.Actor == rm.Actor && pl.Pos > rm.Pos && pl.Pos < until))
                              .Select(rm => rm.Actor).Distinct()]
                    : Array.Empty<int>();
                // Taken out of the room again by this script before its next costume, on the same path and
                // not under a new condition: only there for a moment (an idle animation's extra layer).
                bool passing = b.Pos > Pos && removals.Any(rm => rm.Actor == Actor && rm.Pos > lastPut && rm.Pos < until
                                                                  && OnPath(rm.Pos) && !Guarded(code, lastPut, rm.Pos));
                pairs.Add(new Placement(Actor, Costume, b.X, b.Y, elevation, anim, dir, Via, scale, clip, ignore, ignoreAtPut == false, removes,
                    Pos, b.Pos, passing, animChoices));
            }
        }
        return pairs;
    }

    /// <summary>
    /// Several actors that one script dresses in the same costume and puts on the same spot are one
    /// picture built in layers, each actor playing its own chore (banjo's strings): they become one
    /// entry whose frame holds all their cels, in the script's order.
    /// </summary>
    private static List<RoomActor> MergeLayers(List<RoomActor> placed, Dictionary<RoomActor, int> scriptOf)
    {
        var result = new List<RoomActor>();
        var done = new HashSet<RoomActor>();
        foreach (var p in placed)
        {
            if (done.Contains(p)) continue;
            var layers = p.IsPlayer ? [p] : placed
                .Where(o => !o.IsPlayer && scriptOf[o] == scriptOf[p] && ReferenceEquals(o.Akos, p.Akos) && o.X == p.X && o.DrawY == p.DrawY)
                .ToList();
            foreach (var l in layers) done.Add(l);
            // Only layers that show different frames build a picture together; actors showing the same
            // frame are separate characters that happen to share a costume (hang-dia's dioramas).
            int distinctFrames = layers.Where(l => l.Frame != null)
                .Select(l => string.Join(";", l.Frame.Cels)).Distinct().Count();
            if (layers.Select(l => l.Actor).Distinct().Count() < 2 || distinctFrames < 2)
            {
                result.AddRange(layers);
                continue;
            }
            var frame = new AkosFrame();
            foreach (var l in layers)
                if (l.Frame != null) frame.Cels.AddRange(l.Frame.Cels);
            result.Add(new RoomActor
            {
                Actor = p.Actor, Actors = [.. layers.Select(l => l.Actor).Distinct()], Costume = p.Costume, Akos = p.Akos,
                X = p.X, Y = p.Y, Elevation = p.Elevation, Scale = p.Scale, ZPlane = p.ZPlane, HasPosition = true, Data = p.Data, Frame = frame.IsEmpty ? null : frame, Mirrored = p.Mirrored,
                SortY = p.SortY, Removes = [.. layers.SelectMany(l => l.Removes).Distinct()],
                When = layers.Min(l => l.When), FirstVisit = layers.Any(l => l.FirstVisit), Passing = layers.All(l => l.Passing), StartsBlank = layers.All(l => l.StartsBlank),
            });
        }
        return result;
    }

    /// <summary>
    /// How many spots a script that places actors in a loop fills on a first visit: the N of a
    /// "count = count + N" on a global the boot script leaves at 0 (fortbase's chicken flock grows by 2 a
    /// visit). 0 when the script has no such count.
    /// </summary>
    internal static int FirstVisitCount(ReadOnlySpan<byte> s, FreshGame fresh)
    {
        // push-var V, push-word N, add, write V
        for (int p = 0; p + 21 <= s.Length; p++)
        {
            if (s[p] != OpPushVar || s[p + 5] != OpPushWord || s[p + 10] != 0x0E || s[p + 11] != 0x6D) continue;
            uint v = (uint)s.S32LE(p + 1);
            if ((uint)s.S32LE(p + 12) != v || (v & 0xC0000000) != 0 || fresh?.Vars.ContainsKey(v) == true) continue;
            int n = s.S32LE(p + 6);
            if (n is > 0 and <= 16) return n;
        }
        return 0;
    }

    /// <summary>An exit hotspot: COMI names them "…-door" (optionally numbered: "town-plndrmap-door-1", "town-door-1a").</summary>
    private static bool IsExit(RoomObject o)
    {
        return o.Name != null && ExitName().IsMatch(o.Name);
    }

    [GeneratedRegex(@"(^|-)door(-\d+[a-z]?)?$")]
    private static partial Regex ExitName();

    /// <summary>The first frame of animation <paramref name="anim"/> facing <paramref name="degrees"/>, or null if it draws nothing.</summary>
    private static AkosFrame Animated(AkosData d, int anim, int degrees)
    {
        if (anim < 0) return null;
        int chore = AkosFrames.ChoreIndex(d, anim, AkosFrames.DirectionOf(d, degrees));
        return AkosFrames.HasChore(d, chore) && AkosFrames.ComposeAfterInit(d, chore) is { IsEmpty: false } f ? f : null;
    }

    /// <summary>do-animation ops with a constant animation: (position, actor key, animation).</summary>
    private static List<(int Pos, int Actor, int Anim)> Animations(ReadOnlySpan<byte> s)
    {
        var anims = new List<(int Pos, int Actor, int Anim)>();
        for (int p = 10; p < s.Length; p++)
            if (s[p] == OpAnimateActor && Const(s, p, out int anim, out int animStart) && ActorArg(s, animStart, out int actor))
                anims.Add((p, actor, anim));
        return anims;
    }

    /// <summary>actorOps setCurActor / setCostume pairs with a constant costume: (position, actor key, costume).</summary>
    private static List<(int Pos, int Actor, int Costume)> CostumeSets(ReadOnlySpan<byte> s)
    {
        var costumes = new List<(int Pos, int Actor, int Costume)>();
        int curActor = 0;
        for (int p = 5; p + 1 < s.Length; p++)
        {
            if (s[p] != OpActorOps) continue;
            byte sub = s[p + 1];
            if (sub == SubSetCurActor) curActor = ActorArg(s, p, out int a) ? a : 0;
            else if (sub == SubSetCostume && curActor != 0 && Const(s, p, out int c) && c > 0)
                costumes.Add((p, curActor, c));
        }
        return costumes;
    }

    /// <summary>
    /// Matches actors to image-less objects by how much of the object's rect the actor's frame
    /// covers. <paramref name="bounds"/> gives each actor's frame bounds in room coordinates.
    /// </summary>
    public static void MatchObjects(IReadOnlyList<RoomActor> actors, IReadOnlyList<RoomObject> candidates,
        Func<RoomActor, (int Left, int Top, int Right, int Bottom)?> bounds)
    {
        var scores = new List<(double Score, RoomActor Actor, RoomObject Object)>();
        foreach (var actor in actors)
        {
            if (bounds(actor) is not { } f) continue;
            double frameArea = Math.Max(1, (double)(f.Right - f.Left) * (f.Bottom - f.Top));
            foreach (var o in candidates)
            {
                if (o.Width <= 0 || o.Height <= 0) continue;
                int iw = Math.Min(f.Right, o.X + o.Width) - Math.Max(f.Left, o.X);
                int ih = Math.Min(f.Bottom, o.Y + o.Height) - Math.Max(f.Top, o.Y);
                if (iw <= 0 || ih <= 0) continue;
                double inter = (double)iw * ih;
                double objCover = inter / ((double)o.Width * o.Height);
                double frameCover = inter / frameArea;
                // The frame covers most of the hotspot, or sits mostly inside it (e.g. only hands and
                // cards are a costume, the body is part of the background).
                bool covers = objCover >= 0.5 && frameCover >= 0.15;
                bool inside = frameCover >= 0.8 && objCover >= 0.05;
                if (!covers && !inside) continue;
                scores.Add((objCover + frameCover, actor, o));
            }
        }

        // An actor is one character: its best match claims the object, and its other costumes
        // (alternate animations of the same character) are named after it rather than taking
        // hotspots of their own.
        var usedObjects = new HashSet<RoomObject>();
        var characters = new Dictionary<int, RoomObject>();
        foreach (var (_, actor, obj) in scores.OrderByDescending(s => s.Score))
        {
            if (actor.Object != null || usedObjects.Contains(obj)) continue;
            if (actor.Actor > 0 && characters.ContainsKey(actor.Actor)) continue;
            actor.Object = obj;
            usedObjects.Add(obj);
            if (actor.Actor > 0) characters[actor.Actor] = obj;
        }
        foreach (var actor in actors)
            if (actor.Object == null && actor.Actor > 0 && characters.TryGetValue(actor.Actor, out var character))
                actor.Character = character;
    }
}
