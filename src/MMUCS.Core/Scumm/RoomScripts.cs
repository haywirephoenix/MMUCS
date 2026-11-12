using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

namespace MMUCS.Core.Scumm;

/// <summary>A code range inside a script block's data, e.g. one verb of an object.</summary>
public readonly record struct ScriptPart(string Label, int Start, int End);

/// <summary>A script as listed for a room: a global / local / entry / exit script or an object's verbs.</summary>
public sealed class ScriptEntry
{
    public string Title { get; init; }
    public string Detail { get; init; }
    public string Group { get; init; }
    public ScummBlock Block { get; init; }
    /// <summary>The code ranges in <see cref="Block"/>'s data: one, or one per verb.</summary>
    public IReadOnlyList<ScriptPart> Parts { get; init; } = Array.Empty<ScriptPart>();
    public override string ToString() => Title;
}

/// <summary>
/// Lists a room's scripts and turns them into SCUMM source or an instruction listing.
/// Global script numbers come from the DSCR directory, object names from DOBJ.
/// </summary>
public static class RoomScripts
{
    private sealed class Index
    {
        public readonly Dictionary<(int Room, long Offset), int> ScriptAt = new();
        public readonly Dictionary<int, string> ObjectNames = new();
    }

    private static readonly ConditionalWeakTable<ScummBlock, Index> Indexes = new();

    private static ScummBlock RootOf(ScummBlock b)
    {
        while (b.Parent != null) b = b.Parent;
        return b;
    }

    private static Index IndexFor(ScummBlock any) => Indexes.GetValue(RootOf(any), Build);

    private static Index Build(ScummBlock root)
    {
        var index = new Index();
        ScummBlock dscr = null, dobj = null;
        foreach (var b in Walk(root, 3))
        {
            if (b.Tag == ScummTag.DSCR) dscr ??= b;
            else if (b.Tag == ScummTag.DOBJ) dobj ??= b;
        }

        if (dscr != null)
        {
            // Same layout as DCOS: count, then rooms[count], then offsets[count] (relative to the LFLF).
            var d = dscr.DataSpan;
            if (d.Length >= 2)
            {
                int n = d.U16LE(0);
                int header = d.Length >= 4 + n * 5 && d.U16LE(2) == 0 ? 4 : 2;
                if (d.Length >= header + n * 5)
                    for (int i = 0; i < n; i++)
                    {
                        int room = d[header + i];
                        long offset = d.U32LE(header + n + i * 4);
                        if (room != 0 || offset != 0) index.ScriptAt.TryAdd((room, offset), i);
                    }
            }
        }

        if (dobj != null)
        {
            // v8: u32 count, then per object: name[40], state, room, class u32.
            var d = dobj.DataSpan;
            if (d.Length >= 4)
            {
                int n = (int)d.U32LE(0);
                for (int i = 0; i < n && 4 + (i + 1) * 46 <= d.Length; i++)
                {
                    var name = d.Slice(4 + i * 46, 40);
                    int len = name.IndexOf((byte)0);
                    if (len < 0) len = 40;
                    if (len > 0) index.ObjectNames[i] = Encoding.Latin1.GetString(name[..len]);
                }
            }
        }
        return index;
    }

    /// <summary>An object's name from the game's object table, or null.</summary>
    public static string ObjectName(ScummBlock any, int id)
        => any != null && IndexFor(any).ObjectNames.TryGetValue(id, out var n) ? n : null;

    private static int? RoomNo(ScummBlock lflf)
    {
        var meta = lflf?.GetMetaDataDict();
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

    public static bool IsScript(ScummBlock b) => b != null &&
        (b.Tag is ScummTag.SCRP or ScummTag.LSCR or ScummTag.ENCD or ScummTag.EXCD || IsObjectCode(b));

    private static bool IsObjectCode(ScummBlock b) => b.Tag is ScummTag.OBCD or ScummTag.VERB;

    /// <summary>The scripts of the room <paramref name="lflf"/>, in a stable order.</summary>
    public static List<ScriptEntry> List(ScummBlock lflf)
    {
        var result = new List<ScriptEntry>();
        if (lflf == null) return result;
        foreach (var b in Walk(lflf, 3))
        {
            if (b.Tag is ScummTag.ENCD or ScummTag.EXCD or ScummTag.LSCR or ScummTag.SCRP or ScummTag.OBCD
                && For(b) is { } entry)
                result.Add(entry);
        }
        int Rank(ScriptEntry e) => e.Group switch { "Room" => 0, "Local" => 1, "Global" => 2, _ => 3 };
        return result.Select((e, i) => (e, i)).OrderBy(x => Rank(x.e)).ThenBy(x => x.i).Select(x => x.e).ToList();
    }

    /// <summary>The entry for one script block (a VERB resolves to its object), or null.</summary>
    public static ScriptEntry For(ScummBlock b)
    {
        if (b == null) return null;
        if (b.Tag == ScummTag.VERB && b.Parent?.Tag == ScummTag.OBCD) b = b.Parent;
        int len = b.DataLength;
        switch (b.Tag)
        {
            case ScummTag.ENCD:
                return new ScriptEntry { Title = "Room entry", Detail = "ENCD", Group = "Room", Block = b, Parts = new[] { new ScriptPart(null, 0, len) } };
            case ScummTag.EXCD:
                return new ScriptEntry { Title = "Room exit", Detail = "EXCD", Group = "Room", Block = b, Parts = new[] { new ScriptPart(null, 0, len) } };
            case ScummTag.LSCR:
            {
                int no = len >= 4 ? (int)b.DataSpan.U32LE(0) : -1;
                return new ScriptEntry { Title = $"Local script {no}", Detail = $"LSCR  {len:N0} bytes", Group = "Local", Block = b, Parts = new[] { new ScriptPart(null, 4, len) } };
            }
            case ScummTag.SCRP:
            {
                var lflf = b.FindParent(ScummTag.LFLF);
                string title = "Global script";
                if (RoomNo(lflf) is int room && IndexFor(b).ScriptAt.TryGetValue((room, b.Offset - lflf.Offset), out int no))
                    title = $"Global script {no}";
                return new ScriptEntry { Title = title, Detail = $"SCRP  {len:N0} bytes", Group = "Global", Block = b, Parts = new[] { new ScriptPart(null, 0, len) } };
            }
            case ScummTag.OBCD:
                return ObjectEntry(b);
        }
        return null;
    }

    private static ScriptEntry ObjectEntry(ScummBlock obcd)
    {
        var verb = obcd.FindChild(ScummTag.VERB);
        var cdhd = obcd.FindChild(ScummTag.CDHD);
        if (verb == null) return null;
        int id = cdhd != null && cdhd.DataLength >= 8 ? (int)cdhd.DataSpan.U32LE(4) : -1; // object id
        string name = id >= 0 ? ObjectName(obcd, id) : null;

        // v8 VERB: (verb u32, offset u32) entries ended by a 0 verb; offsets are into the data.
        var d = verb.DataSpan;
        var entries = new List<(int Verb, int Offset)>();
        int p = 0;
        while (p + 4 <= d.Length)
        {
            int v = (int)d.U32LE(p);
            if (v == 0 || p + 8 > d.Length) break;
            entries.Add((v, (int)d.U32LE(p + 4)));
            p += 8;
        }

        int length = d.Length;
        var starts = entries.Select(e => e.Offset).Where(o => o >= 0 && o < length).Distinct().OrderBy(o => o).ToList();
        var parts = new List<ScriptPart>();
        foreach (var (v, off) in entries)
        {
            if (off < 0 || off >= d.Length) continue;
            int next = starts.FirstOrDefault(o => o > off, length);
            parts.Add(new ScriptPart(v == -1 ? "verb default" : $"verb {v}", off, next));
        }

        string title = name ?? (id >= 0 ? $"Object {id}" : "Object");
        string verbs = entries.Count == 0 ? "no verbs" : $"verbs {string.Join(", ", entries.Select(e => e.Verb == -1 ? "default" : e.Verb.ToString()))}";
        return new ScriptEntry { Title = title, Detail = $"#{id}  {verbs}", Group = "Object", Block = verb, Parts = parts };
    }

    private static ScummDecompiler.ObjectNamer Namer(ScummBlock any) => id => ObjectName(any, id);

    /// <summary>The entry as SCUMM source.</summary>
    public static List<ScriptLine> Decompile(ScriptEntry e)
    {
        var data = e.Block.DataSpan;
        var namer = Namer(e.Block);
        if (e.Group != "Object")
        {
            var part = e.Parts.FirstOrDefault();
            return ScummDecompiler.Decompile(data, part.Start, part.End, namer);
        }

        var lines = new List<ScriptLine> { new(-1, 0, $"object {e.Title} {{") };
        foreach (var part in e.Parts)
        {
            lines.Add(new(part.Start, 1, $"{part.Label} {{"));
            foreach (var l in ScummDecompiler.Decompile(data, part.Start, part.End, namer))
                lines.Add(l with { Indent = l.Indent + 2 });
            lines.Add(new(-1, 1, "}"));
        }
        lines.Add(new(-1, 0, "}"));
        return lines;
    }

    /// <summary>The entry as an instruction listing.</summary>
    public static string Disassemble(ScriptEntry e)
    {
        var data = e.Block.DataSpan;
        var sb = new StringBuilder();
        foreach (var part in e.Parts)
        {
            if (part.Label != null) sb.Append("; ").AppendLine(part.Label);
            sb.Append(ScummScript.FormatListing(data, ScummScript.Disassemble(data, part.Start, part.End)));
            if (part.Label != null) sb.AppendLine();
        }
        return sb.ToString();
    }
}
