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
/// One running script: what it runs, where it is and its locals. Everything is plain data (the code is
/// found again from <see cref="Kind"/>, <see cref="Number"/> and <see cref="Room"/>), so a slot saves as
/// it is. The stack is shared by all scripts (<see cref="VmState.Push"/>): a called script hands back its
/// result by leaving it there.
/// </summary>
public sealed class ScriptSlot
{
    /// <summary>Locals 0–25 can be read and written (v8 checks 0..25); starting a script sets the first 25.</summary>
    public const int LocalCount = 26, InitializedLocals = 25;

    public ScriptSlot(int index)
    {
        Index = index;
    }

    /// <summary>This slot's place in <see cref="VmState.Slots"/> (slot 0 is never handed out).</summary>
    public int Index { get; }

    public SlotStatus Status { get; set; }
    public ScriptKind Kind { get; set; }
    /// <summary>
    /// The script number, or the object id for object code; a room's exit and entry code run as 10001 and
    /// 10002 (ScummVM's numbers, which <c>stop-object</c> and <c>script-running</c> see).
    /// </summary>
    public int Number { get; set; }
    public int Room { get; set; }
    /// <summary>For object code: the verb that was run.</summary>
    public int Verb { get; set; }
    /// <summary>For object code: run from the inventory (an actor carries the object), so leaving the room doesn't end it.</summary>
    public bool Carried { get; set; }
    /// <summary>The code being run (found from the fields above; not part of a save).</summary>
    public ScriptCode Code { get; set; }
    /// <summary>The offset of the next instruction in <see cref="ScriptCode.Code"/>.</summary>
    public int Pc { get; set; }

    /// <summary>Jiffies left to wait (<c>delay</c>), and frames left to wait (<c>delay-frames</c>).</summary>
    public int Delay { get; set; }
    public int DelayFrames { get; set; }
    /// <summary>Frozen by <c>freeze-scripts</c> (ScummVM sets bit 0x80 of the status): it neither runs nor counts down its delay.</summary>
    public bool Frozen { get; set; }
    public bool FreezeResistant { get; set; }
    public bool Recursive { get; set; }
    /// <summary>Whether it already ran this frame.</summary>
    public bool DidExec { get; set; }
    public int FreezeCount { get; set; }
    public int CutsceneOverride { get; set; }
    public int Cycle { get; set; }

    public int[] Locals { get; } = new int[LocalCount];

    public bool IsAlive => Status != SlotStatus.Dead;

    /// <summary>Whether it runs this frame: running and not frozen (ScummVM <c>status == ssRunning</c>).</summary>
    public bool IsRunnable => Status == SlotStatus.Running && !Frozen;

    /// <summary>Whether leaving the room ends it: local scripts, room entry and exit code, and code of objects in the room.</summary>
    public bool BelongsToRoom => Kind is ScriptKind.Local or ScriptKind.RoomEntry or ScriptKind.RoomExit || (Kind == ScriptKind.Object && !Carried);

    /// <summary>Whether <c>stop-script</c> and <c>script-running</c> see it (global or local code, not object or room code).</summary>
    public bool IsScript => Kind is ScriptKind.Global or ScriptKind.Local;

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
        Carried = false;
        Code = code;
        Pc = code.Entry;
        Delay = 0;
        DelayFrames = 0;
        Frozen = false;
        FreezeResistant = freezeResistant;
        Recursive = recursive;
        DidExec = false;
        FreezeCount = 0;
        CutsceneOverride = 0;
        Cycle = cycle;
        for (int i = 0; i < InitializedLocals; i++) Locals[i] = args != null && i < args.Count ? args[i] : 0;
    }

    /// <summary>Ends the script (number 0, dead, as ScummVM leaves it); the slot can be handed out again.</summary>
    public void Kill()
    {
        Status = SlotStatus.Dead;
        Frozen = false;
        Number = 0;
        Code = null;
    }

    public override string ToString()
    {
        return Code != null ? $"slot {Index} ({Code})" : $"slot {Index} ({Kind} {Number})";
    }
}
