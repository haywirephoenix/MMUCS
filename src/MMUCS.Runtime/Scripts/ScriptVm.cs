using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using MMUCS.Core.Scumm;

namespace MMUCS.Runtime.Scripts;

/// <summary>
/// An instruction the interpreter doesn't carry out yet, with what it took from the stack (one array per
/// argument: one value, or a list's items).
/// </summary>
public sealed record UnhandledCall(string Slot, ScriptInstruction Instruction, IReadOnlyList<int[]> Args)
{
    public override string ToString()
    {
        return $"{Slot} @{Instruction.Offset}: {Instruction.Name}({string.Join(", ", Args.Select(a => a.Length == 1 ? a[0].ToString() : $"[{string.Join(", ", a)}]"))})";
    }
}

/// <summary>A script stopped on an error the engine treats as fatal; the slot was ended.</summary>
public sealed record ScriptFault(string Slot, int Offset, string Instruction, string Message)
{
    public override string ToString() => $"{Slot} @{Offset} {Instruction}: {Message}";
}

/// <summary>
/// The v8 interpreter: runs script slots in <see cref="VmState"/> the way ScummVM's script.cpp and
/// script_v6/v8.cpp do. Starting a script runs it at once, nested in the one that started it, until it
/// breaks (<c>break-here</c>, a delay, a wait) or ends; then the starter carries on. <see cref="RunAllScripts"/>
/// gives every running slot its turn for a frame, in slot order.
/// <para>
/// Instructions it doesn't carry out yet go to <see cref="Unhandled"/>: their arguments are taken off the
/// stack and 0 is pushed when they return a value, so a script carries on past them (unless
/// <see cref="Strict"/>). Errors the engine treats as fatal end the slot and go to <see cref="Faulted"/>.
/// </para>
/// </summary>
public sealed partial class ScriptVm
{
    /// <summary>ScummVM <c>kMaxScriptNesting</c> and <c>kMaxCutsceneNum</c>.</summary>
    public const int MaxNesting = 15, MaxCutscenes = 5;

    /// <summary>The most locals a started script gets from its caller's list (ScummVM <c>args[25]</c>).</summary>
    private const int MaxArgs = 25;

    private readonly Random _random;
    private readonly List<Nest> _nest = [];
    private readonly ConditionalWeakTable<ScriptCode, Dictionary<int, ScriptInstruction>> _decoded = [];
    private readonly Dictionary<string, int> _unhandledCounts = [];

    private sealed class Nest
    {
        public int Number;
        public ScriptKind Kind;
        public ScriptSlot Slot;
    }

    /// <summary>One level of the cutscene stack (ScummVM <c>cutScenePtr</c> / <c>cutSceneScript</c> / <c>cutSceneData</c>).</summary>
    public struct CutsceneLevel
    {
        /// <summary>Where <c>begin-override</c> was, to resume there when the cutscene is skipped (0: no override).</summary>
        public int OverridePc;
        public ScriptSlot OverrideSlot;
        public int Data;
    }

    public ScriptVm(ScriptStore scripts, VmState state, int seed = 0)
    {
        Scripts = scripts ?? throw new ArgumentNullException(nameof(scripts));
        State = state ?? throw new ArgumentNullException(nameof(state));
        _random = new Random(seed);
        Actors = new ActorTable(this, _random);
    }

    public ScriptStore Scripts { get; }
    public VmState State { get; }

    /// <summary>The room whose local scripts a number from <see cref="ScriptStore.FirstLocal"/> means.</summary>
    public int CurrentRoom { get; set; }

    /// <summary>Throw on an unhandled instruction or a fault instead of carrying on.</summary>
    public bool Strict { get; set; }

    public event Action<UnhandledCall> Unhandled;
    public event Action<ScriptFault> Faulted;

    /// <summary>Something the engine only warns about, such as starting code for an object that isn't here.</summary>
    public event Action<string> Warned;

    /// <summary>A script, object or room code started in a slot (it runs at once, nested at <see cref="Depth"/>).</summary>
    public event Action<ScriptSlot> ScriptStarted;

    /// <summary>
    /// A slot's code ended, with why: <c>ended</c> (it ran to its end), <c>stopped</c> (stop-script or
    /// stop-object), <c>room left</c> or <c>fault</c>. The slot still says what it ran.
    /// </summary>
    public event Action<ScriptSlot, string> ScriptEnded;

    /// <summary>How many starts are nested right now (0 between frames).</summary>
    public int Depth => _nest.Count;

    /// <summary>
    /// A room was entered (after the old room's exit code, before the new one's entry code): hosts load
    /// its pictures here. Room 0 is no room at all.
    /// </summary>
    public event Action<int> RoomEntered;

    /// <summary>Click areas the verb script is told (ScummVM's verbs.h).</summary>
    public const int VerbClickArea = 1, SceneClickArea = 2, InventoryClickArea = 3, KeyClickArea = 4;

    /// <summary>
    /// The current room's objects in their slots, with the states the hit test sees: copied from the object
    /// table once a frame, after the scripts (<see cref="UpdateObjectStates"/>), so a change shows a frame later.
    /// </summary>
    public IReadOnlyList<ObjectPlace> RoomObjects => _roomObjects;
    private IReadOnlyList<ObjectPlace> _roomObjects = [];
    private int[] _localStates = [];

    /// <summary>The cursor's level (shown above 0) and userput's (input counts above 0), as the cursor commands set them.</summary>
    public int CursorLevel { get; private set; }
    public int UserPutLevel { get; private set; }

    /// <summary>The object image the cursor shows (<c>cursor image</c>): the object, its room and which of its images.</summary>
    public (int Object, int Room, int Image) CursorImage { get; private set; }
    public (int X, int Y) CursorHotspot { get; private set; }
    public int CursorTransparent { get; private set; }

    /// <summary>
    /// Plays a video (<c>start-video</c>) to its end before the script goes on, as ScummVM's engine does: the
    /// host blocks here (<see cref="ScriptedGame"/> parks its engine thread) while it shows the video. Null
    /// leaves videos unplayed: the script goes straight on.
    /// </summary>
    public Action<string> PlayVideo { get; set; }

    /// <summary>
    /// Takes a <c>sound-kludge</c> list (Digital iMUSE's commands: music states and sequences, sound
    /// parameters); returns whether it was carried out. Others count as not carried out yet, by command.
    /// </summary>
    public Func<int[], bool> SoundKludge { get; set; }

    /// <summary>Plays start-sound's effects; without, start-sound, stop-sound and sound-running count as not carried out.</summary>
    public Sound.SoundEffects Effects { get; set; }

    /// <summary>A script started a video (its file name, e.g. <c>opening.san</c>), played or not.</summary>
    public event Action<string> VideoStarted;

    /// <summary>A script moved the mouse (<c>cursor put</c>) to a point on the screen.</summary>
    public event Action<int, int> MouseWarped;

    /// <summary>The key that runs <see cref="KeyScript"/> instead of reaching the verb script (kernel set 29; COMI: F1, 315, runs 448).</summary>
    public int KeyScriptKey { get; private set; }
    public int KeyScript { get; private set; }

    /// <summary>The slot numbers a room's exit and entry code run under (ScummVM).</summary>
    public const int ExitCodeNumber = 10001, EntryCodeNumber = 10002;

    /// <summary>The 640×480 screen, half of which is how far the camera's centre stays from a room's edges.</summary>
    private const int ScreenWidth = 640, ScreenHeight = 480;

    /// <summary>How often each unhandled instruction was met, by name.</summary>
    public IReadOnlyDictionary<string, int> UnhandledCounts => _unhandledCounts;

    /// <summary>The cutscene stack; level 0 is the base, <see cref="CutsceneDepth"/> the current level.</summary>
    public CutsceneLevel[] Cutscenes { get; } = new CutsceneLevel[MaxCutscenes];
    public int CutsceneDepth { get; private set; }

    /// <summary>The slot running the cutscene start script's caller while it runs (ScummVM <c>cutSceneScriptIndex</c>).</summary>
    private ScriptSlot _cutsceneCaller;

    // ── Starting and stopping ──

    /// <summary>Sets the engine's variables and runs the boot script, global 1, with <paramref name="bootParam"/> (ScummVM <c>runBootscript</c>).</summary>
    public void Boot(int bootParam = 0)
    {
        EngineVars.Reset(State);
        RunScript(1, args: [bootParam]);
    }

    /// <summary>
    /// Starts script <paramref name="number"/> and runs it until it breaks or ends (ScummVM <c>runScript</c>).
    /// Unless <paramref name="recursive"/>, other copies of it are stopped first.
    /// </summary>
    public void RunScript(int number, bool freezeResistant = false, bool recursive = false, IReadOnlyList<int> args = null)
    {
        if (number == 0) return;
        if (!recursive) StopScript(number);
        var code = Scripts.Find(number, CurrentRoom)
            ?? throw new ScriptException(number < ScriptStore.FirstLocal ? $"no global script {number}" : $"local script {number} is not in room {CurrentRoom}");
        var slot = State.FreeSlot();
        slot.Start(code, args, freezeResistant: freezeResistant, recursive: recursive);
        RunNested(slot);
    }

    /// <summary>Stops every copy of global or local script <paramref name="number"/> (ScummVM <c>stopScript</c>).</summary>
    public void StopScript(int number)
    {
        if (number == 0) return;
        foreach (var s in State.Slots)
        {
            if (s.Number != number || !s.IsAlive || !s.IsScript) continue;
            if (s.CutsceneOverride != 0) throw new ScriptException($"script {number} stopped with an active cutscene/override");
            End(s, "stopped");
            if (State.Current == s) State.Current = null;
        }
        foreach (var n in _nest)
            if (n.Number == number && n.Kind is ScriptKind.Global or ScriptKind.Local)
            {
                n.Number = 0;
                n.Slot = null;
            }
    }

    /// <summary>
    /// Runs <paramref name="verb"/>'s code of object <paramref name="obj"/> (ScummVM <c>runObjectScript</c>):
    /// the code of the room's copy, or of its home room's when an actor carries it. Unless
    /// <paramref name="recursive"/>, its other running code is stopped first. Nothing happens when the
    /// object isn't here or doesn't take the verb.
    /// </summary>
    public void RunObjectScript(int obj, int verb, bool freezeResistant = false, bool recursive = false, IReadOnlyList<int> args = null)
    {
        if (obj == 0) return;
        if (!recursive) StopObjectScript(obj);
        if (ObjectCodeOf(obj) is not { } code)
        {
            Warned?.Invoke($"code for object {obj} isn't in room {CurrentRoom}");
            return;
        }
        if (code.Script(verb) is not { } script) return;
        var slot = State.FreeSlot();
        slot.Start(script, args, verb: verb, freezeResistant: freezeResistant, recursive: recursive);
        slot.Carried = State.Objects.Owner(obj) != ObjectTables.RoomOwner;
        RunNested(slot);
    }

    /// <summary>
    /// Where an object's code is (ScummVM <c>whereIsObject</c>): an object in its room is found among the
    /// current room's objects; one an actor owns, only while it's in the inventory, with its home room's code.
    /// </summary>
    public ObjectCode ObjectCodeOf(int obj)
    {
        var objects = State.Objects;
        if (!objects.IsObject(obj)) return null;
        if (objects.Owner(obj) != ObjectTables.RoomOwner)
            return objects.InInventory(obj) ? Scripts.Object(objects.Room(obj), obj) : null;
        return Scripts.Object(CurrentRoom, obj);
    }

    /// <summary>Stops all running code of object <paramref name="obj"/> (ScummVM <c>stopObjectScript</c>).</summary>
    public void StopObjectScript(int obj)
    {
        if (obj == 0) return;
        foreach (var s in State.Slots)
        {
            if (s.Number != obj || !s.IsAlive || s.IsScript) continue;
            if (s.CutsceneOverride != 0) throw new ScriptException($"object {obj} stopped with an active cutscene/override");
            End(s, "stopped");
            if (State.Current == s) State.Current = null;
        }
        foreach (var n in _nest)
            if (n.Number == obj && n.Kind is not (ScriptKind.Global or ScriptKind.Local))
            {
                n.Number = 0;
                n.Slot = null;
            }
    }

    /// <summary>
    /// Gives <paramref name="obj"/> to <paramref name="owner"/> (ScummVM <c>setOwnerOf</c>). Owner 0 takes it
    /// away from everyone: its code stops and it leaves the inventory. Either way the inventory script runs.
    /// </summary>
    public void SetOwner(int obj, int owner)
    {
        var objects = State.Objects;
        if (obj <= 0) throw new ScriptException($"set-owner of object {obj}");
        if (owner == 0)
        {
            StopObjectScript(obj);
            if (objects.Owner(obj) != ObjectTables.RoomOwner) objects.RemoveFromInventory(obj);
        }
        objects.SetOwner(obj, owner);
        RunInventoryScript(obj);
    }

    /// <summary>
    /// The ego picks <paramref name="obj"/> up (ScummVM <c>o6_pickupObject</c>): into the inventory unless it's
    /// there already, owned by the ego, untouchable and in state 1. The inventory script runs.
    /// </summary>
    public void PickUp(int obj)
    {
        var objects = State.Objects;
        if (objects.InInventory(obj))
        {
            objects.SetOwner(obj, State[EngineVars.Ego]);
            RunInventoryScript(obj);
            return;
        }
        if (!objects.IsObject(obj)) throw new ScriptException($"pick-up of object {obj}");
        objects.AddToInventory(obj);
        objects.SetOwner(obj, State[EngineVars.Ego]);
        objects.SetClass(obj, ObjectTables.Untouchable, true);
        objects.SetState(obj, 1);
        RunInventoryScript(obj);
    }

    private void RunInventoryScript(int obj)
    {
        if (State[EngineVars.InventoryScript] is int script and not 0) RunScript(script, args: [obj]);
    }

    /// <summary>
    /// Leaves the current room for <paramref name="room"/> (ScummVM <c>startScene</c>): <c>new-room</c> is set,
    /// the old room's exit scripts run, then its local scripts and its objects' and own code end. The new
    /// room becomes current with its size and camera limits in the room variables, and its entry scripts run.
    /// A room or local script that asked for this stops here; a global one carries on afterwards. Every
    /// actor goes out of sight; with <paramref name="ego"/> it's put at <paramref name="atObject"/> facing away
    /// from it (<c>come-out-door</c>); then the actors in the new room come into sight before its entry code.
    /// </summary>
    public void StartScene(int room, Actor ego = null, int atObject = 0)
    {
        if (State.Current is { } caller && caller.BelongsToRoom)
        {
            if (caller.CutsceneOverride != 0) throw new ScriptException($"{caller} stopped with an active cutscene/override in exit");
            State.Current = null;
        }
        State[EngineVars.NewRoom] = room;
        RunRoomCode(EngineVars.ExitScript, Scripts.RoomExit(CurrentRoom), ExitCodeNumber, EngineVars.ExitScript2);

        foreach (var s in State.Slots)
        {
            if (!s.IsAlive || !s.BelongsToRoom) continue;
            if (s.CutsceneOverride != 0) Warned?.Invoke($"{s} stopped with an active cutscene/override in exit");
            s.CutsceneOverride = 0;
            ScriptEnded?.Invoke(s, "room left");
            s.Status = SlotStatus.Dead; // the number stays, as ScummVM leaves it
            s.Frozen = false;
            if (State.Current == s) State.Current = null;
        }

        Actors.HideAll();
        CurrentRoom = room;
        State[EngineVars.Room] = room;
        _roomObjects = Scripts.Places(room);
        _localStates = new int[_roomObjects.Count + 1];
        Actors.LoadBoxes(room);
        RoomEntered?.Invoke(room);
        if (room == 0) return;

        var (width, height) = Scripts.RoomSize(room) ?? (ScreenWidth, ScreenHeight);
        State[EngineVars.RoomWidth] = width;
        State[EngineVars.RoomHeight] = height;
        State[EngineVars.CameraMinX] = ScreenWidth / 2;
        State[EngineVars.CameraMaxX] = width - ScreenWidth / 2;
        State[EngineVars.CameraMinY] = ScreenHeight / 2;
        State[EngineVars.CameraMaxY] = height - ScreenHeight / 2;
        State[EngineVars.CameraPosX] = State[EngineVars.CameraDestX] = ScreenWidth / 2;
        State[EngineVars.CameraPosY] = State[EngineVars.CameraDestY] = ScreenHeight / 2;

        if (ego != null)
        {
            if (PlaceOf(atObject) is not { } door) throw new ScriptException($"object {atObject} is not in room {room}");
            var (x, y, dir) = door.StandAt(State.Objects.State(atObject));
            Actors.Put(ego, x, y, CurrentRoom);
            Actors.SetDirection(ego, dir + 180);
            Actors.StopMoving(ego);
        }
        Actors.ShowAll();
        Actors.EgoPositioned = false;

        RunRoomCode(EngineVars.EntryScript, Scripts.RoomEntry(room), EntryCodeNumber, EngineVars.EntryScript2);
    }

    /// <summary>The engine's exit or entry sequence: the script one variable names, the room's own code, the script the other names.</summary>
    private void RunRoomCode(int firstVar, ScriptCode roomCode, int number, int secondVar)
    {
        if (State[firstVar] is int first and not 0) RunScript(first);
        if (roomCode != null)
        {
            var slot = State.FreeSlot();
            slot.Start(roomCode);
            slot.Number = number;
            RunNested(slot);
        }
        if (State[secondVar] is int second and not 0) RunScript(second);
    }

    /// <summary>Whether a copy of global or local script <paramref name="number"/> is alive (ScummVM <c>isScriptRunning</c>).</summary>
    public bool IsScriptRunning(int number)
    {
        foreach (var s in State.Slots)
            if (s.Number == number && s.IsScript && s.IsAlive) return true;
        return false;
    }

    /// <summary>One frame's turn: each running slot runs until it breaks, in slot order (ScummVM <c>runAllScripts</c>).</summary>
    public void RunAllScripts()
    {
        foreach (var s in State.Slots) s.DidExec = false;
        State.Current = null;
        foreach (var s in State.Slots)
        {
            if (s.Cycle != 1 || !s.IsRunnable || s.DidExec) continue;
            State.Current = s;
            Execute();
        }
    }

    /// <summary>Counts paused slots' delays down; those that run out run again (ScummVM <c>decreaseScriptDelay</c>).</summary>
    public void DecreaseDelays(int jiffies)
    {
        foreach (var s in State.Slots)
        {
            if (s.Status != SlotStatus.Paused || s.Frozen) continue;
            s.Delay -= jiffies;
            if (s.Delay < 0)
            {
                s.Status = SlotStatus.Running;
                s.Delay = 0;
            }
        }
    }

    // ── Running ──

    private void RunNested(ScriptSlot slot)
    {
        if (_nest.Count >= MaxNesting) throw new ScriptException("too many nested scripts");
        var caller = State.Current;
        var nest = caller == null ? new Nest() : new Nest { Number = caller.Number, Kind = caller.Kind, Slot = caller };
        _nest.Add(nest);

        State.Current = slot;
        ScriptStarted?.Invoke(slot);
        try
        {
            Execute();
        }
        finally
        {
            _nest.RemoveAt(_nest.Count - 1);
        }
        // Resume the caller if it hasn't been stopped, frozen or moved on in the meantime.
        if (nest.Number != 0 && nest.Slot is { } back
            && back.Number == nest.Number && back.Kind == nest.Kind && back.IsAlive && back.FreezeCount == 0)
        {
            State.Current = back;
            return;
        }
        State.Current = null;
    }

    private void Execute()
    {
        while (State.Current is { } s)
        {
            var ins = Decode(s);
            s.DidExec = true;
            s.Pc = ins.Offset + ins.Length;
            try
            {
                if (ins.Unknown) throw new ScriptException($"can't decode {ins.Name}");
                Exec(s, ins);
            }
            catch (ScriptException e) when (!Strict)
            {
                Faulted?.Invoke(new ScriptFault(s.ToString(), ins.Offset, ins.Name, e.Message));
                End(s, "fault");
                if (State.Current == s) State.Current = null;
            }
        }
    }

    private ScriptInstruction Decode(ScriptSlot s)
    {
        var cache = _decoded.GetValue(s.Code, _ => []);
        if (!cache.TryGetValue(s.Pc, out var ins))
            cache[s.Pc] = ins = ScummScript.DecodeAt(s.Code.Code.Span, s.Pc);
        return ins;
    }

    private void BreakHere() => State.Current = null;

    private void StopObjectCode(ScriptSlot s)
    {
        if (s.CutsceneOverride != 0)
            throw new ScriptException($"{(s.IsScript ? "script" : "object")} {s.Number} ending with an active cutscene/override ({s.CutsceneOverride})");
        End(s, "ended");
        State.Current = null;
    }

    private void End(ScriptSlot s, string why)
    {
        ScriptEnded?.Invoke(s, why);
        s.Kill();
    }

    private int Pop() => State.Pop();

    private void Push(int v) => State.Push(v);

    private void Push(bool b) => State.Push(b ? 1 : 0);

    private int[] Args() => State.PopList(MaxArgs);

    private void Exec(ScriptSlot s, ScriptInstruction ins)
    {
        int a, b;
        switch (ins.Opcode)
        {
            // ── Stack and expressions ──
            case 0x01: Push(ins.Operand); return;
            case 0x02: Push(State.Read(ins.Operand)); return;
            case 0x03: b = Pop(); Push(ReadArray(ins.Operand, 0, b)); return;
            case 0x04: b = Pop(); a = Pop(); Push(ReadArray(ins.Operand, a, b)); return;
            case 0x05: a = Pop(); Push(a); Push(a); return;
            case 0x06: Pop(); return;
            case 0x07: Push(Pop() == 0); return;
            case 0x08: Push(Pop() == Pop()); return;
            case 0x09: Push(Pop() != Pop()); return;
            case 0x0a: a = Pop(); Push(Pop() > a); return;
            case 0x0b: a = Pop(); Push(Pop() < a); return;
            case 0x0c: a = Pop(); Push(Pop() <= a); return;
            case 0x0d: a = Pop(); Push(Pop() >= a); return;
            case 0x0e: a = Pop(); Push(unchecked(Pop() + a)); return;
            case 0x0f: a = Pop(); Push(unchecked(Pop() - a)); return;
            case 0x10: a = Pop(); Push(unchecked(Pop() * a)); return;
            case 0x11: a = Pop(); if (a == 0) throw new ScriptException("division by zero"); Push(Pop() / a); return;
            case 0x12: a = Pop(); Push(Pop() != 0 && a != 0); return;
            case 0x13: a = Pop(); Push(Pop() != 0 || a != 0); return;
            case 0x14: a = Pop(); Push(Pop() & a); return;
            case 0x15: a = Pop(); Push(Pop() | a); return;
            case 0x16: a = Pop(); if (a == 0) throw new ScriptException("modulo by zero"); Push(Pop() % a); return;

            // ── Flow ──
            case 0x64: if (Pop() != 0) s.Pc = ins.Target; return;
            case 0x65: if (Pop() == 0) s.Pc = ins.Target; return;
            case 0x66: s.Pc = ins.Target; return;
            case 0x67: BreakHere(); return;
            case 0x68: // delay-frames: counts down on each run of the same instruction
                if (s.DelayFrames == 0) s.DelayFrames = Pop();
                else s.DelayFrames--;
                if (s.DelayFrames != 0)
                {
                    s.Pc = ins.Offset;
                    BreakHere();
                }
                return;
            case 0x6a: Sleep(s, (ushort)Pop()); return;
            case 0x6b: Sleep(s, unchecked((int)((uint)Pop() * 60))); return;
            case 0x6c: Sleep(s, (ushort)Pop() * 3600); return;

            // ── Variables and arrays ──
            case 0x6d: State.Write(ins.Operand, Pop()); return;
            case 0x6e: State.Write(ins.Operand, unchecked(State.Read(ins.Operand) + 1)); return;
            case 0x6f: State.Write(ins.Operand, unchecked(State.Read(ins.Operand) - 1)); return;
            case 0x70: // dim
            case 0x74: // dim 2d
                if (ins.SubOp == 0x0C) { State.NukeArray(ins.Operand); return; }
                var type = ins.SubOp == 0x0B ? ArrayType.String : ArrayType.Int;
                if (ins.Opcode == 0x70) State.DefineArray(ins.Operand, type, 0, Pop());
                else { b = Pop(); a = Pop(); State.DefineArray(ins.Operand, type, a, b); }
                return;
            case 0x71: a = Pop(); b = Pop(); WriteArray(ins.Operand, 0, b, a); return;
            case 0x72: b = Pop(); WriteArray(ins.Operand, 0, b, unchecked(ReadArray(ins.Operand, 0, b) + 1)); return;
            case 0x73: b = Pop(); WriteArray(ins.Operand, 0, b, unchecked(ReadArray(ins.Operand, 0, b) - 1)); return;
            case 0x75: { int v = Pop(); b = Pop(); a = Pop(); WriteArray(ins.Operand, a, b, v); return; }
            case 0x76: ArrayOps(s, ins); return;

            // ── Scripts ──
            case 0x79: { var args = Args(); int script = Pop(), flags = Pop(); RunScript(script, (flags & 1) != 0, (flags & 2) != 0, args); return; }
            case 0x7a: { var args = Args(); RunScript(Pop(), args: args); return; }
            case 0x7b: StopObjectCode(s); return;
            case 0x7c: a = Pop(); if (a == 0) StopObjectCode(s); else StopScript(a); return;
            case 0x7d: { var args = Args(); int script = Pop(), flags = Pop(); StopObjectCode(s); RunScript(script, (flags & 1) != 0, (flags & 2) != 0, args); return; }
            case 0x7e: return; // return: a no-op; a called script's result is already on the stack
            case 0x81: BeginCutscene(s, Args()); return;
            case 0x82: EndCutscene(s); return;
            case 0x83: a = Pop(); if (a != 0) Freeze(a); else Unfreeze(); return;
            case 0x84: BeginOverride(s); return;
            case 0x85: EndOverride(); return;
            case 0x7f: { var args = Args(); int verb = Pop(), obj = Pop(), flags = Pop(); RunObjectScript(obj, verb, (flags & 1) != 0, (flags & 2) != 0, args); return; }
            case 0x80: StopObjectScript(Pop()); return;
            case 0x87: Pop(); return; // debug
            case 0xb2: SoundKludgeOp(s, ins); return;
            case 0xaf when Effects != null: Effects.Start(Pop()); return;
            case 0xb1 when Effects != null: Effects.Stop(Pop()); return;
            case 0xd5 when Effects != null: a = Pop(); Push(a != 0 && Effects.IsRunning(a)); return;
            // resourceRoutines: loading, locking and freeing resources mean nothing here, as everything is in
            // memory; loading an object (0x3E) brings in a floating object, which isn't here yet.
            case 0xaa when ins.SubOp != 0x3E: Pop(); return;

            // ── Rooms ──
            case 0x9d: StartScene(Pop()); return;
            case 0x9e: ComeOutDoor(ins); return;
            case 0x9c: if (CursorCommand(ins)) return; break;
            case 0xba: KernelSet(s, ins); return;
            case 0xdc: { int y = Pop(), x = Pop(); Push(FindObject(x, y)); return; }
            case 0xb9: StartVideo(s, ins); return;

            // ── Objects ──
            case 0x89: SetClasses(); return;
            case 0x8a: { int state = Pop(), obj = Pop(); State.Objects.SetState(obj, state); return; }
            case 0x8b: { int owner = Pop(), obj = Pop(); SetOwner(obj, owner); return; }
            case 0xa6: PickUp(Pop()); return;
            case 0xc9: { var args = Args(); int verb = Pop(), obj = Pop(); RunObjectScript(obj, verb, recursive: true, args: args); return; }
            case 0xd0: { var list = State.PopList(16); Push(ClassesMatch(Pop(), list)); return; }
            case 0xd1: Push(State.Objects.State(Pop())); return;
            case 0xd2: Push(State.Objects.Owner(Pop())); return;
            case 0xda: { int verb = Pop(), obj = Pop(); Push(ObjectCodeOf(obj) is { } code && code.EntryFor(verb) is int at ? code.CodeStart + at : 0); return; }
            case 0xdf: { int index = Pop(), owner = Pop(); Push(State.Objects.FindInventory(owner, index)); return; }
            case 0xe0: Push(State.Objects.InventoryCount(Pop())); return;
            case 0xc8: { var args = Args(); RunScript(Pop(), recursive: true, args: args); return; }

            // ── Functions ──
            case 0xca: { var list = State.PopList(100); int i = Pop(); if (i < 0 || i > list.Length) throw new ScriptException($"pick-one-of {i} out of range 0..{list.Length - 1}"); Push(i < list.Length ? list[i] : 0); return; }
            case 0xcb: { int def = Pop(); var list = State.PopList(100); int i = Pop(); Push(i < 0 || i >= list.Length ? def : list[i]); return; }
            case 0xcd: { var list = State.PopList(100); int v = Pop(); Push(Array.IndexOf(list, v) >= 0); return; }
            case 0xce: a = Pop(); if (a + 1 == 0) throw new ScriptException("random(-1)"); Push(_random.Next(0x8000) % (a + 1)); return;
            case 0xcf: { int max = Pop(), min = Pop(); int span = max - min + 1; if (span == 0) throw new ScriptException($"random-between({min}, {max})"); Push(min + _random.Next(0x8000) % span); return; }
            case 0xd3: Push(IsScriptRunning(Pop())); return;
            case 0xd6: Push(Math.Abs(Pop())); return;
        }
        if (ExecActor(s, ins) || ExecText(s, ins)) return;
        NotHandled(s, ins);
    }

    /// <summary>
    /// <c>come-out-door obj at x,y</c> (ScummVM <c>o6_loadRoomWithEgo</c>): the ego goes to the object's home
    /// room, where the room's entry puts it at the object facing away from it, with <c>walk-to-object</c> set
    /// meanwhile; then it walks to x,y unless that's −1. Walking isn't here yet: that part is reported
    /// through <see cref="Unhandled"/>.
    /// </summary>
    private void ComeOutDoor(ScriptInstruction ins)
    {
        int y = Pop(), x = Pop(), obj = Pop();
        int room = State.Objects.IsObject(obj) ? State.Objects.Room(obj) : 0;
        var ego = Actors[State[EngineVars.Ego]];
        Actors.Put(ego, 0, 0, room);
        Actors.EgoPositioned = false;
        State[EngineVars.WalkToObject] = obj;
        StartScene(ego.Room, ego, obj);
        State[EngineVars.WalkToObject] = 0;
        if (x is not (-1 or 0x7FFFFFFF))
        {
            _unhandledCounts["walk"] = _unhandledCounts.GetValueOrDefault("walk") + 1;
            Unhandled?.Invoke(new UnhandledCall("ego", ins, [[obj], [x], [y]]));
        }
    }

    /// <summary>
    /// <c>class-of obj is [...]</c>: a class with bit 7 is set, one without is cleared (ScummVM <c>o6_setClass</c>).
    /// Class 0 clears all of the object's classes; ScummVM clears entry <i>n</i> of the table there (the
    /// list position), which looks like a slip for the object.
    /// </summary>
    private void SetClasses()
    {
        var list = State.PopList(16);
        int obj = Pop();
        for (int i = list.Length - 1; i >= 0; i--)
        {
            int cls = list[i];
            if (cls == 0) State.Objects.SetClasses(obj, 0);
            else State.Objects.SetClass(obj, cls, (cls & 0x80) != 0);
        }
    }

    /// <summary>Whether every class in the list matches: with bit 7 it must be set, without it clear (ScummVM <c>o6_ifClassOfIs</c>).</summary>
    private bool ClassesMatch(int obj, int[] list)
    {
        foreach (int cls in list)
            if (State.Objects.HasClass(obj, cls) != ((cls & 0x80) != 0)) return false;
        return true;
    }

    private void Sleep(ScriptSlot s, int jiffies)
    {
        s.Delay = jiffies;
        s.Status = SlotStatus.Paused;
        BreakHere();
    }

    private void ArrayOps(ScriptSlot s, ScriptInstruction ins)
    {
        int b;
        switch (ins.SubOp)
        {
            case 0x14: // the inline string, its 0 and one more 0, from element b
            {
                b = Pop();
                int len = ins.TextLength;
                var array = State.DefineArray(ins.Operand, ArrayType.String, 0, len + 1);
                var bytes = array.Bytes;
                var text = s.Code.Code.Span.Slice(ins.TextStart, len + 1);
                for (int i = 0; i < text.Length + 1 && b + i < bytes.Length; i++)
                    bytes[b + i] = i < text.Length ? text[i] : (byte)0;
                return;
            }
            case 0x15: // a list into a 1-d array from element b, dimensioned first if need be
            {
                b = Pop();
                var list = State.PopList();
                if (State.Read(ins.Operand) == 0) State.DefineArray(ins.Operand, ArrayType.Int, 0, b + list.Length);
                for (int i = list.Length - 1; i >= 0; i--) WriteArray(ins.Operand, 0, b + i, list[i]);
                return;
            }
            case 0x16: // a list into row c of a 2-d array, from element b
            {
                b = Pop();
                var list = State.PopList();
                if (State.Read(ins.Operand) == 0) throw new ScriptException("must dim a two dimensional array before assigning");
                int c = Pop();
                for (int i = list.Length - 1; i >= 0; i--) WriteArray(ins.Operand, c, b + i, list[i]);
                return;
            }
        }
        NotHandled(s, ins);
    }

    private int ReadArray(int variable, int index, int @base)
    {
        var array = State.Array(variable) ?? throw new ScriptException($"no array in {ScummScript.VarName(variable)} ({State.Read(variable)})");
        return array[index, @base];
    }

    /// <summary>Writing to a variable with no array does nothing (ScummVM <c>writeArray</c>).</summary>
    private void WriteArray(int variable, int index, int @base, int value)
    {
        if (State.Array(variable) is { } array) array[index, @base] = value;
    }

    // ── Cutscenes, overrides, freezing (ScummVM script.cpp) ──

    private void BeginCutscene(ScriptSlot s, int[] args)
    {
        s.CutsceneOverride++;
        if (++CutsceneDepth >= MaxCutscenes) throw new ScriptException("cutscene stack overflow");
        Cutscenes[CutsceneDepth] = new CutsceneLevel { Data = args.Length > 0 ? args[0] : 0 };
        _cutsceneCaller = s;
        try
        {
            if (State[EngineVars.CutsceneStartScript] is int start and not 0) RunScript(start, args: args);
        }
        finally
        {
            _cutsceneCaller = null;
        }
    }

    private void EndCutscene(ScriptSlot s)
    {
        if (s.CutsceneOverride > 0) s.CutsceneOverride--;
        int[] args = [Cutscenes[CutsceneDepth].Data];
        State[EngineVars.Override] = 0;
        if (Cutscenes[CutsceneDepth].OverridePc != 0 && s.CutsceneOverride > 0) s.CutsceneOverride--;
        Cutscenes[CutsceneDepth] = default;
        if (CutsceneDepth == 0) throw new ScriptException("cutscene stack underflow");
        CutsceneDepth--;
        if (State[EngineVars.CutsceneEndScript] is int end and not 0) RunScript(end, args: args);
    }

    /// <summary>
    /// Skips the cutscene: the script that set the override jumps back to it with the override variable
    /// set, so it takes the jump that follows (ScummVM <c>abortCutscene</c>). False when there's no override.
    /// </summary>
    public bool AbortCutscene()
    {
        ref var level = ref Cutscenes[CutsceneDepth];
        if (level.OverridePc == 0 || level.OverrideSlot is not { } s) return false;
        s.Pc = level.OverridePc;
        s.Status = SlotStatus.Running;
        s.Frozen = false;
        s.FreezeCount = 0;
        if (s.CutsceneOverride > 0) s.CutsceneOverride--;
        State[EngineVars.Override] = 1;
        level.OverridePc = 0;
        return true;
    }

    /// <summary>Records where to resume when the cutscene is skipped, and steps over the jump that follows.</summary>
    private void BeginOverride(ScriptSlot s)
    {
        ref var level = ref Cutscenes[CutsceneDepth];
        level.OverridePc = s.Pc;
        level.OverrideSlot = s;
        s.Pc += 5; // the jump's opcode and its 32-bit offset
        State[EngineVars.Override] = 0;
    }

    private void EndOverride()
    {
        ref var level = ref Cutscenes[CutsceneDepth];
        level.OverridePc = 0;
        level.OverrideSlot = null;
        State[EngineVars.Override] = 0;
    }

    /// <summary>Freezes every other live slot; freeze-resistant ones only when <paramref name="flag"/> is 2 (v7+).</summary>
    private void Freeze(int flag)
    {
        foreach (var s in State.Slots)
        {
            if (s == State.Current || !s.IsAlive || (s.FreezeResistant && flag != 2)) continue;
            s.Frozen = true;
            s.FreezeCount++;
        }
        if (_cutsceneCaller is { } caller)
        {
            caller.Frozen = false;
            caller.FreezeCount = 0;
        }
    }

    private void Unfreeze()
    {
        foreach (var s in State.Slots)
            if (s.Frozen && --s.FreezeCount == 0) s.Frozen = false;
    }

    // ── Not carried out yet ──

    /// <summary>
    /// The object at a room point (ScummVM <c>findObject</c>): the first slot, not untouchable, whose parents
    /// are each in the state it needs, whose place holds the point; 0 for none.
    /// </summary>
    public int FindObject(int x, int y)
    {
        var objects = State.Objects;
        foreach (var o in _roomObjects)
        {
            if (o.Id < 1 || !objects.IsObject(o.Id) || objects.HasClass(o.Id, ObjectTables.Untouchable)) continue;
            var at = o;
            while (true)
            {
                int needed = at.ParentState;
                if (at.Parent == 0)
                {
                    if (o.Contains(x, y)) return o.Id;
                    break;
                }
                if (at.Parent > _roomObjects.Count) break;
                at = _roomObjects[at.Parent - 1];
                if ((_localStates[at.Slot] & 0xF) != needed) break;
            }
        }
        return 0;
    }

    /// <summary>The state the room's slot for object <paramref name="obj"/> has (what drawing and hit tests see), or 0 when it isn't here.</summary>
    public int LocalState(int obj)
    {
        foreach (var o in _roomObjects)
            if (o.Id == obj) return _localStates[o.Slot];
        return 0;
    }

    /// <summary>
    /// Whether the engine draws object <paramref name="obj"/> of the current room (ScummVM <c>drawRoomObject</c>):
    /// its state isn't 0 and each object it belongs to is in the state it needs.
    /// </summary>
    public bool IsDrawn(int obj)
    {
        var place = _roomObjects.FirstOrDefault(o => o.Id == obj);
        if (place == null || _localStates[place.Slot] == 0) return false;
        for (var at = place; at.Parent != 0;)
        {
            int needed = at.ParentState;
            if (at.Parent > _roomObjects.Count) return false;
            at = _roomObjects[at.Parent - 1];
            if ((_localStates[at.Slot] & 0xF) != needed) return false;
        }
        return true;
    }

    /// <summary>Copies the object table's states into the room's slots (ScummVM <c>updateObjectStates</c>, once a frame after the scripts).</summary>
    public void UpdateObjectStates()
    {
        foreach (var o in _roomObjects)
            if (o.Id > 0 && State.Objects.IsObject(o.Id)) _localStates[o.Slot] = State.Objects.State(o.Id);
    }

    /// <summary>
    /// Runs the verb script with what was clicked or pressed (ScummVM <c>runInputScript</c>): locals 0–2
    /// are the click area, the key or verb (0 for a scene click), and the button (1 left, 2 right; 1 for a key).
    /// </summary>
    public void RunInputScript(int area, int value, int mode)
    {
        if (State[EngineVars.VerbScript] is int script and not 0) RunScript(script, args: [area, value, mode]);
    }

    /// <summary>
    /// <c>start-video NAME</c> (ScummVM <c>o8_startVideo</c>): the whole video plays inside the instruction,
    /// every script waiting, then this one goes on.
    /// </summary>
    private void StartVideo(ScriptSlot s, ScriptInstruction ins)
    {
        string name = ins.TextStart >= 0 ? System.Text.Encoding.Latin1.GetString(s.Code.Code.Span.Slice(ins.TextStart, ins.TextLength)) : "";
        VideoStarted?.Invoke(name);
        PlayVideo?.Invoke(name);
    }

    /// <summary>The cursor and userput commands (ScummVM <c>o8_cursorCommand</c>); false for one not carried out.</summary>
    private bool CursorCommand(ScriptInstruction ins)
    {
        switch (ins.SubOp)
        {
            case 0xDC: CursorLevel = 1; break;
            case 0xDD: CursorLevel = 0; break;
            case 0xDE: CursorLevel++; break;
            case 0xDF: CursorLevel--; break;
            case 0xE0: UserPutLevel = 1; break;
            case 0xE1: UserPutLevel = 0; break;
            case 0xE2: UserPutLevel++; break;
            case 0xE3: UserPutLevel--; break;
            case 0xE4: { int image = Pop(), obj = Pop(); CursorImage = (obj, State.Objects.IsObject(obj) ? State.Objects.Room(obj) : 0, image); break; }
            case 0xE5: { int y = Pop(), x = Pop(); CursorHotspot = (x, y); break; }
            case 0xE6: CursorTransparent = Pop(); break;
            case 0xE9: { int y = Pop(), x = Pop(); MouseWarped?.Invoke(x, y); break; }
            default: return false;
        }
        State[EngineVars.CursorState] = CursorLevel;
        State[EngineVars.UserPut] = UserPutLevel;
        return true;
    }

    /// <summary>Kernel calls (ScummVM <c>o8_kernelSetFunctions</c>): 29 sets the key script, 30 ends every other script; the rest are reported.</summary>
    private void KernelSet(ScriptSlot s, ScriptInstruction ins)
    {
        var args = State.PopList(30);
        switch (args.Length > 0 ? args[0] : 0)
        {
            case 29 when args.Length >= 3:
                (KeyScriptKey, KeyScript) = (args[1], args[2]);
                return;
            case 30:
                foreach (var other in State.Slots)
                    if (other != s && other.IsAlive)
                    {
                        other.CutsceneOverride = 0;
                        ScriptEnded?.Invoke(other, "stopped");
                        other.Status = SlotStatus.Dead;
                        other.Frozen = false;
                    }
                return;
        }
        Report(s, ins, [args]);
    }

    /// <summary>
    /// <c>sound-kludge [...]</c> (ScummVM <c>o6_soundKludge</c>, to iMUSE's parseScriptCmds): [-1] does nothing
    /// in v7+; the rest go to <see cref="SoundKludge"/>.
    /// </summary>
    private void SoundKludgeOp(ScriptSlot s, ScriptInstruction ins)
    {
        var list = State.PopList(16);
        if (list is [-1] || SoundKludge?.Invoke(list) == true) return;
        string name = $"{ins.Name}.{(list.Length > 0 ? list[0] : 0)}";
        _unhandledCounts[name] = _unhandledCounts.GetValueOrDefault(name) + 1;
        if (Strict) throw new ScriptException($"{name} isn't carried out yet");
        Unhandled?.Invoke(new UnhandledCall(s.ToString(), ins, [list]));
    }

    private void NotHandled(ScriptSlot s, ScriptInstruction ins)
    {
        _unhandledCounts[ins.Name] = _unhandledCounts.GetValueOrDefault(ins.Name) + 1;
        if (Strict) throw new ScriptException($"{ins.Name} isn't carried out yet");

        // Take the arguments off the stack, last pushed first, and hand them over in push order.
        var args = new int[ins.Args.Length][];
        for (int i = ins.Args.Length - 1; i >= 0; i--)
            args[i] = ins.Args[i] == 'L' ? State.PopList() : [Pop()];
        if (ins.Pushes) Push(0);
        Unhandled?.Invoke(new UnhandledCall(s.ToString(), ins, args));
    }

    /// <summary>Reports an instruction whose arguments were already taken off the stack.</summary>
    private void Report(ScriptSlot s, ScriptInstruction ins, int[][] args)
    {
        _unhandledCounts[ins.Name] = _unhandledCounts.GetValueOrDefault(ins.Name) + 1;
        if (Strict) throw new ScriptException($"{ins.Name}({string.Join(", ", args.Select(a => string.Join(" ", a)))}) isn't carried out yet");
        Unhandled?.Invoke(new UnhandledCall(s.ToString(), ins, args));
    }
}
