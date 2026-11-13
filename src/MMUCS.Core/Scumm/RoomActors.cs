using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace MMUCS.Core.Scumm;

/// <summary>
/// One of a room's costumes: the actor the room's scripts dress in it and where they put it (when
/// that can be read), and the object (hotspot) it belongs to, matched by overlap or linked by hand.
/// </summary>
public sealed class RoomActor
{
    /// <summary>Actor number from the scripts, or 0 when no script sets this costume with literals.</summary>
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
    public bool HasPosition { get; init; }
    public AkosData Data { get; init; }
    /// <summary>The frame shown in the room (the stand/init chore), or null when it draws nothing.</summary>
    public AkosFrame Frame { get; init; }
    /// <summary>The object this costume belongs to, if any.</summary>
    public RoomObject Object { get; set; }
    /// <summary>True when <see cref="Object"/> comes from a manual link rather than overlap.</summary>
    public bool IsLinked { get; set; }
    /// <summary>
    /// For an actor whose other costume is matched to an object: that object (the character it
    /// plays). It names this costume too, without claiming the hotspot.
    /// </summary>
    public RoomObject Character { get; set; }
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
            var b = Frame.Bounds;
            return (b.Left + X, b.Top + DrawY, b.Right + X, b.Bottom + DrawY);
        }
    }

    private string ActorText => Actors is { Count: > 1 } all ? $"actors {string.Join(", ", all)}" : $"actor {Actor}";

    public string Label => Actor <= 0 ? $"costume {Costume}"
        : IsPlayer ? $"costume {Costume}  (player, {ActorText})"
        : Character != null && Object == null ? $"costume {Costume}  ({ActorText}, {Character.Name})"
        : $"costume {Costume}  ({ActorText})";

    public override string ToString() => HasPosition ? $"{Label} at {X},{Y}{(Elevation != 0 ? $" elevation {Elevation}" : "")}" : Label;
}

/// <summary>
/// Finds the costumes of a room and the objects they belong to. Objects don't reference costumes:
/// the room's scripts set an actor's costume (actorOps setCurActor / setCostume) and put it in the
/// room (putActorAtXY). This reads those calls when their arguments are literal pushes, resolves
/// costume numbers through the index's DCOS directory, and pairs each actor with the hotspot its
/// frame covers. Manual links (room / object name → costume) override the matching.
/// </summary>
public static class RoomActors
{
    private const byte OpPushWord = 0x01, OpPushVar = 0x02, OpPutActorAtXY = 0xA1, OpAnimateActor = 0xA4, OpActorOps = 0xAC;
    private const byte SubSetCostume = 0x64, SubElevation = 0x6E, SubSetCurActor = 0x7A, SubFace = 0x81;
    private const byte OpAdd = 0x0E, OpSub = 0x0F, OpMul = 0x10, OpDiv = 0x11;

    private static readonly object Sync = new();
    private static readonly ConditionalWeakTable<ScummBlock, CostumeDirectory> Directories = new();
    private static readonly ConditionalWeakTable<ScummBlock, List<RoomActor>> Analyses = new();
    private static readonly ConditionalWeakTable<ScummBlock, StrongBox<int>> Players = new();
    /// <summary>AKOS block → name of the object it belongs to (filled as rooms are analysed).</summary>
    private static readonly ConditionalWeakTable<ScummBlock, string> AkosObjectNames = new();

    private static Dictionary<string, int> _links = new();

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
                _links = value ?? new();
                Analyses.Clear();
            }
        }
    }

    public static string LinkKey(int roomNo, string objectName) => $"{roomNo}/{objectName}";

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

    /// <summary>Costume number → AKOS block, from the index file's DCOS.</summary>
    private sealed class CostumeDirectory
    {
        private readonly Dictionary<int, (int Room, uint Offset)> _entries = new();
        private readonly Dictionary<int, ScummBlock> _lflfByRoom = new();
        private readonly Dictionary<(int Room, long Offset), int> _byLocation = new();

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

    private static CostumeDirectory DirectoryFor(ScummBlock b)
    {
        lock (Sync) return Directories.GetValue(Root(b), r => new CostumeDirectory(r));
    }

    private static bool IsScriptTag(uint tag) => tag is ScummTag.LSCR or ScummTag.ENCD or ScummTag.EXCD or ScummTag.SCRP or ScummTag.VERB;

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
                    foreach (var c in CostumeSets(script.DataSpan)) actors.Add(c.Actor);
            foreach (int a in actors) rooms[a] = rooms.GetValueOrDefault(a) + 1;
        }
        var ranked = rooms.OrderByDescending(kv => kv.Value).ToList();
        int player = ranked.Count > 0 && ranked[0].Value >= 5 && (ranked.Count == 1 || ranked[0].Value >= 2 * ranked[1].Value)
            ? ranked[0].Key : -1;
        lock (Sync) Players.AddOrUpdate(root, new StrongBox<int>(player));
        return player;
    }

    /// <summary>The costume number of an AKOS block, from the index (null without an index).</summary>
    public static int? CostumeNumber(ScummBlock akos) => akos?.Tag == ScummTag.AKOS ? DirectoryFor(akos).CostumeOf(akos) : null;

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
        if (lflf == null) return new();
        lock (Sync)
            if (Analyses.TryGetValue(lflf, out var cached)) return cached;

        parser ??= new AkosParser();
        var directory = DirectoryFor(lflf);
        var akosBlocks = lflf.Children.Where(c => c.Tag == ScummTag.AKOS).ToList();
        var data = new Dictionary<ScummBlock, (AkosData Data, AkosFrame Frame)>();
        (AkosData, AkosFrame) Load(ScummBlock akos)
        {
            if (data.TryGetValue(akos, out var v)) return v;
            var d = parser.Parse(akos);
            int chore = AkosFrames.FindDefaultChore(d);
            var f = chore >= 0 ? AkosFrames.Compose(d, chore) : null;
            return data[akos] = (d, f is { IsEmpty: false } ? f : null);
        }

        // Script pairs (actor, costume, position) for the room's own costumes.
        int player = PlayerActor(lflf);
        var scripts = Walk(lflf, 4).Where(b => IsScriptTag(b.Tag)).ToList();
        var scans = scripts.Select(b => ScanScript(b.DataSpan, room.RoomNo)).ToList();
        // Animations the room's scripts play on each actor, and which costumes each actor wears here.
        var roomAnims = new Dictionary<int, List<int>>();
        foreach (var b in scripts)
            foreach (var (_, actor, anim) in Animations(b.DataSpan))
                if (anim is >= 0 and < 1000) (roomAnims.TryGetValue(actor, out var l) ? l : roomAnims[actor] = new()).Add(anim);
        var wears = scans.SelectMany(x => x).GroupBy(x => x.Actor).ToDictionary(g => g.Key, g => g.Select(x => x.Costume).Distinct().Count());

        var placed = new List<RoomActor>();
        var scriptOf = new Dictionary<RoomActor, int>();
        var seen = new HashSet<(int, int)>();
        foreach (var (si, (actor, costume, x, y, elevation, anim, dir)) in scans.SelectMany((list, i) => list.Select(e => (i, e))))
        {
            if (!seen.Add((actor, costume))) continue;
            var akos = directory.Resolve(costume, lflf);
            if (akos == null || !ReferenceEquals(akos.Parent, lflf)) continue;
            var (d, f) = Load(akos);
            // Show the animation the script starts (e.g. a full-screen close-up whose stand chore is
            // empty). If that's blank and so is the default pose (the costume starts hidden), use
            // another animation the room plays on the same actor, as long as the actor wears only this
            // costume here (banjo's close-up appears from a loop in another script).
            if (Animated(d, anim, dir) is { } started) f = started;
            else if (f == null && wears.GetValueOrDefault(actor) == 1 && roomAnims.TryGetValue(actor, out var others))
                f = others.Distinct().Select(o => Animated(d, o, dir)).FirstOrDefault(x => x != null);
            var entry = new RoomActor { Actor = actor, Costume = costume, Akos = akos, X = x, Y = y, Elevation = elevation, HasPosition = true, Data = d, Frame = f, IsPlayer = actor == player };
            placed.Add(entry);
            scriptOf[entry] = si;
        }
        placed = MergeLayers(placed, scriptOf);

        // Image-less hotspots, except exits: a room's doors are walk-through areas, never a costume
        // (full-room overlays such as waterlines overlap them by chance).
        var candidates = room.Objects.Where(o => o.ImageCount == 0 && !IsExit(o)).ToList();
        MatchObjects(placed.Where(p => !p.IsPlayer).ToList(), candidates, a => a.RoomBounds);

        // One entry per AKOS: its matched placements, else its first placement, else an unplaced entry.
        var result = new List<RoomActor>();
        foreach (var akos in akosBlocks)
        {
            var mine = placed.Where(p => ReferenceEquals(p.Akos, akos)).ToList();
            var matched = mine.Where(p => p.Object != null).ToList();
            if (matched.Count > 0) result.AddRange(matched);
            else if ((mine.FirstOrDefault(p => p.CanDraw) ?? mine.FirstOrDefault()) is { } first) result.Add(first);
            else if (directory.CostumeOf(akos) is int costume)
            {
                var (d, f) = Load(akos);
                result.Add(new RoomActor { Costume = costume, Akos = akos, Data = d, Frame = f });
            }
        }

        ApplyLinks(room, result, directory, Load);

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

    internal static bool Const(ReadOnlySpan<byte> s, int end, out int v) => Const(s, end, out v, out _);

    /// <summary>
    /// The script's costume placements: actor, costume, position, and the animation the script
    /// starts for it (do-animation after the costume is set; -1 for none) with the facing in degrees
    /// (south unless a face op says otherwise) that picks its chore. Positions may be constant
    /// expressions ("320,492 + 300"); the elevation is the last one set between the costume and the placement.
    /// </summary>
    internal static List<(int Actor, int Costume, int X, int Y, int Elevation, int Anim, int Dir)> ScanScript(ReadOnlySpan<byte> s, int roomNo)
    {
        var costumes = CostumeSets(s);
        var anims = Animations(s);
        var faces = new List<(int Pos, int Actor, int Dir)>(); // Dir in degrees
        var elevations = new List<(int Pos, int Actor, int Elevation)>();
        int curActor = -1;
        for (int p = 5; p + 1 < s.Length; p++)
        {
            if (s[p] != OpActorOps) continue;
            if (s[p + 1] == SubSetCurActor && Const(s, p, out int ca)) curActor = ca;
            else if (s[p + 1] == SubFace && curActor > 0 && Const(s, p, out int degrees)) faces.Add((p, curActor, degrees));
            else if (s[p + 1] == SubElevation && curActor > 0 && Const(s, p, out int elevation)) elevations.Add((p, curActor, elevation));
        }
        var places = new List<(int Pos, int Actor, int X, int Y)>();

        for (int p = 20; p < s.Length; p++)
        {
            if (s[p] == OpPutActorAtXY)
            {
                // Arguments are pushed actor, x, y, room: read them back to front.
                int yEnd;
                if (Const(s, p, out int r, out int roomStart)) { if (r != roomNo) continue; yEnd = roomStart; }
                else if (s[p - 5] == OpPushVar) yEnd = p - 5; // usually the current room
                else continue;
                // 0,0 counts too: full-room overlays (waterlines) sit at the room's origin. Parking an
                // actor out of the way is done with room 0, which the room check already rejects.
                if (Const(s, yEnd, out int y, out int xEnd) && Const(s, xEnd, out int x, out int aEnd) && Const(s, aEnd, out int a) && a > 0)
                    places.Add((p, a, x, y));
            }
        }

        var pairs = new List<(int, int, int, int, int, int, int)>();
        foreach (var c in costumes)
        {
            // Until the actor's next costume change in this script.
            int until = costumes.Where(o => o.Actor == c.Actor && o.Pos > c.Pos).Select(o => o.Pos).DefaultIfEmpty(int.MaxValue).Min();
            // Below 1000 an animation number is a chore; 1000 and up are turn/stop commands.
            int anim = anims.Where(a => a.Actor == c.Actor && a.Pos > c.Pos && a.Pos < until && a.Anim is >= 0 and < 1000)
                            .Select(a => a.Anim).DefaultIfEmpty(-1).First();
            int dir = faces.Where(f => f.Actor == c.Actor && f.Pos > c.Pos && f.Pos < until).Select(f => f.Dir).DefaultIfEmpty(180).First();

            // The first placement of the actor while it wears this costume (after the costume is set,
            // before its next change); otherwise the nearest one, preferring those after.
            (int Pos, int Actor, int X, int Y)? best = places
                .Where(pl => pl.Actor == c.Actor && pl.Pos > c.Pos && pl.Pos < until)
                .Select(pl => ((int, int, int, int)?)pl).FirstOrDefault();
            int bestDist = int.MaxValue;
            if (best == null)
                foreach (var pl in places)
                {
                    if (pl.Actor != c.Actor) continue;
                    int dist = pl.Pos >= c.Pos ? pl.Pos - c.Pos : (c.Pos - pl.Pos) * 2;
                    if (dist < bestDist) { bestDist = dist; best = pl; }
                }
            if (best is { } b)
            {
                // Only an elevation set while it wears this costume, before this placement, moves the
                // picture. The common "elevation E, then put-actor at x-of(a), y-of(a) + E" re-places the
                // actor after the literal placement so it's drawn where it was (only its depth changes).
                bool inWindow = b.Pos > c.Pos && b.Pos < until;
                int elevation = !inWindow ? 0 : elevations.Where(e => e.Actor == c.Actor && e.Pos > c.Pos && e.Pos < b.Pos)
                                                          .Select(e => e.Elevation).DefaultIfEmpty(0).Last();
                pairs.Add((c.Actor, c.Costume, b.X, b.Y, elevation, anim, dir));
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
            var layers = p.IsPlayer ? new List<RoomActor> { p } : placed
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
                Actor = p.Actor, Actors = layers.Select(l => l.Actor).Distinct().ToList(), Costume = p.Costume, Akos = p.Akos,
                X = p.X, Y = p.Y, Elevation = p.Elevation, HasPosition = true, Data = p.Data, Frame = frame.IsEmpty ? null : frame,
            });
        }
        return result;
    }

    /// <summary>An exit hotspot: COMI names them "…-door" (optionally numbered: "town-plndrmap-door-1", "town-door-1a").</summary>
    private static bool IsExit(RoomObject o) =>
        o.Name != null && System.Text.RegularExpressions.Regex.IsMatch(o.Name, @"(^|-)door(-\d+[a-z]?)?$");

    /// <summary>The first frame of animation <paramref name="anim"/> facing <paramref name="degrees"/>, or null if it draws nothing.</summary>
    private static AkosFrame Animated(AkosData d, int anim, int degrees)
    {
        if (anim < 0) return null;
        int chore = AkosFrames.ChoreIndex(d, anim, AkosFrames.DirectionOf(d, degrees));
        return AkosFrames.HasChore(d, chore) && AkosFrames.Compose(d, chore) is { IsEmpty: false } f ? f : null;
    }

    /// <summary>do-animation ops with literal arguments: (position, actor, animation).</summary>
    private static List<(int Pos, int Actor, int Anim)> Animations(ReadOnlySpan<byte> s)
    {
        var anims = new List<(int Pos, int Actor, int Anim)>();
        for (int p = 10; p < s.Length; p++)
            if (s[p] == OpAnimateActor && Const(s, p, out int anim, out int animStart) && Const(s, animStart, out int actor))
                anims.Add((p, actor, anim));
        return anims;
    }

    /// <summary>actorOps setCurActor / setCostume pairs with literal arguments.</summary>
    private static List<(int Pos, int Actor, int Costume)> CostumeSets(ReadOnlySpan<byte> s)
    {
        var costumes = new List<(int Pos, int Actor, int Costume)>();
        int curActor = -1;
        for (int p = 5; p + 1 < s.Length; p++)
        {
            if (s[p] != OpActorOps) continue;
            byte sub = s[p + 1];
            if (sub == SubSetCurActor && Const(s, p, out int a)) curActor = a;
            else if (sub == SubSetCostume && curActor > 0 && Const(s, p, out int c) && c > 0)
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
