using System;
using System.Collections.Generic;
using System.Linq;

namespace MMUCS.Runtime.Scripts;

/// <summary>
/// An object image the scripts drew over the room this frame (ScummVM's blast object queue): object
/// <see cref="Object"/>'s image <see cref="Image"/> (from 1) at a page rectangle, scaled (255 is full
/// size); <see cref="Mode"/> 3 draws it as a shadow.
/// </summary>
public sealed record BlastObject(int Object, int X, int Y, int Width, int Height, int ScaleX, int ScaleY, int Image, int Mode)
{
    public bool Contains(int x, int y) => x >= X && x < X + Width && y >= Y && y < Y + Height;
}

/// <summary>
/// Floating objects and blast objects (ScummVM object.cpp's loadFlObject, enqueueObject and findBlastObject):
/// <c>load-object</c> brings an object from its home room into the current room's object slots (the
/// inventory items, the verb coin), and <c>super-blast-object</c> draws one's image over the room for a
/// frame, which the hit test of kernel-get 216 looks through.
/// </summary>
public sealed partial class ScriptVm
{
    private readonly List<BlastObject> _blastObjects = [];

    /// <summary>The floating objects in the current room's slots and whether the scripts locked them (kept from room to room).</summary>
    private readonly Dictionary<int, bool> _floating = [];

    /// <summary>The objects the last frame blasted, in the order drawn.</summary>
    public IReadOnlyList<BlastObject> BlastObjects { get; private set; } = [];

    /// <summary>Whether an object was brought into the current room as a floating object.</summary>
    public bool IsFloating(int obj) => _floating.ContainsKey(obj);

    /// <summary>
    /// ScummVM <c>loadFlObject</c>: object <paramref name="obj"/> from its home room takes a free slot in the
    /// current room's objects, image and code, unless it's here already.
    /// </summary>
    public void LoadFloatingObject(int obj)
    {
        if (PlaceOf(obj) != null) return;
        int room = State.Objects.IsObject(obj) ? State.Objects.Room(obj) : 0;
        var home = Scripts.Places(room).LastOrDefault(p => p.Id == obj)
            ?? throw new ScriptException($"load-object: object {obj} is not in its room {room}");
        AddSlot(home);
        _floating[obj] = false;
    }

    /// <summary>A floating object gets the next slot; it belongs to no other object here.</summary>
    private void AddSlot(ObjectPlace place)
    {
        var slots = _roomObjects.ToList();
        slots.Add(place with { Slot = slots.Count + 1, Parent = 0, ParentState = 0 });
        _roomObjects = slots;
        Array.Resize(ref _localStates, slots.Count + 1);
        _localStates[^1] = State.Objects.IsObject(place.Id) ? State.Objects.State(place.Id) : 0;
    }

    /// <summary>
    /// ScummVM <c>clearRoomObjects</c>: on a room change the floating objects go, except those the scripts
    /// locked, which take slots in the new room after its own.
    /// </summary>
    private void KeepLockedFloatingObjects(IReadOnlyList<ObjectPlace> oldSlots)
    {
        var kept = _floating.Where(f => f.Value).Select(f => f.Key).ToList();
        _floating.Clear();
        foreach (int obj in kept)
        {
            if (oldSlots.LastOrDefault(p => p.Id == obj) is not { } place || PlaceOf(obj) != null) continue;
            AddSlot(place);
            _floating[obj] = true;
        }
    }

    /// <summary>kernel-set 11 and 12: a floating object stays (or not) when the room changes.</summary>
    private void LockFloatingObject(int obj, bool locked)
    {
        if (PlaceOf(obj) == null) throw new ScriptException($"object {obj} isn't in the room to lock");
        if (_floating.ContainsKey(obj)) _floating[obj] = locked;
    }

    /// <summary>
    /// ScummVM <c>enqueueObject</c>: an object of the room (floating ones too) drawn at x,y this frame; a 0
    /// width or height is the object's own.
    /// </summary>
    private void Blast(int obj, int x, int y, int width, int height, int scaleX, int scaleY, int image, int mode)
    {
        var place = PlaceOf(obj) ?? throw new ScriptException($"blast object {obj} is not in room {CurrentRoom}");
        _blastObjects.Add(new BlastObject(obj, x, y, width != 0 ? width : place.Width, height != 0 ? height : place.Height, scaleX, scaleY, image, mode));
    }

    /// <summary>kernel-get 216 (ScummVM findBlastObject): the last blasted object over the point, not untouchable; 0 for none.</summary>
    private int FindBlastObject(int x, int y)
    {
        x += (State[EngineVars.CameraPosX] - ScreenWidth / 2) & 7;
        for (int i = _blastObjects.Count - 1; i >= 0; i--)
        {
            var b = _blastObjects[i];
            if (b.Contains(x, y) && !(State.Objects.IsObject(b.Object) && State.Objects.HasClass(b.Object, ObjectTables.Untouchable))) return b.Object;
        }
        return 0;
    }

    /// <summary>The frame is drawn with its blast objects; they're gone next frame (ScummVM removeBlastObjects).</summary>
    private void EndFrameBlasts()
    {
        BlastObjects = [.. _blastObjects];
        _blastObjects.Clear();
        _iconStep = null;
    }
}
