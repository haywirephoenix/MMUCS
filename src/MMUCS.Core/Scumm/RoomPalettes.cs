using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace MMUCS.Core.Scumm;

/// <summary>
/// The palette each costume and object image is drawn in. A COMI room has several palettes (PALS);
/// it opens in palette 0 and a script switches with "room palette N" before it shows what belongs to
/// that palette, then switches back: skulisle's script 406 sets palette 1, dresses actor 29 in costume
/// 381, and sets palette 0 once it's gone; stancrpt shows stans-office in palette 1; the credits show
/// page k in palette k − 1. So a costume a script dresses an actor in, or an object it turns on, after
/// "room palette N" (N &gt; 0) is drawn in palette N in that room, and so is what the scripts it starts
/// then do (the banjo duel's close-ups, started by script 216 in palette 1); so is an object turned on
/// just before the switch (smglcave's cave-dark-anim-object). Loading a room puts its palette 0 back.
/// Everything else is in palette 0.
/// </summary>
public sealed class RoomPalettes
{
    private static readonly Regex NewPalette = new(@"^room palette (\d+)$", RegexOptions.Compiled);
    private static readonly Regex SetState = new(@"^state-of ([\w-]+) is (\d+)$", RegexOptions.Compiled);
    private static readonly Regex SetCostume = new(@"^actor .+ costume (\d+)$", RegexOptions.Compiled);
    private static readonly Regex CurrentRoom = new(@"^current-room (\d+)$", RegexOptions.Compiled);
    private static readonly Regex Wait = new(@"^(?:break-here|wait-for)", RegexOptions.Compiled);
    private static readonly Regex StartScript = new(@"^(?:start-script|start-script-quick|chain-script)\b(?: [a-z]+)* (\d+)\b", RegexOptions.Compiled);

    private static readonly ConditionalWeakTable<ScummBlock, RoomPalettes> Cache = new();

    private readonly Dictionary<(int Room, int Costume), int> _costumes = new();
    private readonly Dictionary<(int Room, string Name), int> _objects = new();

    /// <summary>The palette costume <paramref name="costume"/> is drawn in in room <paramref name="room"/> (0 unless a
    /// script shows it in another).</summary>
    public int Costume(int room, int? costume) => costume is int c && _costumes.TryGetValue((room, c), out int p) ? p : 0;

    /// <summary>The palette an object image of room <paramref name="room"/> is drawn in.</summary>
    public int Object(int room, string name) => name != null && _objects.TryGetValue((room, name), out int p) ? p : 0;

    /// <summary>The palette <paramref name="obj"/> of <paramref name="room"/> is drawn in.</summary>
    public int Object(RoomModel room, RoomObject obj) => room == null || obj == null ? 0 : Object(room.RoomNo, obj.Name);

    /// <summary>The palettes of the game <paramref name="any"/> belongs to (read once per game).</summary>
    public static RoomPalettes For(ScummBlock any)
    {
        if (any == null) return new RoomPalettes();
        var root = any;
        while (root.Parent != null) root = root.Parent;
        lock (Cache)
        {
            if (Cache.TryGetValue(root, out var cached)) return cached;
            var result = new RoomPalettes();
            try { result.Read(root); }
            catch { /* what was read so far stands */ }
            Cache.AddOrUpdate(root, result);
            return result;
        }
    }

    private sealed record Script(ScriptEntry Entry, int Room);

    private void Read(ScummBlock root)
    {
        var globals = new Dictionary<int, Script>();
        var locals = new Dictionary<(int Room, int No), Script>();
        var withSwitch = new List<Script>();
        foreach (var lflf in root.FindChildrenRecursive(ScummTag.LFLF))
        {
            int room = RoomScripts.RoomNo(lflf) ?? -1;
            foreach (var e in RoomScripts.List(lflf))
            {
                var s = new Script(e, room);
                if (e.Group == "Global" && e.Title.StartsWith("Global script ") && int.TryParse(e.Title[14..], out int g)) globals.TryAdd(g, s);
                else if (e.Group == "Local" && int.TryParse(e.Title[13..], out int l)) locals.TryAdd((room, l), s);
                if (SwitchesPalette(e.Block.DataSpan)) withSwitch.Add(s);
            }
        }

        var done = new HashSet<(ScriptEntry, int, int)>();
        // A global script runs in the room the game is in; a local one in its own.
        void Run(Script s, int palette, int room)
        {
            if (s == null || !done.Add((s.Entry, palette, room))) return;
            List<ScriptLine> lines;
            try { lines = RoomScripts.Decompile(s.Entry); }
            catch { return; }
            // Objects turned on since the last wait, in the same block.
            var turnedOn = new List<string>();
            int turnedOnIndent = -1;
            // A switch inside a block holds to its end ("if (bit77) { room palette 1 … }" in stancrpt): the
            // palette to go back to, by the indent the block's lines have.
            var scopes = new Stack<(int Indent, int Restore)>();
            foreach (var line in lines)
            {
                string t = line.Text.Trim();
                while (scopes.Count > 0 && line.Indent < scopes.Peek().Indent) palette = scopes.Pop().Restore;
                if (line.Indent != turnedOnIndent) turnedOn.Clear();
                if (NewPalette.Match(t) is { Success: true } np)
                {
                    scopes.Push((line.Indent, palette));
                    palette = int.Parse(np.Groups[1].Value);
                    if (palette > 0) foreach (var name in turnedOn) _objects.TryAdd((room, name), palette);
                    turnedOn.Clear();
                }
                else if (CurrentRoom.Match(t) is { Success: true } cr)
                {
                    room = int.Parse(cr.Groups[1].Value);
                    palette = 0;
                    turnedOn.Clear();
                }
                else if (Wait.IsMatch(t)) turnedOn.Clear();
                else if (SetState.Match(t) is { Success: true } ss && ss.Groups[2].Value != "0")
                {
                    if (palette > 0) _objects.TryAdd((room, ss.Groups[1].Value), palette);
                    else
                    {
                        turnedOn.Add(ss.Groups[1].Value);
                        turnedOnIndent = line.Indent;
                    }
                }
                else if (palette <= 0) continue;
                else if (SetCostume.Match(t) is { Success: true } sc) _costumes.TryAdd((room, int.Parse(sc.Groups[1].Value)), palette);
                else if (StartScript.Match(t) is { Success: true } st && int.TryParse(st.Groups[1].Value, out int no))
                {
                    if (no >= 2000) Run(locals.GetValueOrDefault((room, no)), palette, room);
                    else if (globals.GetValueOrDefault(no) is { } g) Run(g, palette, room);
                }
            }
        }
        foreach (var s in withSwitch) Run(s, 0, s.Room);
    }

    /// <summary>Whether the code holds a roomOps newPalette (0xAB 0x5C): only those scripts are decompiled.</summary>
    private static bool SwitchesPalette(ReadOnlySpan<byte> code)
    {
        for (int i = 0; i + 1 < code.Length; i++)
            if (code[i] == 0xAB && code[i + 1] == 0x5C) return true;
        return false;
    }
}
