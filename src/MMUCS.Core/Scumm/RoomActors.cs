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
    public int Costume { get; init; }
    public ScummBlock Akos { get; init; }
    /// <summary>Actor origin in the room (valid when <see cref="HasPosition"/>).</summary>
    public int X { get; init; }
    public int Y { get; init; }
    public bool HasPosition { get; init; }
    public AkosData Data { get; init; }
    /// <summary>The frame shown in the room (the stand/init chore), or null when it draws nothing.</summary>
    public AkosFrame Frame { get; init; }
    /// <summary>The object this costume belongs to, if any.</summary>
    public RoomObject Object { get; set; }
    /// <summary>True when <see cref="Object"/> comes from a manual link rather than overlap.</summary>
    public bool IsLinked { get; set; }

    public bool CanDraw => HasPosition && Frame is { IsEmpty: false };

    /// <summary>Frame bounds in room coordinates (when it can be drawn).</summary>
    public (int Left, int Top, int Right, int Bottom)? RoomBounds
    {
        get
        {
            if (!CanDraw) return null;
            var b = Frame.Bounds;
            return (b.Left + X, b.Top + Y, b.Right + X, b.Bottom + Y);
        }
    }

    public string Label => Actor > 0 ? $"costume {Costume}  (actor {Actor})" : $"costume {Costume}";

    public override string ToString() => HasPosition ? $"{Label} at {X},{Y}" : Label;
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
    private const byte OpPushWord = 0x01, OpPushVar = 0x02, OpPutActorAtXY = 0xA1, OpActorOps = 0xAC;
    private const byte SubSetCostume = 0x64, SubSetCurActor = 0x7A;

    private static readonly object Sync = new();
    private static readonly ConditionalWeakTable<ScummBlock, CostumeDirectory> Directories = new();
    private static readonly ConditionalWeakTable<ScummBlock, List<RoomActor>> Analyses = new();
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
        var placed = new List<RoomActor>();
        var seen = new HashSet<(int, int)>();
        foreach (var script in Walk(lflf, 4))
        {
            if (script.Tag is not (ScummTag.LSCR or ScummTag.ENCD or ScummTag.EXCD or ScummTag.SCRP or ScummTag.VERB)) continue;
            foreach (var (actor, costume, x, y) in ScanScript(script.DataSpan, room.RoomNo))
            {
                if (!seen.Add((actor, costume))) continue;
                var akos = directory.Resolve(costume, lflf);
                if (akos == null || !ReferenceEquals(akos.Parent, lflf)) continue;
                var (d, f) = Load(akos);
                placed.Add(new RoomActor { Actor = actor, Costume = costume, Akos = akos, X = x, Y = y, HasPosition = true, Data = d, Frame = f });
            }
        }

        var candidates = room.Objects.Where(o => o.ImageCount == 0).ToList();
        MatchObjects(placed, candidates, a => a.RoomBounds);

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
                var owner = result.FirstOrDefault(r => ReferenceEquals(r.Akos, akos) && r.Object != null);
                AkosObjectNames.AddOrUpdate(akos, owner?.Object.Name);
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

    /// <summary>
    /// Pairs costume assignments with placements of the same actor within one script. Only literal
    /// arguments are understood; the room argument of putActorAtXY may be a variable (usually the
    /// current room) but not another literal room.
    /// </summary>
    private static bool Lit(ReadOnlySpan<byte> s, int p, out int v)
    {
        v = 0;
        if (p < 0 || p + 5 > s.Length || s[p] != OpPushWord) return false;
        v = s.S32LE(p + 1);
        return true;
    }

    private static List<(int Actor, int Costume, int X, int Y)> ScanScript(ReadOnlySpan<byte> s, int roomNo)
    {
        var costumes = new List<(int Pos, int Actor, int Costume)>();
        var places = new List<(int Pos, int Actor, int X, int Y)>();
        int curActor = -1;

        for (int p = 5; p + 1 < s.Length; p++)
        {
            if (s[p] == OpActorOps)
            {
                byte sub = s[p + 1];
                if (sub == SubSetCurActor && Lit(s, p - 5, out int a)) curActor = a;
                else if (sub == SubSetCostume && curActor > 0 && Lit(s, p - 5, out int c) && c > 0)
                    costumes.Add((p, curActor, c));
            }
            else if (s[p] == OpPutActorAtXY && p >= 20)
            {
                bool roomOk = Lit(s, p - 5, out int r) ? r == roomNo : s[p - 5] == OpPushVar;
                if (roomOk && Lit(s, p - 10, out int y) && Lit(s, p - 15, out int x) && Lit(s, p - 20, out int a) && a > 0 && (x != 0 || y != 0))
                    places.Add((p, a, x, y));
            }
        }

        var pairs = new List<(int, int, int, int)>();
        foreach (var c in costumes)
        {
            // Nearest placement of the same actor, preferring one after the costume is set.
            (int Pos, int Actor, int X, int Y)? best = null;
            int bestDist = int.MaxValue;
            foreach (var pl in places)
            {
                if (pl.Actor != c.Actor) continue;
                int dist = pl.Pos >= c.Pos ? pl.Pos - c.Pos : (c.Pos - pl.Pos) * 2;
                if (dist < bestDist) { bestDist = dist; best = pl; }
            }
            if (best is { } b) pairs.Add((c.Actor, c.Costume, b.X, b.Y));
        }
        return pairs;
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

        var usedObjects = new HashSet<RoomObject>();
        foreach (var (_, actor, obj) in scores.OrderByDescending(s => s.Score))
        {
            if (actor.Object != null || usedObjects.Contains(obj)) continue;
            actor.Object = obj;
            usedObjects.Add(obj);
        }
    }
}
