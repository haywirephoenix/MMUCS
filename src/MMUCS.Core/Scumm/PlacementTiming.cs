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

    public PlacementTiming(IEnumerable<ScummBlock> scripts)
    {
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
