using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace MMUCS.Core.Scumm;

/// <summary>
/// Writes what MMUCS knows about each room as JSON, for tools that read an export (tools/mroom):
/// <code>
/// out/data/game.json                 every object in the game's object table (DOBJ): name, room, state, classes
/// out/data/rooms/009_cannon.json     size, objects (id, hover name, verbs, image box, walk-to points),
///                                    walkboxes, costumes and the actors the scripts put there
/// </code>
/// Room files are named like the room folders of the other exports. A room that appears in more than
/// one file (per-disc copies) is written once, from its first copy.
/// </summary>
public static class RoomDataExport
{
    public const int FormatVersion = 1;

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep quotes and apostrophes in names readable
    };

    /// <summary>One object from DOBJ: v8 layout is name[40], state, room, class u32.</summary>
    public readonly record struct GameObject(int Id, string Name, int State, int Room, uint Classes);

    public static List<GameObject> ReadObjectTable(ScummBlock root)
    {
        var list = new List<GameObject>();
        var dobj = root.FindChildrenRecursive(ScummTag.DOBJ).FirstOrDefault();
        if (dobj == null) return list;
        var d = dobj.DataSpan;
        if (d.Length < 4) return list;
        int n = (int)d.U32LE(0);
        for (int i = 0; i < n && 4 + (i + 1) * 46 <= d.Length; i++)
        {
            var e = d.Slice(4 + i * 46, 46);
            int len = e[..40].IndexOf((byte)0);
            if (len < 0) len = 40;
            if (len == 0) continue;
            list.Add(new GameObject(i, Encoding.Latin1.GetString(e[..len]), e[40], e[41], e.U32LE(42)));
        }
        return list;
    }

    /// <summary>SCUMM class numbers (1-based) from a class bit field.</summary>
    public static List<int> ClassList(uint bits)
    {
        var list = new List<int>();
        for (int b = 0; b < 32; b++)
            if ((bits & (1u << b)) != 0) list.Add(b + 1);
        return list;
    }

    /// <summary>"/TWNZ518/plaque" → "plaque" (the text after a message code).</summary>
    public static string StripMessageCode(string s)
    {
        if (string.IsNullOrEmpty(s) || s[0] != '/') return s;
        int end = s.IndexOf('/', 1);
        return end > 0 ? s[(end + 1)..] : s;
    }

    public static void WriteGame(ScummBlock root, string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        using var stream = File.Create(Path.Combine(dataDir, "game.json"));
        using var w = new Utf8JsonWriter(stream, WriterOptions);
        w.WriteStartObject();
        w.WriteNumber("format", FormatVersion);
        w.WriteStartArray("objects");
        foreach (var o in ReadObjectTable(root))
        {
            w.WriteStartObject();
            w.WriteNumber("id", o.Id);
            w.WriteString("name", o.Name);
            w.WriteNumber("room", o.Room);
            w.WriteNumber("state", o.State);
            WriteInts(w, "classes", ClassList(o.Classes));
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    /// <summary>Writes data/rooms/&lt;folder&gt;.json for one room.</summary>
    public static void WriteRoom(RoomModel room, string folder, string dataDir, Dictionary<int, GameObject> table)
    {
        string dir = Path.Combine(dataDir, "rooms");
        Directory.CreateDirectory(dir);
        using var stream = File.Create(Path.Combine(dir, folder + ".json"));
        using var w = new Utf8JsonWriter(stream, WriterOptions);
        var lflf = room.Lflf;

        w.WriteStartObject();
        w.WriteNumber("format", FormatVersion);
        w.WriteNumber("room", room.RoomNo);
        w.WriteString("name", room.Name ?? "");
        w.WriteString("folder", folder);
        w.WriteString("file", ScummExporters.FileRootName(lflf));
        w.WriteNumber("width", room.Width);
        w.WriteNumber("height", room.Height);
        w.WriteNumber("palettes", room.PaletteCount);
        w.WriteNumber("sounds", lflf.Children.Count(c => c.Tag == ScummTag.SOUN));

        WriteObjects(w, room, table);
        WriteBoxes(w, room);
        WriteCostumes(w, room);

        // Global scripts whose code is stored in this room's file (not where they run).
        var globals = RoomScripts.List(lflf).Where(e => e.Group == "Global")
            .Select(e => e.Title.StartsWith("Global script ") && int.TryParse(e.Title[14..], out int n) ? n : -1)
            .Where(n => n >= 0).ToList();
        WriteInts(w, "global_scripts_stored", globals);
        w.WriteEndObject();
    }

    private static void WriteObjects(Utf8JsonWriter w, RoomModel room, Dictionary<int, GameObject> table)
    {
        // Code (OBCD) carries the id, hover name and verbs; the image (OBIM) the placement. They're
        // matched by name, since OBIM only knows the object's name.
        var images = room.Objects.GroupBy(o => o.Name ?? "").ToDictionary(g => g.Key, g => new Queue<RoomObject>(g));
        Dictionary<RoomObject, int> entryStates;
        try { entryStates = RoomObjectStates.Initial(room); }
        catch { entryStates = new(); }
        var code = new List<(int Id, string Name, string Title, List<int> Verbs)>();
        var rmsc = room.Lflf.FindChild(ScummTag.RMSC);
        foreach (var obcd in (rmsc ?? room.Room).Children.Where(c => c.Tag == ScummTag.OBCD))
        {
            var cdhd = obcd.FindChild(ScummTag.CDHD);
            int id = cdhd != null && cdhd.DataLength >= 8 ? (int)cdhd.DataSpan.U32LE(4) : -1;
            string name = id >= 0 ? RoomScripts.ObjectName(obcd, id) : null;
            var obna = obcd.FindChild(ScummTag.OBNA);
            string title = null;
            if (obna != null)
            {
                var s = obna.DataSpan;
                int len = s.IndexOf((byte)0);
                title = StripMessageCode(Encoding.Latin1.GetString(len < 0 ? s : s[..len]));
            }
            code.Add((id, name, title, Verbs(obcd.FindChild(ScummTag.VERB))));
        }

        w.WriteStartArray("objects");
        foreach (var (id, name, title, verbs) in code)
        {
            RoomObject img = null;
            if (name != null && images.TryGetValue(name, out var q) && q.Count > 0) img = q.Dequeue();
            w.WriteStartObject();
            w.WriteNumber("id", id);
            w.WriteString("name", name ?? "");
            if (!string.IsNullOrEmpty(title)) w.WriteString("title", title);
            WriteInts(w, "verbs", verbs);
            if (table.TryGetValue(id, out var t))
            {
                w.WriteNumber("state", t.State);
                WriteInts(w, "classes", ClassList(t.Classes));
            }
            if (img != null)
            {
                // Its state when the room is entered (0 = hidden), as the room viewer starts it.
                if (entryStates.TryGetValue(img, out int entry)) w.WriteNumber("entry_state", entry);
                WriteImage(w, img);
            }
            w.WriteEndObject();
        }
        // Images with no code: scenery the scripts only draw.
        foreach (var img in images.Values.SelectMany(q => q))
        {
            w.WriteStartObject();
            w.WriteNull("id");
            w.WriteString("name", img.Name ?? "");
            WriteImage(w, img);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void WriteImage(Utf8JsonWriter w, RoomObject img)
    {
        // v8 IMHD: name[40], version, image count, x, y, width, height, actor dir, hotspot count, hotspots[15] (x, y).
        var s = img.Block.FindChild(ScummTag.IMHD).DataSpan;
        w.WriteStartObject("image");
        w.WriteNumber("x", img.X);
        w.WriteNumber("y", img.Y);
        w.WriteNumber("width", img.Width);
        w.WriteNumber("height", img.Height);
        w.WriteNumber("states", img.ImageCount);
        if (s.Length >= 68) w.WriteNumber("actor_dir", s.S32LE(64));
        // The point an actor walks to for each state (state 1 = hotspot 0), relative to the room,
        // as ScummVM reads it. A point at 0,0 is unset.
        var points = new List<(int X, int Y)>();
        for (int i = 0; i < Math.Max(1, img.ImageCount) && i < 15 && 80 + i * 8 <= s.Length; i++)
            points.Add((img.X + s.S32LE(72 + i * 8), img.Y + s.S32LE(76 + i * 8)));
        if (points.Any(p => p != (0, 0)))
        {
            w.WriteStartArray("walk_to");
            foreach (var (x, y) in points)
            {
                w.WriteStartArray();
                w.WriteNumberValue(x);
                w.WriteNumberValue(y);
                w.WriteEndArray();
            }
            w.WriteEndArray();
        }
        w.WriteEndObject();
    }

    private static List<int> Verbs(ScummBlock verb)
    {
        var list = new List<int>();
        if (verb == null) return list;
        var d = verb.DataSpan;
        for (int p = 0; p + 8 <= d.Length; p += 8)
        {
            int v = (int)d.U32LE(p);
            if (v == 0) break;
            list.Add(v);
        }
        return list;
    }

    private static void WriteBoxes(Utf8JsonWriter w, RoomModel room)
    {
        w.WriteStartArray("boxes");
        foreach (var b in room.Walkboxes)
        {
            if (b.IsPlaceholder) continue;
            w.WriteStartObject();
            w.WriteNumber("index", b.Index);
            w.WriteStartArray("corners");
            foreach (var p in new[] { b.UpperLeft, b.UpperRight, b.LowerRight, b.LowerLeft })
            {
                w.WriteStartArray();
                w.WriteNumberValue(p.X);
                w.WriteNumberValue(p.Y);
                w.WriteEndArray();
            }
            w.WriteEndArray();
            w.WriteNumber("mask", b.Mask);
            w.WriteNumber("flags", b.Flags);
            w.WriteNumber("scale", b.Scale);
            w.WriteNumber("scale_slot", b.ScaleSlot);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void WriteCostumes(Utf8JsonWriter w, RoomModel room)
    {
        List<RoomActor> actors;
        try { actors = RoomActors.Analyze(room); }
        catch { actors = new(); }

        w.WriteStartArray("costumes");
        foreach (var akos in room.Lflf.Children.Where(c => c.Tag == ScummTag.AKOS))
        {
            w.WriteStartObject();
            if (RoomActors.CostumeNumber(akos) is int no) w.WriteNumber("costume", no);
            else w.WriteNull("costume");
            w.WriteString("akos", $"0x{akos.Offset:X8}");
            var uses = actors.Where(a => ReferenceEquals(a.Akos, akos)).ToList();
            var owner = uses.Select(a => a.Owner?.Name).FirstOrDefault(n => !string.IsNullOrEmpty(n));
            if (owner != null) w.WriteString("object", owner);
            // An actor a script dresses in it but places through variables: worn, never placed.
            var worn = uses.Where(a => a.Actor > 0 && !a.HasPosition).SelectMany(a => a.Actors ?? new[] { a.Actor }).Distinct().ToList();
            if (worn.Count > 0) WriteInts(w, "worn_by", worn);
            // Offered by a script a script here starts (script 88's chickens), but placed nowhere readable.
            if (!uses.Any(a => a.HasPosition) && uses.FirstOrDefault(a => a.DressedBy >= 0) is { } offered)
                w.WriteNumber("dressed_by", offered.DressedBy);
            w.WriteStartArray("placed");
            foreach (var a in uses.Where(a => a.HasPosition))
            {
                w.WriteStartObject();
                // No actor number when the script keeps it in a variable (the fortbase chickens).
                if (a.Actor > 0) w.WriteNumber("actor", a.Actor);
                if (a.DressedBy >= 0) w.WriteNumber("dressed_by", a.DressedBy);
                w.WriteNumber("x", a.X);
                w.WriteNumber("y", a.Y);
                // Drawn this far above y: the picture's origin is at y - elevation.
                if (a.Elevation != 0) w.WriteNumber("elevation", a.Elevation);
                if (a.Scale != 255) w.WriteNumber("scale", a.Scale);
                if (a.ZPlane > 0) w.WriteNumber("zplane", a.ZPlane);
                if (WhenText(a.When) is { } when) w.WriteString("when", when);
                // Shown as the room opens: what the room viewer starts with.
                if (a.StartsShown) w.WriteBoolean("starts_shown", true);
                // Drawn in front of the actors its script takes out of the room while it plays.
                if (a.SortY != a.Y) w.WriteNumber("sort_y", a.SortY);
                if (a.Removes.Count > 0) WriteInts(w, "removes", a.Removes);
                if (a.IsPlayer) w.WriteBoolean("player", true);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    /// <summary>"entry", "entry-if", "cutscene" or "later" (null when not worked out).</summary>
    public static string WhenText(PlacementTime t) => t switch
    {
        PlacementTime.OnEntry => "entry",
        PlacementTime.OnEntryIf => "entry-if",
        PlacementTime.Cutscene => "cutscene",
        PlacementTime.Later => "later",
        _ => null,
    };

    private static void WriteInts(Utf8JsonWriter w, string name, IEnumerable<int> values)
    {
        w.WriteStartArray(name);
        foreach (int v in values) w.WriteNumberValue(v);
        w.WriteEndArray();
    }
}
