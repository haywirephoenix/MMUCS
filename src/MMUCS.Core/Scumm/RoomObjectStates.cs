using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MMUCS.Core.Scumm;

/// <summary>
/// The state each object image is in when the player enters a room. SCUMM draws an object's image
/// only while its state is nonzero, and state N shows the object's image N. An object starts in the
/// state the game's object table (DOBJ) gives it; the room's entry script and the local scripts it
/// starts then set some with "state-of OBJ is N". Those set unconditionally count, and those inside
/// conditions known to hold on a fresh game (bit variables 0, the objects' starting classes: pistols'
/// "if (class-of(lid) is [32]) … else state 1"), so this is the room as it looks before anything the
/// player has done changes it.
/// </summary>
public static class RoomObjectStates
{
    private static readonly Regex SetState = new(@"^state-of ([\w-]+) is (-?\d+)$", RegexOptions.Compiled);
    private static readonly Regex StartLocal = new(@"^(?:start-script|start-script-quick|chain-script)\b(?: [a-z]+)* (\d+)\b", RegexOptions.Compiled);

    /// <summary>State on entry for each of the room's object images (by <see cref="RoomObject"/>).</summary>
    public static Dictionary<RoomObject, int> Initial(RoomModel room)
    {
        var result = new Dictionary<RoomObject, int>();
        if (room?.Lflf == null) return result;
        var root = room.Lflf;
        while (root.Parent != null) root = root.Parent;

        // Start states by name (object images only know their name) and by id.
        var byName = new Dictionary<string, int>(StringComparer.Ordinal);
        var byId = new Dictionary<int, string>();
        foreach (var o in RoomDataExport.ReadObjectTable(root))
        {
            byName.TryAdd(o.Name, o.State);
            byId.TryAdd(o.Id, o.Name);
        }

        var fresh = FreshGame.For(root);
        var scripts = RoomScripts.List(room.Lflf);
        var locals = new Dictionary<int, ScriptEntry>();
        foreach (var e in scripts.Where(e => e.Group == "Local" && e.Title.StartsWith("Local script ")))
            if (int.TryParse(e.Title[13..], out int no)) locals.TryAdd(no, e);
        var entry = scripts.FirstOrDefault(e => e.Group == "Room" && e.Title == "Room entry");

        var visited = new HashSet<ScriptEntry>();
        void Run(ScriptEntry e)
        {
            if (e == null || !visited.Add(e)) return;
            List<ScriptLine> lines;
            try { lines = RoomScripts.Decompile(e); }
            catch { return; } // a script that doesn't decompile sets nothing we can read
            var part = e.Parts.FirstOrDefault();
            var branches = PlacementTiming.Branches(e.Block.DataSpan, part.Start, part.End, room.RoomNo, fresh);
            foreach (var line in lines.Where(l => l.Indent == 0 || (l.Offset >= 0 && PlacementTiming.Surely(branches, l.Offset))))
            {
                string t = line.Text.Trim();
                if (SetState.Match(t) is { Success: true } m)
                {
                    string name = m.Groups[1].Value;
                    if (int.TryParse(name, out int id) && byId.TryGetValue(id, out var n)) name = n;
                    byName[name] = int.Parse(m.Groups[2].Value);
                }
                // Local scripts the entry starts run as the room opens: follow them, in order.
                else if (StartLocal.Match(t) is { Success: true } s && int.TryParse(s.Groups[1].Value, out int no)
                         && no >= 2000 && locals.TryGetValue(no, out var local))
                    Run(local);
            }
        }
        Run(entry);

        foreach (var obj in room.Objects)
            result[obj] = obj.Name != null && byName.TryGetValue(obj.Name, out int state) ? state : 1;
        return result;
    }

    /// <summary>The image frame a state shows (state N is the object's image N), or 0 when it shows none.</summary>
    public static int FrameFor(int state, int frames) => state <= 0 ? 0 : Math.Clamp(state - 1, 0, Math.Max(0, frames - 1));
}
