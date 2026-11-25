using System;
using System.Collections.Generic;
using System.Linq;
using MMUCS.Core.Scumm;

namespace MMUCS.Runtime.Scripts;

/// <summary>
/// An actor as the engine drew it in the last frame (before its costume stepped on, as ScummVM draws then
/// animates): its costume's frame, which way round and how big, where (room pixels; <see cref="Y"/> already
/// lifted by the elevation), and the z-plane it's cut by (<see cref="FromBox"/>: the walk box's, which the
/// engine keeps within the room's planes). <see cref="FromX"/>, <see cref="FromY"/>: where the frame before
/// drew it (its own place when it wasn't drawn or wore another costume), for a view that slides actors between frames.
/// <see cref="Remap"/> is the actor's own colours for the costume's (<c>actor N palette</c>, <c>remap-costume</c>):
/// a screen colour for each costume colour, 0xFF for the costume's own (AKPL); null when there are none.
/// The colours are the screen's (<see cref="ScreenPalette"/>) when it's drawn.
/// <see cref="ShadowMode"/> is how its shadow colours draw (<see cref="AkosShadow"/>).
/// </summary>
public sealed record ActorPicture(int Actor, Costume Costume, AkosFrame Frame, bool Mirrored, int Scale, int X, int Y, int ZPlane, bool FromBox, int FromX, int FromY,
    int[] Remap = null, int ShadowMode = 0)
{
    /// <summary>What decides the picture's pixels in palette indices (not where it is, nor the palette), for caching them.</summary>
    public string Key { get; } = $"{Costume.Number}:{(Mirrored ? 1 : 0)}:{Scale}:" + string.Join(";", Frame.Cels.Select(c => $"{c.Cel},{c.X},{c.Y}"))
        + (Remap == null ? "" : ":" + string.Join(",", Remap)) + (ShadowMode == 0 ? "" : $":s{ShadowMode}");

    /// <summary>The actor's colours for a costume of <paramref name="colours"/> colours, or null when it has none of its own.</summary>
    public static int[] RemapOf(int[] palette, int colours)
    {
        int n = Math.Min(colours, palette.Length);
        for (int i = 0; i < n; i++)
            if (palette[i] != 0xFF) return palette[..n];
        return null;
    }
}

/// <summary>
/// The engine's actors and the rules for moving, turning, showing and animating them (ScummVM actor.cpp
/// and akos.cpp for v8): costumes step through their AKOS sequences once a frame (<see cref="Process"/>),
/// carrying out the sequences' commands (variables, jumps, starting animations, hiding the actor). Walking
/// is in ActorWalking.cs.
/// </summary>
public sealed partial class ActorTable
{
    /// <summary>v8 has 80 actors (ScummVM <c>_numActors</c>); object numbers below this are actors.</summary>
    public const int Count = 80;

    /// <summary>Object classes that matter to actors (ScummVM object.h).</summary>
    public const int ClassYFlip = 29, ClassXFlip = 30, ClassPlayer = 31;

    private const uint BoxXFlip = 0x08, BoxYFlip = 0x10;

    private readonly ScriptVm _vm;
    private readonly Random _random;
    private readonly Actor[] _actors = new Actor[Count];
    private readonly Stack<(int Cmd, int Actor, int Param)> _queue = new();
    private readonly int[] _extraBoxFlags = new int[65];
    private List<Walkbox> _boxes = [];

    /// <summary>ScummVM's 20 scale slots: a scale at x1,y1 and another at x2,y2, interpolated between (by y, by x, or both averaged).</summary>
    private readonly (int X1, int Y1, int S1, int X2, int Y2, int S2)[] _scaleSlots = new (int, int, int, int, int, int)[20];

    internal ActorTable(ScriptVm vm, Random random)
    {
        _vm = vm;
        _random = random;
        for (int i = 0; i < Count; i++) _actors[i] = new Actor(i);
        for (int i = 1; i < Count; i++) TakeObjectZeroClasses(_actors[i]);
    }

    /// <summary>
    /// ScummVM <c>Actor::initActor</c> (v7+), the engine's part: the actor's fields from <see cref="Actor.Init"/>,
    /// then its classes become object 0's as they are now, so scripts that make object 0 untouchable before
    /// setting up an actor (<c>actor N default</c>) make that actor untouchable (room 9's cannon overlay).
    /// </summary>
    public void Init(Actor a, int mode)
    {
        a.Init(mode);
        TakeObjectZeroClasses(a);
    }

    private void TakeObjectZeroClasses(Actor a)
    {
        var objects = _vm.State.Objects;
        if (a.Number < objects.Count) objects.SetClasses(a.Number, objects.Classes(0));
    }

    /// <summary>Actor <paramref name="number"/> (ScummVM <c>derefActor</c>: 0 counts, past the table is an error).</summary>
    public Actor this[int number] => number >= 0 && number < Count ? _actors[number] : throw new ScriptException($"invalid actor {number}");

    /// <summary>Actor <paramref name="number"/>, or null past the table (ScummVM <c>derefActorSafe</c>).</summary>
    public Actor Find(int number) => number >= 0 && number < Count ? _actors[number] : null;

    public IEnumerable<Actor> All => _actors;

    /// <summary>The actors as the last frame drew them, back to front (not saved: <see cref="Redraw"/> makes them again after a load).</summary>
    [field: NonSerialized]
    public IReadOnlyList<ActorPicture> Pictures { get; private set; } = [];

    /// <summary>The actors on a layer below 0, which the engine draws over the blasted objects (ScummVM processUpperActors).</summary>
    [field: NonSerialized]
    public IReadOnlyList<ActorPicture> UpperPictures { get; private set; } = [];

    /// <summary>The current room's walk boxes, with the flags the scripts set.</summary>
    public IReadOnlyList<Walkbox> Boxes => _boxes;

    /// <summary>The ego was put somewhere since the room was entered (ScummVM <c>_egoPositioned</c>).</summary>
    public bool EgoPositioned { get; set; }

    public bool IsInCurrentRoom(Actor a) => a.Room == _vm.CurrentRoom;

    // ── Rooms ──

    /// <summary>A new room's walk boxes (box flags the scripts set don't carry over, as the engine reloads them).</summary>
    internal void LoadBoxes(int room)
    {
        var (boxes, scales, matrix) = room == 0 ? ([], [], null) : _vm.Scripts.RoomBoxes(room);
        _boxes = [.. boxes];
        for (int i = 0; i < scales.Count && i < _scaleSlots.Length; i++) // the room's SCAL (v8: by y only)
            _scaleSlots[i] = (0, scales[i].Y1, scales[i].Scale1, 0, scales[i].Y2, scales[i].Scale2);
        if (matrix != null) _matrix = matrix;
        else BuildMatrix();
        Array.Clear(_extraBoxFlags);
    }

    /// <summary>Every actor out of sight, as the engine does on leaving a room.</summary>
    internal void HideAll()
    {
        for (int i = 1; i < Count; i++) Hide(_actors[i]);
        foreach (var a in _actors) (a.Drawn, a.LastPicturePlace) = (null, null);
        Pictures = [];
        UpperPictures = [];
    }

    /// <summary>The actors in the room just entered come into sight (ScummVM <c>showActors</c>).</summary>
    internal void ShowAll()
    {
        for (int i = 1; i < Count; i++)
            if (IsInCurrentRoom(_actors[i])) Show(_actors[i]);
    }

    /// <summary>ScummVM <c>setBoxFlags</c>: flags with 0xC000 are the engine's own (a direction to face), the rest the box's.</summary>
    public void SetBoxFlags(int box, int value)
    {
        if ((value & 0xC000) != 0)
        {
            if (box < 0 || box >= _extraBoxFlags.Length) throw new ScriptException($"box {box} out of range for extra flags");
            _extraBoxFlags[box] = value;
        }
        else if (box >= 0 && box != Actor.InvalidBox && box < _boxes.Count)
            _boxes[box] = _boxes[box] with { Flags = (uint)value };
    }

    private uint BoxFlags(int box) => box >= 0 && box != Actor.InvalidBox && box < _boxes.Count ? _boxes[box].Flags & 0xFF : 0;

    /// <summary>The z-plane a box puts actors behind (ScummVM <c>getMaskFromBox</c>).</summary>
    public int BoxMask(int box) => box >= 0 && box != Actor.InvalidBox && box < _boxes.Count ? (int)(_boxes[box].Mask & 0xFF) : 0;

    /// <summary>Whether the actor stands in box <paramref name="box"/> (ScummVM <c>checkXYInBoxBounds</c>).</summary>
    public bool IsInBox(Actor a, int box) => IsInBox(box, a.X, a.Y);

    /// <summary>
    /// Whether the room point x,y is in box <paramref name="box"/> (ScummVM <c>checkXYInBoxBounds</c>, kernel-get
    /// 116: the banjo's strings are boxes 1–5 of room 25).
    /// </summary>
    public bool IsInBox(int box, int x, int y)
    {
        if (box < 0 || box == Actor.InvalidBox || box >= _boxes.Count) return false;
        var b = _boxes[box];
        return RoomActors.InBox([b.UpperLeft, b.UpperRight, b.LowerRight, b.LowerLeft], x, y);
    }

    /// <summary>
    /// ScummVM <c>getSpecialBox</c> (kernel-get 115): the last box holding x,y, searched down to box 0; −1 for
    /// none, or once a visible player-only box is met first.
    /// </summary>
    public int BoxAt(int x, int y)
    {
        for (int i = _boxes.Count - 1; i >= 0; i--)
        {
            uint flags = BoxFlags(i);
            if ((flags & 0x80) == 0 && (flags & 0x20) != 0) return -1;
            if (IsInBox(i, x, y)) return i;
        }
        return -1;
    }

    // ── Placing ──

    /// <summary>ScummVM <c>Actor::putActor</c>: moves it, and it comes into sight, stays (kept in the boxes) or goes out of it.</summary>
    public void Put(Actor a, int x, int y, int room)
    {
        a.X = x;
        a.Y = y;
        a.Room = room;
        a.NeedRedraw = true;
        if (_vm.State[EngineVars.Ego] == a.Number) EgoPositioned = true;

        if (a.Visible)
        {
            if (IsInCurrentRoom(a))
            {
                if (a.IsMoving)
                {
                    StopMoving(a);
                    StartAnim(a, a.StandFrame);
                }
                AdjustPosition(a);
            }
            else Hide(a);
        }
        else if (IsInCurrentRoom(a)) Show(a);
    }

    /// <summary>ScummVM <c>adjustActorPos</c>: into the nearest box (unless it ignores them), stopped.</summary>
    private void AdjustPosition(Actor a)
    {
        var (x, y, box) = AdjustXY(a, a.X, a.Y);
        a.X = x;
        a.Y = y;
        a.WalkDestBox = box;
        SetBox(a, box);
        StopMoving(a);
    }

    /// <summary>ScummVM <c>setBox</c>: the actor's box, and its scale from there.</summary>
    private void SetBox(Actor a, int box)
    {
        a.Walkbox = box;
        SetupScale(a);
    }

    /// <summary>
    /// ScummVM <c>setupActorScale</c>: an actor that follows the boxes takes its box's scale where it stands
    /// (a scale slot's changes with y). The engine does it on each box change and each time it draws the actor.
    /// </summary>
    private void SetupScale(Actor a)
    {
        if (a.IgnoreBoxes) return;
        int box = a.Walkbox;
        a.BoxScale = box >= 0 && box < _boxes.Count ? (int)_boxes[box].Scale : 255;
        a.ScaleX = a.ScaleY = ScaleAt(box, a.X, a.Y);
    }

    /// <summary>ScummVM <c>getScale</c> (v8): a box's scale slot at x,y if it has one, else its own scale; 255 for no box.</summary>
    private int ScaleAt(int box, int x, int y)
    {
        if (box < 0 || box >= _boxes.Count) return 255;
        var b = _boxes[box];
        int slot = (int)b.ScaleSlot;
        return slot == 0 ? Math.Clamp((int)b.Scale, 1, 255) : ScaleFromSlot(slot, x, y);
    }

    /// <summary>ScummVM <c>getScaleFromSlot</c>: by y, by x, or the two averaged, kept to 1–255.</summary>
    private int ScaleFromSlot(int slot, int x, int y)
    {
        if (slot < 1 || slot > _scaleSlots.Length) throw new ScriptException($"invalid scale slot {slot}");
        var (X1, Y1, S1, X2, Y2, S2) = _scaleSlots[slot - 1];
        if (Y1 == Y2 && X1 == X2) throw new ScriptException($"invalid scale slot {slot}");
        int scaleY = 0, scale;
        if (Y1 != Y2) scaleY = (S2 - S1) * (Math.Max(0, y) - Y1) / (Y2 - Y1) + S1;
        if (X1 == X2) scale = scaleY;
        else
        {
            int scaleX = (S2 - S1) * (x - X1) / (X2 - X1) + S1;
            scale = Y1 == Y2 ? scaleX : (scaleX + scaleY) / 2;
        }
        return Math.Clamp(scale, 1, 255);
    }

    /// <summary>kernel-set 21 (ScummVM <c>setScaleSlot</c>): a scale slot from two points and their scales.</summary>
    public void SetScaleSlot(int slot, int x1, int y1, int scale1, int x2, int y2, int scale2)
    {
        if (slot < 1 || slot > _scaleSlots.Length) throw new ScriptException($"invalid scale slot {slot}");
        _scaleSlots[slot - 1] = (x1, y1, scale1, x2, y2, scale2);
    }

    /// <summary>kernel-set 20 (ScummVM <c>setBoxScaleSlot</c>): the scale slot a box scales its actors by.</summary>
    public void SetBoxScaleSlot(int box, int slot)
    {
        if (box < 0 || box >= _boxes.Count) throw new ScriptException($"no box {box} to give scale slot {slot}");
        _boxes[box] = _boxes[box] with { ScaleSlot = (uint)slot };
    }

    public void Show(Actor a)
    {
        if (_vm.CurrentRoom == 0 || a.Visible) return;
        AdjustPosition(a);
        if (a.CostumeNeedsInit)
        {
            StartAnim(a, a.InitFrame);
            a.CostumeNeedsInit = false;
        }
        StopMoving(a);
        a.Visible = true;
        a.NeedRedraw = true;
    }

    public void Hide(Actor a)
    {
        if (!a.Visible) return;
        if (a.IsMoving)
        {
            StopMoving(a);
            StartAnim(a, a.StandFrame);
        }
        a.Visible = false;
        a.NeedRedraw = false;
        (a.Drawn, a.LastPicturePlace) = (null, null);
    }

    public void StopMoving(Actor a)
    {
        if (a.WalkScript != 0) _vm.StopScript(a.WalkScript);
        a.Moving = 0;
    }

    // ── Costumes and animations ──

    /// <summary>ScummVM <c>setActorCostume</c> (v7+): a new costume starts from its init animation if the actor's in sight, or when it comes into sight.</summary>
    public void SetCostume(Actor a, int costume)
    {
        a.CostumeNeedsInit = true;
        Array.Clear(a.Vars);
        a.Costume = costume;
        a.ResetCostume();
        if (a.Visible) StartAnim(a, a.InitFrame);
        Array.Fill(a.Palette, 0xFF);
    }

    /// <summary>
    /// ScummVM <c>startAnimActor</c> (v7+): 1001–1005 mean the actor's own init, walk, stand, talk-start and
    /// talk-stop animations; the init animation starts the costume afresh.
    /// </summary>
    public void StartAnim(Actor a, int frame)
    {
        frame = frame switch
        {
            1001 => a.InitFrame,
            1002 => a.WalkFrame,
            1003 => a.StandFrame,
            1004 => a.TalkStartFrame,
            1005 => a.TalkStopFrame,
            _ => frame,
        };
        if (a.Costume == 0) return;
        a.AnimProgress = 0;
        a.NeedRedraw = true;
        if (frame == a.InitFrame) a.ResetCostume();
        DecodeData(a, frame, uint.MaxValue);
        a.Frame = frame;
    }

    /// <summary>ScummVM <c>Actor::animateActor</c> (v7+): <i>cmd</i>×1000 + <i>dir</i>, where 2 stops, 3 faces, 4 turns, and the rest start an animation.</summary>
    public void Animate(Actor a, int anim)
    {
        if (anim == 0xFF) anim = 2000;
        int cmd = anim / 1000, dir = anim % 1000;
        switch (cmd)
        {
            case 2:
                StartAnim(a, a.StandFrame);
                StopMoving(a);
                break;
            case 3:
                a.Moving &= ~Actor.Turning;
                SetDirection(a, dir);
                break;
            case 4:
                TurnTo(a, dir);
                break;
            default:
                StartAnim(a, anim);
                break;
        }
    }

    /// <summary>ScummVM <c>setDirection</c>: faces the nearest of the eight ways at once, every limb re-decoded for it.</summary>
    public void SetDirection(Actor a, int direction)
    {
        if (a.Facing == direction) return;
        a.Facing = NormalizeAngle(direction);
        if (a.Costume == 0) return;
        for (int i = 0; i < Actor.Limbs; i++)
            if (a.LimbFrame[i] != Actor.NoFrame) DecodeData(a, a.LimbFrame[i], 0x8000u >> i);
        a.NeedRedraw = true;
    }

    /// <summary>ScummVM <c>turnToDirection</c> (v7+): starts turning, which happens over the next frames.</summary>
    public static void TurnTo(Actor a, int direction)
    {
        if (direction == -1 || a.IgnoreTurns) return;
        a.Moving &= ~Actor.Turning;
        if (direction != a.Facing)
        {
            a.Moving |= Actor.Turning;
            a.TargetFacing = direction;
        }
    }

    /// <summary>ScummVM <c>faceToObject</c>: turns left or right, towards something.</summary>
    public void FaceTo(Actor a, int obj)
    {
        if (!IsInCurrentRoom(a) || _vm.ObjectOrActorXY(obj) is not { } at) return;
        TurnTo(a, at.X > a.X ? 90 : 270);
    }

    /// <summary>ScummVM <c>normalizeAngle</c>: the nearest multiple of 45°.</summary>
    public static int NormalizeAngle(int angle) => ToSimpleDir8(((angle + 360) % 360 + 360) % 360) * 45;

    private static int ToSimpleDir8(int dir)
    {
        ReadOnlySpan<int> bounds = [22, 72, 107, 157, 202, 252, 287, 337];
        for (int i = 0; i < 7; i++)
            if (dir >= bounds[i] && dir <= bounds[i + 1]) return i + 1;
        return 0;
    }

    /// <summary>
    /// ScummVM <c>AkosCostumeLoader::costumeDecodeData</c>: the chore for animation <paramref name="frame"/>
    /// in the way the actor faces sets up the limbs <paramref name="useMask"/> picks (MSB: limb 0): code 1
    /// clears a limb, 4 stops it, 5 lets it go on, anything else starts it on an AKSQ range.
    /// </summary>
    private void DecodeData(Actor a, int frame, uint useMask)
    {
        if (a.Costume == 0 || _vm.Scripts.Costume(a.Costume) is not { Data: var d }) return;
        int anim = AkosFrames.DirectionOf(d, a.Facing) + frame * d.DirectionCount;
        var ch = d.ChoreData.Span;
        if (anim < 0 || anim >= d.Header.ChoreCount || anim * 2 + 2 > ch.Length) return;
        int r = ch.U16LE(anim * 2);
        if (r == 0 || r + 2 > ch.Length) return;
        ushort mask = ch.U16LE(r);
        r += 2;
        int i = 0;
        do
        {
            if ((mask & 0x8000) != 0)
            {
                if (r >= ch.Length) return;
                byte code = ch[r++];
                if ((useMask & 0x8000) != 0)
                {
                    switch (code)
                    {
                        case 1:
                            a.Active[i] = 0;
                            a.LimbFrame[i] = frame;
                            a.End[i] = a.Start[i] = a.CurPos[i] = 0;
                            break;
                        case 4:
                            a.Stopped |= 1 << i;
                            break;
                        case 5:
                            a.Stopped &= ~(1 << i);
                            break;
                        default:
                            if (r + 4 > ch.Length) return;
                            int start = ch.U16LE(r), len = ch.U16LE(r + 2);
                            r += 4;
                            a.Active[i] = code;
                            a.LimbFrame[i] = frame;
                            a.End[i] = start + len;
                            a.Start[i] = start;
                            a.CurPos[i] = start;
                            break;
                    }
                }
                else if (code is not (1 or 4 or 5)) r += 4;
            }
            i++;
            mask <<= 1;
            useMask <<= 1;
        } while (mask != 0);
    }

    // ── A frame ──

    /// <summary>ScummVM <c>updateActorDirection</c> for v7+: the target direction as the box allows it, no steps in between.</summary>
    private int UpdateDirection(Actor a) => RemapDirection(a, a.TargetFacing) & 1023;

    /// <summary>ScummVM <c>remapDirection</c> (v7+): a box can fix the way actors in it face, or flip it.</summary>
    private int RemapDirection(Actor a, int dir)
    {
        if (!a.IgnoreBoxes)
        {
            int box = a.Walkbox;
            int specdir = box >= 0 && box < _extraBoxFlags.Length ? _extraBoxFlags[box] : 0;
            if (specdir != 0)
            {
                if ((specdir & 0x8000) != 0) dir = specdir & 0x3FFF;
                else
                {
                    specdir &= 0x3FFF;
                    dir = specdir - 90 < dir && dir < specdir + 90 ? specdir : specdir + 180;
                }
            }
            uint flags = BoxFlags(box);
            if ((flags & BoxXFlip) != 0 || _vm.State.Objects.HasClass(a.Number, ClassXFlip)) dir = 360 - dir;
            if ((flags & BoxYFlip) != 0 || _vm.State.Objects.HasClass(a.Number, ClassYFlip)) dir = 180 - dir;
            switch (flags & 7)
            {
                case 1: return dir < 180 ? 90 : 270;
                case 2: return dir > 90 && dir < 270 ? 180 : 0;
                case 3: return 270;
                case 4: return 90;
                case 5: return 0;
                case 6: return 180;
            }
        }
        return NormalizeAngle(dir) | 1024;
    }

    /// <summary>
    /// ScummVM <c>processActors</c> (v8) and the AKOS queue: the actors in the room (layer 0 and up), sorted
    /// back to front by y less 2000 per layer with the engine's own unstable sort, are drawn and then their
    /// costumes step on; the commands the costumes queued run last, newest first.
    /// </summary>
    internal void Process() => DrawAll(animate: true);

    /// <summary>
    /// The pictures made again from the actors as they are, without stepping their costumes on or running the
    /// queue: after a load, whose actors the last frame drawn didn't show.
    /// </summary>
    internal void Redraw() => DrawAll(animate: false);

    private void DrawAll(bool animate)
    {
        var sorted = new List<Actor>();
        for (int i = 1; i < Count; i++)
        {
            var a = _actors[i];
            a.Drawn = null;
            if (a.Layer >= 0 && IsInCurrentRoom(a)) sorted.Add(a);
        }
        for (int j = 0; j < sorted.Count; j++)
            for (int i = 0; i < sorted.Count; i++)
                if (sorted[j].Y - sorted[j].Layer * 2000 < sorted[i].Y - sorted[i].Layer * 2000)
                    (sorted[i], sorted[j]) = (sorted[j], sorted[i]);

        var pictures = new List<ActorPicture>();
        foreach (var a in sorted)
        {
            if (a.Costume == 0) continue;
            if (a.Visible && Draw(a) is { } picture) pictures.Add(picture);
            if (animate) AnimateCostume(a);
        }
        Pictures = pictures;
        if (animate) ProcessQueue();

        // Then, after the blast objects, the actors below layer 0, in actor order, each drawn then animated.
        var upper = new List<ActorPicture>();
        for (int i = 1; i < Count; i++)
        {
            var a = _actors[i];
            if (a.Layer >= 0 || a.Costume == 0 || !IsInCurrentRoom(a)) continue;
            if (a.Visible && Draw(a) is { } picture) upper.Add(picture);
            if (animate) AnimateCostume(a);
        }
        UpperPictures = upper;
    }

    /// <summary>The actor as it looks now, and where that puts it for <c>actor-at</c>.</summary>
    private ActorPicture Draw(Actor a)
    {
        a.NeedRedraw = false;
        if (_vm.Scripts.Costume(a.Costume) is not { } costume) return null;
        SetupScale(a);
        var positions = new List<int>();
        for (int i = 0; i < Actor.Limbs; i++)
            if (a.Active[i] != 0 && (a.Stopped & (1 << i)) == 0) positions.Add(a.CurPos[i]);
        var frame = AkosFrames.ComposeAt(costume.Data, positions);
        if (frame.IsEmpty) return null;

        bool mirrored = AkosFrames.IsMirrored(costume.Data, a.Facing) ^ a.Flip;
        int scale = AkosFrames.DrawnScale(costume.Data, Math.Clamp(a.ScaleX, 1, 255)), y = a.Y - a.Elevation;
        var (l, t, r, b) = frame.Bounds;
        if (mirrored) (l, r) = (-r, -l);
        a.Drawn = (a.X + l * scale / 255, y + t * scale / 255, a.X + r * scale / 255, y + b * scale / 255);

        bool fromBox = a.ForceClip == Actor.BoxClip;
        // A view slides the actor from the last frame's place, but not into another costume: that's a new
        // picture, placed where the scripts put it (room 30's alligator, costume 171, is put 7 px below
        // Guybrush's feet, its own Guybrush drawn to line up with his).
        var (X, Y) = a.LastPicturePlace is var (lx, ly, lc) && lc == a.Costume ? (lx, ly) : (a.X, y);
        a.LastPicturePlace = (a.X, y, a.Costume);
        return new ActorPicture(a.Number, costume, frame, mirrored, scale, a.X, y, fromBox ? BoxMask(a.Walkbox) : a.ForceClip, fromBox, X, Y,
            ActorPicture.RemapOf(a.Palette, costume.Data.Palette.Length), a.ShadowMode);
    }

    /// <summary>
    /// ScummVM <c>Actor::actorHitTest</c>: whether the actor, drawn as it stands now, has a costume pixel at
    /// room point (<paramref name="x"/>, <paramref name="y"/>). Ship combat asks it of each ship for every
    /// cannonball in flight (kernel-get 217).
    /// </summary>
    public bool HitTest(Actor a, int x, int y)
    {
        if (a.Costume == 0 || _vm.Scripts.Costume(a.Costume) is not { } costume) return false;
        SetupScale(a);
        var positions = new List<int>();
        for (int i = 0; i < Actor.Limbs; i++)
            if (a.Active[i] != 0 && (a.Stopped & (1 << i)) == 0) positions.Add(a.CurPos[i]);
        var frame = AkosFrames.ComposeAt(costume.Data, positions);
        bool mirrored = AkosFrames.IsMirrored(costume.Data, a.Facing) ^ a.Flip;
        return AkosFrames.HitTest(costume.Data, frame, Math.Clamp(a.ScaleX, 1, 255), mirrored, x - a.X, y - (a.Y - a.Elevation));
    }

    /// <summary>ScummVM <c>animateCostume</c>: every <i>animation speed</i> frames, each active limb steps on.</summary>
    private void AnimateCostume(Actor a)
    {
        if (a.Costume == 0) return;
        if (++a.AnimProgress < a.AnimSpeed) return;
        a.AnimProgress = 0;
        if (_vm.Scripts.Costume(a.Costume) is not { Data: var d }) return;
        bool changed = false;
        for (int i = 0; i < Actor.Limbs; i++)
            if (a.Active[i] != 0) changed |= IncreaseAnim(a, i, d.Sequence, d.FrameOffsets);
        if (changed) a.NeedRedraw = true;
    }

    /// <summary>
    /// ScummVM <c>Actor::animateLimb</c>: limb <paramref name="limb"/> steps <paramref name="steps"/> times
    /// through its sequence (as <see cref="AnimateCostume"/> steps every limb once), on the frames the
    /// animation speed lets it. The talk scripts start the talk animation and step the mouth limb to the
    /// frame the lip sync picks this way, every frame.
    /// </summary>
    public void AnimateLimb(Actor a, int limb, int steps)
    {
        if (steps == 0) return;
        if (++a.AnimProgress < a.AnimSpeed) return;
        a.AnimProgress = 0;
        if (a.Costume == 0 || limb is < 0 or >= Actor.Limbs) return;
        if (_vm.Scripts.Costume(a.Costume) is not { Data: var d }) return;
        while (steps-- > 0)
            if (a.Active[limb] != 0) IncreaseAnim(a, limb, d.Sequence, d.FrameOffsets);
        a.NeedRedraw = true;
    }

    private void Queue(int cmd, Actor a, int param) => _queue.Push((cmd, a.Number, param));

    private void ProcessQueue()
    {
        while (_queue.TryPop(out var q))
        {
            var a = this[q.Actor];
            switch (q.Cmd)
            {
                case 1: Put(a, 0, 0, 0); break;
                case 4: StartAnim(a, q.Param); break;
                case 5: a.ForceClip = q.Param & 0xFF; break;
                // 3: a sound effect (no sound yet).
            }
        }
    }

    // ── The AKOS sequence engine (ScummVM akos_increaseAnim) ──

    private const int AkcReturn = 0xC001, AkcSetVar = 0xC010, AkcCmdQue3 = 0xC015, AkcC016 = 0xC016, AkcC017 = 0xC017,
        AkcC018 = 0xC018, AkcC019 = 0xC019, AkcComplexChan = 0xC020, AkcC021 = 0xC021, AkcC022 = 0xC022,
        AkcComplexChan2 = 0xC025, AkcJump = 0xC030, AkcJumpIfSet = 0xC031, AkcAddVar = 0xC040, AkcC042 = 0xC042,
        AkcC044 = 0xC044, AkcC045 = 0xC045, AkcC046 = 0xC046, AkcC047 = 0xC047, AkcC048 = 0xC048, AkcIgnore = 0xC050,
        AkcIncVar = 0xC060, AkcCmdQue3Quick = 0xC061, AkcJumpStart = 0xC070, AkcJumpGE = 0xC075, AkcStartAnim = 0xC080,
        AkcStartVarAnim = 0xC081, AkcRandom = 0xC082, AkcSetActorClip = 0xC083, AkcStartAnimInActor = 0xC084,
        AkcSetVarInActor = 0xC085, AkcHideActor = 0xC086, AkcSetDrawOffs = 0xC087, AkcJumpTable = 0xC088,
        AkcSoundStuff = 0xC089, AkcFlip = 0xC08A, AkcCmd3 = 0xC08B, AkcIgnore3 = 0xC08C, AkcIgnore2 = 0xC08D,
        AkcC08E = 0xC08E, AkcSkipStart = 0xC090, AkcSkipGE = 0xC095, AkcClearFlag = 0xC09F, AkcC0A0 = 0xC0A0,
        AkcC0A1 = 0xC0A1, AkcC0A2 = 0xC0A2, AkcC0A3 = 0xC0A3, AkcC0A4 = 0xC0A4, AkcC0A5 = 0xC0A5, AkcEndSeq = 0xC0FF;

    private static int CodeAt(ReadOnlySpan<byte> aksq, int pos)
    {
        if (pos < 0 || pos >= aksq.Length) return AkcEndSeq;
        int code = aksq[pos];
        return (code & 0x80) != 0 && pos + 1 < aksq.Length ? (code << 8) | aksq[pos + 1] : code;
    }

    private static bool Compare(int a, int b, int cmd) => cmd switch
    {
        0 => a == b,
        1 => a != b,
        2 => a < b,
        3 => a <= b,
        4 => a > b,
        _ => a >= b,
    };

    private static int GetVar(Actor a, int var) => var is >= 0 and < Actor.AnimVars ? a.Vars[var] : throw new ScriptException($"{a} animation variable {var} out of range");

    private static void SetVar(Actor a, int var, int value)
    {
        if (var is < 0 or >= Actor.AnimVars) throw new ScriptException($"{a} animation variable {var} out of range");
        a.Vars[var] = value;
    }

    /// <summary>
    /// One step of limb <paramref name="chan"/>: past its current step (how depends on the limb's mode: 2
    /// loops, 3 plays once, 6 and 8 run commands), then through any commands to the next step it shows.
    /// Returns whether it changed.
    /// </summary>
    private bool IncreaseAnim(Actor a, int chan, ReadOnlyMemory<byte> sequence, ushort[] akfo)
    {
        var aksq = sequence.Span;
        int active = a.Active[chan], end = a.End[chan];
        int curpos = a.CurPos[chan], old = curpos;
        bool flag = false, redraw = false;

        int B(int o) => curpos + o < sequence.Length ? sequence.Span[curpos + o] : 0;
        int W(int o) => curpos + o + 1 < sequence.Length ? (short)((ReadOnlySpan<byte>)sequence.Span).U16LE(curpos + o) : 0;
        int UW(int o) => curpos + o + 1 < sequence.Length ? ((ReadOnlySpan<byte>)sequence.Span).U16LE(curpos + o) : 0;

        for (int guard = 0; ; guard++)
        {
            if (guard > 10000) throw new ScriptException($"{a} costume {a.Costume} limb {chan} loops without showing anything");
            int code = CodeAt(aksq, curpos);
            switch (active)
            {
                case 6:
                case 8:
                    switch (code)
                    {
                        case AkcJumpIfSet: case AkcAddVar: case AkcSetVar:
                        case >= AkcSkipStart and <= AkcSkipGE:
                        case AkcC016: case AkcC017: case AkcC018: case AkcC019:
                            curpos += 5; break;
                        case AkcJumpTable: case AkcSetActorClip: case AkcIgnore3: case AkcIgnore2: case AkcIgnore:
                        case AkcStartAnim: case AkcStartVarAnim: case AkcCmdQue3: case AkcC042: case AkcC044: case AkcC0A3:
                            curpos += 3; break;
                        case AkcSoundStuff: curpos += 8; break;
                        case AkcCmd3: case AkcSetVarInActor: case AkcSetDrawOffs: curpos += 6; break;
                        case AkcClearFlag: case AkcHideActor: case AkcIncVar: case AkcCmdQue3Quick: case AkcReturn: case AkcEndSeq:
                            curpos += 2; break;
                        case >= AkcJumpStart and <= AkcJumpGE: case AkcRandom: curpos += 7; break;
                        case AkcFlip: case AkcJump: case AkcStartAnimInActor: case AkcC0A0: case AkcC0A1: case AkcC0A2:
                            curpos += 4; break;
                        case AkcComplexChan2:
                        case AkcComplexChan:
                        {
                            if (code == AkcComplexChan2) curpos += 4;
                            curpos += 3;
                            int n = curpos - 1 < aksq.Length ? aksq[curpos - 1] : 0;
                            while (--n >= 0)
                            {
                                curpos += 4;
                                curpos += curpos < aksq.Length && (aksq[curpos] & 0x80) != 0 ? 2 : 1;
                            }
                            break;
                        }
                        case AkcC021: case AkcC022: case AkcC045: case AkcC046: case AkcC047: case AkcC048:
                            redraw = true;
                            curpos += B(2);
                            break;
                        case AkcC08E: curpos += 4; break;
                        default: curpos += (code & 0x8000) != 0 ? 2 : 1; break;
                    }
                    break;
                case 2:
                    curpos += (code & 0x8000) != 0 ? 2 : 1;
                    if (curpos > end) curpos = a.Start[chan];
                    break;
                case 3:
                    if (curpos != end) curpos += (code & 0x8000) != 0 ? 2 : 1;
                    break;
            }

            code = CodeAt(aksq, curpos);
            if (flag && code != AkcClearFlag) continue;

            switch (code)
            {
                case AkcStartAnimInActor:
                    Queue(4, this[GetVar(a, B(2))], GetVar(a, B(3)));
                    continue;
                case AkcRandom:
                {
                    int min = W(2), max = W(4);
                    SetVar(a, B(6), max < min ? min : min + _random.Next(max - min + 1));
                    continue;
                }
                case >= AkcJumpStart and <= AkcJumpGE:
                    if (Compare(GetVar(a, B(4)), W(5), code - AkcJumpStart)) { curpos = UW(2); goto Done; }
                    continue;
                case AkcIncVar: SetVar(a, 0, GetVar(a, 0) + 1); continue;
                case AkcSetVar: SetVar(a, B(4), W(2)); continue;
                case AkcAddVar: SetVar(a, B(4), GetVar(a, B(4)) + W(2)); continue;
                case AkcFlip: a.Flip = W(2) != 0; continue;
                case AkcCmdQue3: case AkcCmdQue3Quick: case AkcSoundStuff: continue; // sounds: none yet
                case AkcStartAnim: Queue(4, a, B(2)); continue;
                case AkcStartVarAnim: Queue(4, a, GetVar(a, B(2))); continue;
                case AkcSetVarInActor: SetVar(this[GetVar(a, B(2))], B(3), W(4)); continue;
                case AkcHideActor: Queue(1, a, 0); continue;
                case AkcSetActorClip: Queue(5, a, B(2)); continue;
                case AkcSetDrawOffs: continue; // HE only
                case AkcJumpTable:
                {
                    int i = GetVar(a, B(2)) - 1;
                    if (akfo == null || i < 0 || i >= akfo.Length) throw new ScriptException($"{a} costume {a.Costume}: jump table entry {i} out of range");
                    curpos = akfo[i];
                    goto Done;
                }
                case AkcJumpIfSet:
                    if (GetVar(a, B(4)) == 0) continue;
                    SetVar(a, B(4), 0);
                    curpos = UW(2);
                    goto Done;
                case AkcClearFlag: flag = false; continue;
                case AkcJump: curpos = UW(2); goto Done;
                case AkcReturn: case AkcEndSeq: case AkcComplexChan: case AkcC08E: case AkcComplexChan2:
                    goto Done;
                case AkcC021: case AkcC022:
                    redraw = true;
                    goto Done;
                case AkcCmd3: case AkcIgnore: case AkcIgnore3: case AkcIgnore2: continue;
                case >= AkcSkipStart and <= AkcSkipGE:
                    if (!Compare(GetVar(a, B(4)), W(2), code - AkcSkipStart)) flag = true;
                    continue;
                // Sounds never play yet: "while it plays" never holds, "once it stopped" always does.
                case AkcC016: case AkcC018: continue;
                case AkcC017: case AkcC019: curpos = UW(2); goto Done;
                case AkcC042: case AkcC044: case AkcC045: case AkcC046: case AkcC047: case AkcC048: case AkcC0A0: case AkcC0A3: case AkcC0A1:
                    continue; // HE talk and sound
                case AkcC0A2: curpos = UW(2); goto Done;
                case AkcC0A4:
                    if (_vm.State[EngineVars.TalkActor] != 0) { curpos = UW(2); goto Done; }
                    continue;
                case AkcC0A5:
                    if (_vm.State[EngineVars.TalkActor] == 0) { curpos = UW(2); goto Done; }
                    continue;
                default:
                    if ((code & 0xC000) == 0xC000) throw new ScriptException($"{a} costume {a.Costume}: undefined AKOS command {code:X4}");
                    goto Done;
            }
        }
    Done:
        a.CurPos[chan] = curpos;
        return redraw || curpos != old;
    }
}
