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
public sealed class ScriptVm
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
    }

    public ScriptStore Scripts { get; }
    public VmState State { get; }

    /// <summary>The room whose local scripts a number from <see cref="ScriptStore.FirstLocal"/> means.</summary>
    public int CurrentRoom { get; set; }

    /// <summary>Throw on an unhandled instruction or a fault instead of carrying on.</summary>
    public bool Strict { get; set; }

    public event Action<UnhandledCall> Unhandled;
    public event Action<ScriptFault> Faulted;

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
            s.Kill();
            if (State.Current == s) State.Current = null;
        }
        foreach (var n in _nest)
            if (n.Number == number && n.Kind is ScriptKind.Global or ScriptKind.Local)
            {
                n.Number = 0;
                n.Slot = null;
            }
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
                s.Kill();
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
        s.Kill();
        State.Current = null;
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
            case 0x87: Pop(); return; // debug
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
        NotHandled(s, ins);
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
}
