using System;
using System.Collections;
using System.Collections.Generic;

namespace MMUCS.Runtime.Scripts;

/// <summary>
/// The script machine's state: global and bit variables, arrays, and the script slots with their
/// locals and stacks. Nothing runs here (the interpreter does that); this is what the scripts read and
/// write, laid out as v8 addresses it, and it's all plain data so it can be saved.
/// <para>
/// v8 variable numbers (ScummVM <c>ScummEngine_v8::readVar</c> / <c>writeVar</c>): no flag bits is a global
/// (0 to <see cref="GameLimits.Variables"/> − 1), bit 31 a bit variable, bit 30 a local of the running
/// script (0–25). v8 has no room variables. Arrays are named by a variable holding the array's id.
/// </para>
/// </summary>
public sealed class VmState
{
    /// <summary>Script slots (ScummVM <c>NUM_SCRIPT_SLOT</c>); slot 0 is never handed out.</summary>
    public const int SlotCount = 80;

    public const uint BitFlag = 0x80000000, LocalFlag = 0x40000000;

    /// <summary>How deep the stack may grow (ScummVM <c>_vmStack[256]</c>).</summary>
    public const int StackLimit = 256;

    private readonly List<int> _stack = [];
    private readonly int[] _globals;
    private readonly BitArray _bits;
    private readonly ScriptArray[] _arrays;

    public VmState(GameLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        Limits = limits;
        _globals = new int[limits.Variables];
        _bits = new BitArray(limits.BitVariables);
        _arrays = new ScriptArray[Math.Max(limits.Arrays, 1)];
        Objects = new ObjectTables(limits);
        Slots = new ScriptSlot[SlotCount];
        for (int i = 0; i < SlotCount; i++) Slots[i] = new ScriptSlot(i);
        foreach (var (variable, dim2, dim1) in limits.IndexArrays)
            DefineArray(variable, ArrayType.Int, dim2, dim1);
    }

    public GameLimits Limits { get; }

    public ScriptSlot[] Slots { get; }

    /// <summary>Owners, states, rooms and classes of the global objects, and the inventory.</summary>
    public ObjectTables Objects { get; }

    /// <summary>The slot whose locals a local variable means (the running script), or null.</summary>
    public ScriptSlot Current { get; set; }

    /// <summary>
    /// The stack, shared by every script as in ScummVM: a script called with <c>startScriptQuick2</c>
    /// pushes its result and returns, and the caller pops it.
    /// </summary>
    public IReadOnlyList<int> Stack => _stack;

    public void Push(int value)
    {
        if (_stack.Count >= StackLimit) throw new ScriptException("stack overflow");
        _stack.Add(value);
    }

    public int Pop()
    {
        if (_stack.Count == 0) throw new ScriptException("stack underflow");
        int v = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        return v;
    }

    /// <summary>
    /// A list as scripts push it, count on top (ScummVM <c>getStackList</c>), in push order.
    /// </summary>
    public int[] PopList(int max = 128)
    {
        int n = Pop();
        if (n < 0 || n > max) throw new ScriptException($"{n} items in a stack list, max {max}");
        var list = new int[n];
        for (int i = n - 1; i >= 0; i--) list[i] = Pop();
        return list;
    }

    public void ClearStack() => _stack.Clear();

    /// <summary>A global variable by number, without the flag checks of <see cref="Read"/>.</summary>
    public int this[int global]
    {
        get => _globals[CheckGlobal(global, "reading")];
        set => _globals[CheckGlobal(global, "writing")] = value;
    }

    public bool Bit(int bit) => _bits[CheckBit(bit, "reading")];

    public void SetBit(int bit, bool value) => _bits[CheckBit(bit, "writing")] = value;

    /// <summary>Reads variable <paramref name="variable"/> as a script names it.</summary>
    public int Read(int variable)
    {
        uint v = (uint)variable;
        if ((v & 0xF0000000) == 0) return this[(int)v];
        if ((v & BitFlag) != 0) return Bit((int)(v & 0x7FFFFFFF)) ? 1 : 0;
        if ((v & LocalFlag) != 0) return CurrentLocals("reading")[CheckLocal(v, "reading")];
        throw new ScriptException($"illegal variable 0x{v:X8} (reading)");
    }

    /// <summary>Writes variable <paramref name="variable"/> as a script names it; a bit variable takes any non-zero value as 1.</summary>
    public void Write(int variable, int value)
    {
        uint v = (uint)variable;
        if ((v & 0xF0000000) == 0) this[(int)v] = value;
        else if ((v & BitFlag) != 0) SetBit((int)(v & 0x7FFFFFFF), value != 0);
        else if ((v & LocalFlag) != 0) CurrentLocals("writing")[CheckLocal(v, "writing")] = value;
        else throw new ScriptException($"illegal variable 0x{v:X8} (writing)");
    }

    /// <summary>The array whose id variable <paramref name="variable"/> holds, or null when it holds none.</summary>
    public ScriptArray Array(int variable)
    {
        int id = Read(variable);
        return id > 0 && id < _arrays.Length ? _arrays[id] : null;
    }

    /// <summary>The array with id <paramref name="id"/>, or null.</summary>
    public ScriptArray ArrayById(int id) => id > 0 && id < _arrays.Length ? _arrays[id] : null;

    /// <summary>
    /// Makes a new array for <paramref name="variable"/>, dropping the one it pointed at: the lowest free
    /// id is written to the variable (ScummVM <c>ScummEngine_v6::defineArray</c>). <paramref name="dim2"/>
    /// and <paramref name="dim1"/> are the highest indexes, so each dimension has one more element.
    /// </summary>
    public ScriptArray DefineArray(int variable, ArrayType type, int dim2, int dim1)
    {
        if (((uint)variable & BitFlag) != 0) throw new ScriptException("can't define a bit variable as an array pointer");
        NukeArray(variable);
        int id = FreeArrayId();
        var array = new ScriptArray(type, dim2, dim1);
        Write(variable, id);
        _arrays[id] = array;
        return array;
    }

    /// <summary>Puts <paramref name="array"/> in place of the one with id <paramref name="id"/> (ScummVM nukes and makes a string resource so).</summary>
    public void ReplaceArray(int id, ScriptArray array)
    {
        if (id > 0 && id < _arrays.Length) _arrays[id] = array;
    }

    /// <summary>Drops the array <paramref name="variable"/> points at and sets the variable to 0.</summary>
    public void NukeArray(int variable)
    {
        int id = Read(variable);
        if (id > 0 && id < _arrays.Length) _arrays[id] = null;
        Write(variable, 0);
    }

    /// <summary>The first dead slot from slot 1 (ScummVM <c>getScriptSlot</c>).</summary>
    public ScriptSlot FreeSlot()
    {
        for (int i = 1; i < SlotCount; i++)
            if (!Slots[i].IsAlive) return Slots[i];
        throw new ScriptException($"too many scripts running, {SlotCount} max");
    }

    private int FreeArrayId()
    {
        for (int i = 1; i < _arrays.Length; i++)
            if (_arrays[i] == null) return i;
        throw new ScriptException($"out of array ids, {_arrays.Length} max");
    }

    private int[] CurrentLocals(string what)
    {
        return Current?.Locals ?? throw new ScriptException($"local variable {what} with no script running");
    }

    private int CheckGlobal(int v, string what)
    {
        if (v < 0 || v >= _globals.Length) throw new ScriptException($"variable {v} out of range 0..{_globals.Length - 1} ({what})");
        return v;
    }

    private int CheckBit(int v, string what)
    {
        if (v < 0 || v >= _bits.Length) throw new ScriptException($"bit variable {v} out of range 0..{_bits.Length - 1} ({what})");
        return v;
    }

    private static int CheckLocal(uint v, string what)
    {
        uint n = v & 0x0FFFFFFF;
        if (n >= ScriptSlot.LocalCount) throw new ScriptException($"local variable {n} out of range 0..{ScriptSlot.LocalCount - 1} ({what})");
        return (int)n;
    }
}
