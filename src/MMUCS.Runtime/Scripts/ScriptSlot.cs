using System.Collections.Generic;

namespace MMUCS.Runtime.Scripts;

/// <summary>A slot's state (ScummVM <c>ssDead</c>, <c>ssPaused</c>, <c>ssRunning</c>).</summary>
public enum SlotStatus
{
    Dead = 0,
    Paused = 1,
    Running = 2,
}

/// <summary>
/// One running script: what it runs, where it is, its locals and its stack. Everything is plain data
/// (the code is found again from <see cref="Kind"/>, <see cref="Number"/> and <see cref="Room"/>), so a
/// slot saves as it is. Unlike ScummVM, which shares one stack between all scripts, each slot has its
/// own: a script's stack is empty at every break, so it behaves the same and keeps slots independent.
/// </summary>
public sealed class ScriptSlot
{
    /// <summary>Locals 0–25 can be read and written (v8 checks 0..25); starting a script sets the first 25.</summary>
    public const int LocalCount = 26, InitializedLocals = 25;

    /// <summary>How deep a stack may grow (ScummVM's shared stack has 256 entries).</summary>
    public const int StackLimit = 256;

    private readonly List<int> _stack = [];

    public ScriptSlot(int index)
    {
        Index = index;
    }

    /// <summary>This slot's place in <see cref="VmState.Slots"/> (slot 0 is never handed out).</summary>
    public int Index { get; }

    public SlotStatus Status { get; set; }
    public ScriptKind Kind { get; set; }
    /// <summary>The script number, or the object id for object code, or the room for entry and exit code.</summary>
    public int Number { get; set; }
    public int Room { get; set; }
    /// <summary>For object code: the verb that was run.</summary>
    public int Verb { get; set; }
    /// <summary>The code being run (found from the fields above; not part of a save).</summary>
    public ScriptCode Code { get; set; }
    /// <summary>The offset of the next instruction in <see cref="ScriptCode.Code"/>.</summary>
    public int Pc { get; set; }

    /// <summary>Jiffies left to wait (<c>delay</c>), and frames left to wait (<c>delay-frames</c>).</summary>
    public int Delay { get; set; }
    public int DelayFrames { get; set; }
    public bool FreezeResistant { get; set; }
    public bool Recursive { get; set; }
    /// <summary>Whether it already ran this frame.</summary>
    public bool DidExec { get; set; }
    public int FreezeCount { get; set; }
    public int CutsceneOverride { get; set; }
    public int Cycle { get; set; }

    public int[] Locals { get; } = new int[LocalCount];

    public IReadOnlyList<int> Stack => _stack;

    public bool IsAlive => Status != SlotStatus.Dead;

    public void Push(int value)
    {
        if (_stack.Count >= StackLimit) throw new ScriptException($"{this}: stack overflow");
        _stack.Add(value);
    }

    public int Pop()
    {
        if (_stack.Count == 0) throw new ScriptException($"{this}: stack underflow");
        int v = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        return v;
    }

    /// <summary>
    /// Sets the slot up to run <paramref name="code"/> from its entry. The first 25 locals come from
    /// <paramref name="args"/> (missing ones are 0); local 25 keeps what it had, as in ScummVM's
    /// <c>initializeLocals</c>.
    /// </summary>
    public void Start(ScriptCode code, IReadOnlyList<int> args = null, int verb = 0, bool freezeResistant = false, bool recursive = false, int cycle = 1)
    {
        Status = SlotStatus.Running;
        Kind = code.Kind;
        Number = code.Number;
        Room = code.Room;
        Verb = verb;
        Code = code;
        Pc = code.Entry;
        Delay = 0;
        DelayFrames = 0;
        FreezeResistant = freezeResistant;
        Recursive = recursive;
        DidExec = false;
        FreezeCount = 0;
        CutsceneOverride = 0;
        Cycle = cycle;
        for (int i = 0; i < InitializedLocals; i++) Locals[i] = args != null && i < args.Count ? args[i] : 0;
        _stack.Clear();
    }

    /// <summary>Ends the script; the slot can be handed out again.</summary>
    public void Kill()
    {
        Status = SlotStatus.Dead;
        Code = null;
        _stack.Clear();
    }

    public override string ToString()
    {
        return Code != null ? $"slot {Index} ({Code})" : $"slot {Index} ({Kind} {Number})";
    }
}
