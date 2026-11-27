using System;
using System.Collections.Generic;

namespace MMUCS.Runtime.Scripts;

/// <summary>
/// What the engine keeps per global object: its owner, state, home room and classes, plus the inventory,
/// the objects actors carry in the order they were picked up (ScummVM <c>_objectOwnerTable</c>,
/// <c>_objectStateTable</c>, <c>_objectRoomTable</c>, <c>_classData</c>, <c>_inventory</c>). In v7+ an
/// object owned by <see cref="RoomOwner"/> (0xFF, every object's start) is in its room; any other owner
/// is an actor carrying it.
/// </summary>
public sealed class ObjectTables
{
    /// <summary>The owner meaning "in the room" (ScummVM <c>OF_OWNER_ROOM</c> for v7+).</summary>
    public const int RoomOwner = 0xFF;

    /// <summary>The class picking an object up sets (ScummVM <c>kObjectClassUntouchable</c>).</summary>
    public const int Untouchable = 32;

    private readonly byte[] _owner, _state, _room;
    private readonly uint[] _classes;
    private readonly int[] _inventory;
    private readonly Dictionary<int, byte[]> _newNames = [];
    private readonly int _maxNewNames;

    public ObjectTables(GameLimits limits)
    {
        int n = Math.Max(limits.GlobalObjects, 1);
        _owner = new byte[n];
        _state = new byte[n];
        _room = new byte[n];
        _classes = new uint[n];
        _inventory = new int[Math.Max(limits.Inventory, 1)];
        _maxNewNames = Math.Max(limits.NewNames, 1);
        Array.Fill(_owner, (byte)RoomOwner);
        for (int i = 0; i < limits.IndexObjects.Count && i < n; i++)
            (_state[i], _room[i], _classes[i]) = limits.IndexObjects[i];
    }

    /// <summary>Counts changes to states, owners and classes, so a host can tell when to draw the room again.</summary>
    public int Version { get; private set; }

    /// <summary>How many global object numbers there are (0 is never an object).</summary>
    public int Count => _owner.Length;

    /// <summary>Object ids in the inventory in pickup order, 0 for empty places (the gaps are closed up).</summary>
    public ReadOnlySpan<int> Inventory => _inventory;

    public bool IsObject(int obj) => obj > 0 && obj < Count;

    public int Owner(int obj) => _owner[Check(obj)];

    public void SetOwner(int obj, int owner)
    {
        if (owner is < 0 or > 0xFF) throw new ScriptException($"owner {owner} out of range 0..255");
        _owner[Check(obj)] = (byte)owner;
        Version++;
    }

    public int State(int obj) => _state[Check(obj)];

    public void SetState(int obj, int state)
    {
        if (state is < 0 or > 0xFF) throw new ScriptException($"state {state} out of range 0..255");
        _state[Check(obj)] = (byte)state;
        Version++;
    }

    /// <summary>The room an object's code and image live in.</summary>
    public int Room(int obj) => _room[Check(obj)];

    public uint Classes(int obj) => _classes[Check(obj)];

    public void SetClasses(int obj, uint classes)
    {
        _classes[Check(obj)] = classes;
        Version++;
    }

    /// <summary>Whether <paramref name="obj"/> has class <paramref name="cls"/> (1–32; bit 7 is ignored).</summary>
    public bool HasClass(int obj, int cls) => (_classes[Check(obj)] & ClassBit(cls)) != 0;

    public void SetClass(int obj, int cls, bool on)
    {
        if (on) _classes[Check(obj)] |= ClassBit(cls);
        else _classes[Check(obj)] &= ~ClassBit(cls);
        Version++;
    }

    /// <summary>The name a script gave <paramref name="obj"/> (ScummVM <c>_newNames</c>), or null for its room's own.</summary>
    public byte[] NewName(int obj) => _newNames.GetValueOrDefault(obj);

    /// <summary>
    /// Gives <paramref name="obj"/> a new name in place of its room's (ScummVM <c>setObjectName</c>), kept
    /// whichever room is current, so a room can name another's objects (Puerto Pollo on the Plunder map).
    /// </summary>
    public void SetName(int obj, byte[] name)
    {
        Check(obj);
        // A save from before names were kept has no cap (0): it goes unchecked.
        if (_maxNewNames > 0 && !_newNames.ContainsKey(obj) && _newNames.Count >= _maxNewNames)
            throw new ScriptException($"new name of {obj} overflows the name table ({_maxNewNames} max)");
        _newNames[obj] = name;
        Version++;
    }

    /// <summary>
    /// Drops the new names of objects no one owns any more (ScummVM <c>killScriptsAndResources</c>, on leaving
    /// a room; in v7+ an object in its room keeps its name).
    /// </summary>
    public void ForgetUnownedNames()
    {
        foreach (int obj in new List<int>(_newNames.Keys))
            if (_owner[obj] == 0) _newNames.Remove(obj);
    }

    public bool InInventory(int obj) => obj > 0 && Array.IndexOf(_inventory, obj) >= 0;

    /// <summary>Puts <paramref name="obj"/> in the first free inventory place (ScummVM <c>addObjectToInventory</c>).</summary>
    public void AddToInventory(int obj)
    {
        int i = Array.IndexOf(_inventory, 0);
        if (i < 0) throw new ScriptException($"inventory full, {_inventory.Length} max items");
        _inventory[i] = obj;
    }

    /// <summary>Takes <paramref name="obj"/> out of the inventory and closes the gap.</summary>
    public void RemoveFromInventory(int obj)
    {
        int i = Array.IndexOf(_inventory, obj);
        if (i < 0) return;
        Array.Copy(_inventory, i + 1, _inventory, i, _inventory.Length - i - 1);
        _inventory[^1] = 0;
    }

    /// <summary>How many inventory objects <paramref name="owner"/> has (ScummVM <c>getInventoryCount</c>).</summary>
    public int InventoryCount(int owner)
    {
        int n = 0;
        foreach (int obj in _inventory)
            if (obj != 0 && _owner[obj] == owner) n++;
        return n;
    }

    /// <summary>The <paramref name="index"/>th (from 1) inventory object <paramref name="owner"/> has, or 0 (ScummVM <c>findInventory</c>).</summary>
    public int FindInventory(int owner, int index)
    {
        int count = 1;
        foreach (int obj in _inventory)
            if (obj != 0 && _owner[obj] == owner && count++ == index) return obj;
        return 0;
    }

    private static uint ClassBit(int cls)
    {
        cls &= 0x7F;
        if (cls is < 1 or > 32) throw new ScriptException($"class {cls} out of range 1..32");
        return 1u << (cls - 1);
    }

    private int Check(int obj)
    {
        if (obj < 0 || obj >= Count) throw new ScriptException($"object {obj} out of range 0..{Count - 1}");
        return obj;
    }
}
