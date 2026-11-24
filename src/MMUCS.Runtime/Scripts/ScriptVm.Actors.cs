using System;
using System.Linq;
using MMUCS.Core.Scumm;

namespace MMUCS.Runtime.Scripts;

/// <summary>The actor instructions (ScummVM script_v6.cpp and script_v8.cpp), on <see cref="Actors"/>.</summary>
public sealed partial class ScriptVm
{
    /// <summary>The actor <c>actorOps</c> works on (ScummVM <c>_curActor</c>).</summary>
    private int _curActor;

    /// <summary>The engine's actors.</summary>
    public ActorTable Actors { get; }

    /// <summary>The actor <c>actorOps</c> works on.</summary>
    public int CurrentActor => _curActor;

    internal void Warn(string message) => Warned?.Invoke(message);

    /// <summary>The frame's turning actors turn (ScummVM <c>walkActors</c>; walking itself comes later).</summary>
    public void WalkActors() => Guarded("walking actors", Actors.Walk);

    /// <summary>The frame's drawing and costume steps (ScummVM <c>processActors</c>): <see cref="ActorTable.Pictures"/> is what it drew.</summary>
    public void ProcessActors() => Guarded("animating actors", Actors.Process);

    /// <summary>Engine work outside the scripts: what the engine would stop on is a fault here, and the game carries on.</summary>
    private void Guarded(string what, Action work)
    {
        try { work(); }
        catch (ScriptException e) when (!Strict) { Faulted?.Invoke(new ScriptFault("engine", 0, what, e.Message)); }
    }

    /// <summary>
    /// Where something is for an actor going to it (ScummVM <c>getObjectOrActorXY</c>): an actor in this room
    /// where it stands; an object in the room at its hotspot; one an actor carries where that actor is; null
    /// when it isn't here.
    /// </summary>
    public (int X, int Y)? ObjectOrActorXY(int obj)
    {
        if (obj < ActorTable.Count)
            return Actors.Find(obj) is { } a && Actors.IsInCurrentRoom(a) ? (a.X, a.Y) : null;
        var objects = State.Objects;
        if (!objects.IsObject(obj)) return null;
        if (objects.Owner(obj) != ObjectTables.RoomOwner)
        {
            int owner = objects.Owner(obj);
            return objects.InInventory(obj) && owner < ActorTable.Count && Actors.Find(owner) is { } carrier && Actors.IsInCurrentRoom(carrier)
                ? (carrier.X, carrier.Y) : null;
        }
        return PlaceOf(obj) is { } place ? Stand(place) : null;
    }

    /// <summary>The current room's slot for <paramref name="obj"/> (the last one, as ScummVM searches), or null.</summary>
    private ObjectPlace PlaceOf(int obj)
    {
        for (int i = _roomObjects.Count - 1; i >= 0; i--)
            if (_roomObjects[i].Id == obj) return _roomObjects[i];
        return null;
    }

    private (int X, int Y) Stand(ObjectPlace place)
    {
        var (x, y, _) = place.StandAt(State.Objects.IsObject(place.Id) ? State.Objects.State(place.Id) : 0);
        return (x, y);
    }

    /// <summary>ScummVM <c>getObjX</c>/<c>getObjY</c>: an actor's place, an object's (−1 when it isn't here), 0 below 1.</summary>
    private int ObjectCoordinate(int obj, bool y)
    {
        if (obj < 1) return 0;
        if (obj < ActorTable.Count)
        {
            var a = Actors[obj];
            return y ? a.Y : a.X;
        }
        return ObjectOrActorXY(obj) is { } at ? (y ? at.Y : at.X) : -1;
    }

    /// <summary>ScummVM <c>getObjNewDir</c>: the way an actor faces, or the way an actor at an object faces.</summary>
    private int ObjectDirection(int obj)
    {
        if (obj < ActorTable.Count) return Actors[obj].Facing;
        return PlaceOf(obj) is { } place ? place.StandAt(State.Objects.State(obj)).Dir : 0;
    }

    /// <summary>Carries out an actor instruction; false for one that isn't.</summary>
    private bool ExecActor(ScriptSlot s, ScriptInstruction ins)
    {
        switch (ins.Opcode)
        {
            case 0xac: ActorOps(s, ins); return true;
            case 0x69: Wait(s, ins); return true;
            case 0xa1: // put-actor at x,y in-room
            {
                int room = Pop(), y = Pop(), x = Pop();
                var a = Actors[Pop()];
                if (room is 0xFF or 0x7FFFFFFF) room = a.Room;
                else if (room != 0) a.Room = room;
                Actors.Put(a, x, y, room);
                return true;
            }
            case 0xa2: // put-actor at-object
            {
                int obj = Pop();
                int room = State.Objects.IsObject(obj) ? State.Objects.Room(obj) : 0;
                var a = Actors[Pop()];
                // Where it is if it's here (in the room or carried), the middle of a 320×240 screen otherwise.
                bool here = State.Objects.IsObject(obj) && (State.Objects.Owner(obj) == ObjectTables.RoomOwner ? PlaceOf(obj) != null : State.Objects.InInventory(obj));
                var (x, y) = here && PlaceOf(obj) is { } place ? Stand(place) : (160, 120);
                if (room == 0xFF) room = a.Room;
                Actors.Put(a, x, y, room);
                return true;
            }
            case 0xa3: { int obj = Pop(); Actors.FaceTo(Actors[Pop()], obj); return true; }
            case 0xa4: { int anim = Pop(); Actors.Animate(Actors[Pop()], anim); return true; }
            case 0xa7: // set-box [...] to flags
            {
                int value = Pop();
                var boxes = State.PopList(65);
                for (int i = boxes.Length - 1; i >= 0; i--) Actors.SetBoxFlags(boxes[i], value);
                return true;
            }
            case 0xa8: return true; // set-box-path: the walk matrix comes with walking
            case 0xd9: { int box = Pop(); Push(Actors.IsInBox(Actors[Pop()], box)); return true; }
            case 0xdb: { int y = Pop(), x = Pop(); Push(ActorAt(x, y)); return true; }
            case 0xe1: { int var = Pop(); var a = Actors[Pop()]; Push(var is >= 0 and < Actor.AnimVars ? a.Vars[var] : throw new ScriptException($"{a} animation variable {var} out of range")); return true; }
            case 0xe2: { int act = Pop(); Push(act is 0 or 255 ? 0 : Actors[act].Room); return true; }
            case 0xe3: { var a = Actors[Pop()]; Push(a.IgnoreBoxes ? 0 : a.Walkbox); return true; }
            case 0xe4: Push(Actors[Pop()].Moving); return true;
            case 0xe5: Push(Actors[Pop()].Costume); return true;
            case 0xe6: Push(Actors[Pop()].ScaleX); return true;
            case 0xe7: Push(Actors[Pop()].Layer); return true;
            case 0xe8: Push(Actors[Pop()].Elevation); return true;
            case 0xe9: Push(Actors[Pop()].Width); return true;
            case 0xea: Push(ObjectDirection(Pop())); return true;
            case 0xeb: Push(ObjectCoordinate(Pop(), y: false)); return true;
            case 0xec: Push(ObjectCoordinate(Pop(), y: true)); return true;
            case 0xed: Push(Actors[Pop()].Frame); return true;
            case 0xf7: // actor-zplane
            {
                var a = Actors[Pop()];
                Push(a.ForceClip == Actor.BoxClip ? Actors.BoxMask(a.Walkbox) : a.ForceClip);
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// ScummVM <c>getActorFromPos</c>: the lowest-numbered actor, not untouchable, drawn over the point in the
    /// last frame; 0 for none.
    /// </summary>
    public int ActorAt(int x, int y)
    {
        foreach (var a in Actors.All.Skip(1))
        {
            if (a.Drawn is not var (l, t, r, b)) continue;
            if (x < l || x >= r || y < t || y > b) continue;
            if (State.Objects.IsObject(a.Number) && State.Objects.HasClass(a.Number, ObjectTables.Untouchable)) continue;
            return a.Number;
        }
        return 0;
    }

    /// <summary>
    /// ScummVM <c>o8_actorOps</c>. Its arguments always come off the stack, even when the current actor
    /// doesn't exist (ScummVM leaves them there; the scripts never do it).
    /// </summary>
    private void ActorOps(ScriptSlot s, ScriptInstruction ins)
    {
        if (ins.SubOp == 0x7A)
        {
            _curActor = Pop();
            return;
        }
        var args = new int[ins.Args.Length];
        for (int i = args.Length - 1; i >= 0; i--) args[i] = Pop();
        if (Actors.Find(_curActor) is not { } a) return;
        int A0() => args[0];
        switch (ins.SubOp)
        {
            case 0x64: Actors.SetCostume(a, A0()); break;
            case 0x65: a.SpeedX = args[0]; a.SpeedY = args[1]; break;
            case 0x67:
                a.InitFrame = 1;
                a.WalkFrame = 2;
                a.StandFrame = 3;
                a.TalkStartFrame = 4;
                a.TalkStopFrame = 5;
                break;
            case 0x68: a.InitFrame = A0(); break;
            case 0x69: a.TalkStartFrame = args[0]; a.TalkStopFrame = args[1]; break;
            case 0x6A: a.WalkFrame = A0(); break;
            case 0x6B: a.StandFrame = A0(); break;
            case 0x6C: a.AnimSpeed = A0(); a.AnimProgress = 0; break;
            case 0x6D: a.Init(0); break;
            case 0x6E:
                if (a.Elevation != A0()) { a.Elevation = A0(); a.NeedRedraw = true; }
                break;
            case 0x6F:
                if (args[0] is < 0 or > 31) throw new ScriptException($"{a} palette slot {args[0]} out of range");
                a.Palette[args[0]] = args[1];
                break;
            case 0x70: a.TalkColor = A0(); break;
            case 0x71:
                a.Name = ins.TextStart >= 0 ? System.Text.Encoding.Latin1.GetString(s.Code.Code.Span.Slice(ins.TextStart, ins.TextLength)) : "";
                break;
            case 0x72: a.Width = A0(); break;
            case 0x73: a.ScaleX = a.ScaleY = A0(); break;
            case 0x74: a.ForceClip = 0; break;
            case 0x75: // the engine keeps it in a byte, so −1 is 255 too: the walk box's plane
                a.ForceClip = (A0() & 0xFF) == 255 ? Actor.BoxClip : A0() & 0xFF;
                break;
            case 0x76:
            case 0x77:
                a.IgnoreBoxes = ins.SubOp == 0x76;
                a.ForceClip = Actor.BoxClip;
                if (Actors.IsInCurrentRoom(a)) Actors.Put(a, a.X, a.Y, a.Room);
                break;
            case 0x78: a.ShadowMode = A0(); break;
            case 0x79: a.TalkX = args[0]; a.TalkY = args[1]; break;
            case 0x7B:
                if (args[0] is < 0 or >= Actor.AnimVars) throw new ScriptException($"{a} animation variable {args[0]} out of range");
                a.Vars[args[0]] = args[1];
                break;
            case 0x7C: a.IgnoreTurns = true; break;
            case 0x7D: a.IgnoreTurns = false; break;
            case 0x7E: a.Init(2); break;
            case 0x7F: a.Layer = A0(); break;
            case 0x80:
                Actors.StopMoving(a);
                Actors.StartAnim(a, a.StandFrame);
                break;
            case 0x81:
                a.Moving &= ~Actor.Turning;
                Actors.SetDirection(a, A0());
                break;
            case 0x82: Actors.TurnTo(a, A0()); break;
            case 0x83: a.WalkScript = A0(); break;
            case 0x84: a.TalkScript = A0(); break;
            case 0x85: a.Moving |= Actor.Frozen; break;
            case 0x86: a.Moving &= ~Actor.Frozen; break;
            case 0x87: a.TalkVolume = A0(); break;
            case 0x88: a.TalkFrequency = A0(); break;
            case 0x89: a.TalkPan = A0(); break;
            default: throw new ScriptException($"actorOps sub-op 0x{ins.SubOp:x2}");
        }
    }

    /// <summary>
    /// ScummVM <c>o8_wait</c>: while what it waits for goes on, the script tries again next frame (those with
    /// an actor jump back to push it again). The camera doesn't move yet, so it's never waited for.
    /// </summary>
    private void Wait(ScriptSlot s, ScriptInstruction ins)
    {
        bool wait;
        switch (ins.SubOp)
        {
            case 0x1E: { var a = Actors[Pop()]; wait = Actors.IsInCurrentRoom(a) && a.IsMoving; break; }
            case 0x1F: wait = State[EngineVars.HaveMessage] != 0; break;
            case 0x20: wait = false; break;
            case 0x21: wait = State[EngineVars.SentenceScript] is int sentence and not 0 && IsScriptRunning(sentence); break;
            case 0x22: { var a = Actors[Pop()]; wait = Actors.IsInCurrentRoom(a) && a.NeedRedraw; break; }
            case 0x23: { var a = Actors[Pop()]; wait = Actors.IsInCurrentRoom(a) && (a.Moving & Actor.Turning) != 0; break; }
            default: throw new ScriptException($"wait sub-op 0x{ins.SubOp:x2}");
        }
        if (!wait) return;
        s.Pc = ins.SubOp is 0x1E or 0x22 or 0x23 ? ins.Target : ins.Offset;
        BreakHere();
    }
}
