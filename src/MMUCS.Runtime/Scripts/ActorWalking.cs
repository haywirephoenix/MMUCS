using System;
using System.Collections.Generic;
using MMUCS.Core.Scumm;

namespace MMUCS.Runtime.Scripts;

/// <summary>
/// Walking along the walk boxes (ScummVM actor.cpp's startWalkActor, walkActor, calcMovementFactor and
/// actorWalkStep for v7+, and boxes.cpp's getNextBox, findPathTowards, areBoxesNeighbors and createBoxMatrix).
/// A walk heads for a point kept in the boxes; each frame the actor steps along a leg, a straight line to the
/// edge it shares with the next box on the way (the box matrix says which box that is), and the last leg to
/// the point itself. A step is the walk speed scaled by the actor's scale, so actors far off walk slower.
/// </summary>
public sealed partial class ActorTable
{
    private const uint BoxInvisible = 0x80, BoxPlayerOnly = 0x20;

    /// <summary>The room's box matrix (BOXM): per box, after 0xFF, triples "boxes lo..hi go by box via".</summary>
    private byte[] _matrix = [];

    /// <summary>ScummVM <c>getBoxCoordinates</c> (v8): a box's corners, with COMI's flipped boxes put the right way up.</summary>
    private (BoxPoint Ul, BoxPoint Ur, BoxPoint Lr, BoxPoint Ll) BoxCoords(int box)
    {
        var b = _boxes[box];
        BoxPoint ul = b.UpperLeft, ur = b.UpperRight, lr = b.LowerRight, ll = b.LowerLeft;
        if (ul.Y > ll.Y && ur.Y > lr.Y)
        {
            (ul, ll) = (ll, ul);
            (ur, lr) = (lr, ur);
        }
        if (ul.X > ur.X && ll.X > lr.X)
        {
            (ul, ur) = (ur, ul);
            (ll, lr) = (lr, ll);
        }
        return (ul, ur, lr, ll);
    }

    /// <summary>ScummVM <c>adjustXYToBeInBox</c>: the nearest point in the boxes and its box (<see cref="Actor.InvalidBox"/> for none, or when it ignores them).</summary>
    private (int X, int Y, int Box) AdjustXY(Actor a, int x, int y)
    {
        if (a.IgnoreBoxes || _boxes.Count < 2) return (x, y, Actor.InvalidBox);
        var (ax, ay, box) = RoomActors.PutInBox(_boxes, x, y, _vm.State.Objects.HasClass(a.Number, ClassPlayer));
        return (ax, ay, box == 0 ? Actor.InvalidBox : box); // box 0 is no box in v8: every box was skipped
    }

    /// <summary>
    /// ScummVM <c>getNextBox</c> (<see cref="Walkboxes.NextBox"/> on the room's matrix): the box next to <paramref name="from"/> on the way to <paramref name="to"/>
    /// (it may be <paramref name="to"/> itself); −1 when there's no way.
    /// </summary>
    public int NextBox(int from, int to)
    {
        if (from == to) return to;
        if (to == Actor.InvalidBox) return -1;
        if (from == Actor.InvalidBox) return to;
        if (from < 0 || from >= _boxes.Count || to < 0 || to >= _boxes.Count) throw new ScriptException($"no box {Math.Max(from, to)} for a walk (room has {_boxes.Count})");
        return Walkboxes.NextBox(_matrix, from, to);
    }

    /// <summary>
    /// ScummVM <c>createBoxMatrix</c> (<c>set-box-path</c>): which box leads towards which, worked out from the
    /// boxes that touch (shortest ways first); invisible boxes lead nowhere. COMI's engine then puts each actor
    /// in the room again, into the boxes as they are now.
    /// </summary>
    public void CreateBoxMatrix()
    {
        BuildMatrix();
        for (int i = 1; i < Count; i++)
            if (IsInCurrentRoom(_actors[i])) Put(_actors[i], _actors[i].X, _actors[i].Y, _actors[i].Room);
    }

    private void BuildMatrix()
    {
        int num = _boxes.Count;
        var dist = new int[num, num];
        var via = new int[num, num];
        for (int i = 0; i < num; i++)
            for (int j = 0; j < num; j++)
            {
                if (i == j) (dist[i, j], via[i, j]) = (0, j);
                else if (AreBoxesNeighbors(i, j)) (dist[i, j], via[i, j]) = (1, j);
                else (dist[i, j], via[i, j]) = (255, Actor.InvalidBox);
            }
        // Kleene's algorithm, as ScummVM (bytes: 255 is no way).
        for (int k = 0; k < num; k++)
            for (int i = 0; i < num; i++)
                for (int j = 0; j < num; j++)
                {
                    if (i == j) continue;
                    if (dist[i, j] > dist[i, k] + dist[k, j])
                    {
                        dist[i, j] = (byte)(dist[i, k] + dist[k, j]);
                        via[i, j] = via[i, k];
                    }
                }
        var m = new List<byte>();
        for (int i = 0; i < num; i++)
        {
            m.Add(0xFF);
            for (int j = 0; j < num; j++)
            {
                int it = via[i, j];
                if (it == Actor.InvalidBox) continue;
                m.Add((byte)j);
                while (j < num - 1 && it == via[i, j + 1]) j++;
                m.Add((byte)j);
                m.Add((byte)it);
            }
        }
        m.Add(0xFF);
        _matrix = [.. m];
    }

    /// <summary>ScummVM <c>areBoxesNeighbors</c>: two visible boxes with sides on one line that overlap.</summary>
    private bool AreBoxesNeighbors(int box1, int box2)
    {
        if ((BoxFlags(box1) & BoxInvisible) != 0 || (BoxFlags(box2) & BoxInvisible) != 0) return false;
        var b2 = BoxCoords(box1);
        var b = BoxCoords(box2);
        for (int j = 0; j < 4; j++)
        {
            for (int k = 0; k < 4; k++)
            {
                if (b2.Ur.X == b2.Ul.X && b.Ul.X == b2.Ul.X && b.Ur.X == b2.Ul.X)
                {
                    int b2lo = Math.Min(b2.Ul.Y, b2.Ur.Y), b2hi = Math.Max(b2.Ul.Y, b2.Ur.Y);
                    int blo = Math.Min(b.Ul.Y, b.Ur.Y), bhi = Math.Max(b.Ul.Y, b.Ur.Y);
                    if (!(bhi < b2lo || blo > b2hi || ((blo == b2hi || bhi == b2lo) && b2hi != b2lo && blo != bhi))) return true;
                }
                if (b2.Ur.Y == b2.Ul.Y && b.Ul.Y == b2.Ul.Y && b.Ur.Y == b2.Ul.Y)
                {
                    int b2lo = Math.Min(b2.Ul.X, b2.Ur.X), b2hi = Math.Max(b2.Ul.X, b2.Ur.X);
                    int blo = Math.Min(b.Ul.X, b.Ur.X), bhi = Math.Max(b.Ul.X, b.Ur.X);
                    if (!(bhi < b2lo || blo > b2hi || ((blo == b2hi || bhi == b2lo) && b2hi != b2lo && blo != bhi))) return true;
                }
                b2 = (b2.Ur, b2.Lr, b2.Ll, b2.Ul);
            }
            b = (b.Ur, b.Lr, b.Ll, b.Ul);
        }
        return false;
    }

    /// <summary>
    /// ScummVM <c>findPathTowards</c>: the point on the edge box 1 shares with box 2 to walk to next, on the
    /// way to box 3. True when the actor can walk straight to the destination instead (box 2 is the last box
    /// and the straight line crosses the shared edge).
    /// </summary>
    private bool FindPathTowards(Actor a, int box1nr, int box2nr, int box3nr, out int foundX, out int foundY)
    {
        var box1 = BoxCoords(box1nr);
        var box2 = BoxCoords(box2nr);
        foundX = foundY = 0;
        for (int i = 0; i < 4; i++)
        {
            for (int j = 0; j < 4; j++)
            {
                if (box1.Ul.X == box1.Ur.X && box1.Ul.X == box2.Ul.X && box1.Ul.X == box2.Ur.X)
                {
                    int b1lo = Math.Min(box1.Ul.Y, box1.Ur.Y), b1hi = Math.Max(box1.Ul.Y, box1.Ur.Y);
                    int b2lo = Math.Min(box2.Ul.Y, box2.Ur.Y), b2hi = Math.Max(box2.Ul.Y, box2.Ur.Y);
                    if (!(b1lo > b2hi || b2lo > b1hi || ((b1hi == b2lo || b2hi == b1lo) && b1lo != b1hi && b2lo != b2hi)))
                    {
                        int pos = a.Y;
                        if (box2nr == box3nr)
                        {
                            int diffX = a.WalkDestX - a.X, diffY = a.WalkDestY - a.Y, boxDiffX = box1.Ul.X - a.X;
                            if (diffX != 0)
                            {
                                diffY *= boxDiffX;
                                int t = diffY / diffX;
                                if (t == 0 && (diffY <= 0 || diffX <= 0) && (diffY >= 0 || diffX >= 0)) t = -1;
                                pos = a.Y + t;
                            }
                        }
                        int q = Math.Min(Math.Max(Math.Min(Math.Max(pos, b2lo), b2hi), b1lo), b1hi);
                        if (q == pos && box2nr == box3nr) return true;
                        foundY = q;
                        foundX = box1.Ul.X;
                        return false;
                    }
                }
                if (box1.Ul.Y == box1.Ur.Y && box1.Ul.Y == box2.Ul.Y && box1.Ul.Y == box2.Ur.Y)
                {
                    int b1lo = Math.Min(box1.Ul.X, box1.Ur.X), b1hi = Math.Max(box1.Ul.X, box1.Ur.X);
                    int b2lo = Math.Min(box2.Ul.X, box2.Ur.X), b2hi = Math.Max(box2.Ul.X, box2.Ur.X);
                    if (!(b1lo > b2hi || b2lo > b1hi || ((b1hi == b2lo || b2hi == b1lo) && b1lo != b1hi && b2lo != b2hi)))
                    {
                        int pos = a.X;
                        if (box2nr == box3nr)
                        {
                            int diffX = a.WalkDestX - a.X, diffY = a.WalkDestY - a.Y, boxDiffY = box1.Ul.Y - a.Y;
                            if (diffY != 0) pos += diffX * boxDiffY / diffY;
                        }
                        int q = Math.Min(Math.Max(Math.Min(Math.Max(pos, b2lo), b2hi), b1lo), b1hi);
                        if (q == pos && box2nr == box3nr) return true;
                        foundX = q;
                        foundY = box1.Ul.Y;
                        return false;
                    }
                }
                box1 = (box1.Ur, box1.Lr, box1.Ll, box1.Ul);
            }
            box2 = (box2.Ur, box2.Lr, box2.Ll, box2.Ul);
        }
        return false;
    }

    // ── Walking ──

    /// <summary>
    /// ScummVM <c>startWalkActor</c> (v7+): the actor (in this room) sets off for x,y, kept in the boxes, to face
    /// <paramref name="dir"/> there (−1: as it arrives). Already there, it only turns.
    /// </summary>
    public void StartWalk(Actor a, int x, int y, int dir)
    {
        if (!IsInCurrentRoom(a)) return;
        var (dx, dy, box) = AdjustXY(a, x, y);
        if (a.IgnoreBoxes)
        {
            box = Actor.InvalidBox;
            a.Walkbox = Actor.InvalidBox;
        }
        else if (a.IsMoving && a.WalkDestDir == dir && a.WalkDestX == dx && a.WalkDestY == dy) return;

        if (a.X == dx && a.Y == dy)
        {
            if (dir != a.Facing) TurnTo(a, dir);
            return;
        }
        a.WalkDestX = dx;
        a.WalkDestY = dy;
        a.WalkDestBox = box;
        a.WalkDestDir = dir;
        a.WalkCurBox = a.Walkbox;
        a.Moving = (a.Moving & Actor.InLeg) | Actor.NewLeg;
    }

    /// <summary>ScummVM <c>setActorWalkSpeed</c> (v8): a new speed takes effect at once mid-leg.</summary>
    public void SetWalkSpeed(Actor a, int x, int y)
    {
        if (x == a.SpeedX && y == a.SpeedY) return;
        a.SpeedX = x;
        a.SpeedY = y;
        if (a.IsMoving && (a.Moving & Actor.InLeg) != 0) CalcMovementFactor(a, a.WalkNextX, a.WalkNextY);
    }

    /// <summary>ScummVM <c>walkActors</c>: each actor in the room walks or turns for a frame.</summary>
    internal void Walk()
    {
        for (int i = 1; i < Count; i++)
            if (IsInCurrentRoom(_actors[i])) WalkActor(_actors[i]);
    }

    /// <summary>
    /// ScummVM <c>Actor::walkActor</c> (v7+): a frozen actor only turns; otherwise a step along the leg, or at
    /// the leg's end the next leg (towards the next box on the way, or straight to the destination), or at the
    /// end of the last leg it stops and turns to face the way it was told.
    /// </summary>
    private void WalkActor(Actor a)
    {
        if ((a.Moving & Actor.Frozen) != 0)
        {
            if ((a.Moving & Actor.Turning) != 0)
            {
                int dir = UpdateDirection(a);
                if (a.Facing != dir) SetDirection(a, dir);
                else a.Moving &= ~Actor.Turning;
            }
            return;
        }
        if (a.Moving == 0) return;

        if ((a.Moving & Actor.NewLeg) == 0)
        {
            if ((a.Moving & Actor.InLeg) != 0 && WalkStep(a) != 0) return;
            if ((a.Moving & Actor.LastLeg) != 0)
            {
                a.Moving = 0;
                SetBox(a, a.WalkDestBox);
                StartWalkAnim(a, 3, a.WalkDestDir);
                return;
            }
            if ((a.Moving & Actor.Turning) != 0)
            {
                int dir = UpdateDirection(a);
                if (a.Facing != dir) SetDirection(a, dir);
                else a.Moving &= ~Actor.Turning;
                return;
            }
            SetBox(a, a.WalkCurBox);
            a.Moving &= Actor.InLeg;
        }

        a.Moving &= ~Actor.NewLeg;
        while (true)
        {
            if (a.Walkbox == Actor.InvalidBox)
            {
                SetBox(a, a.WalkDestBox);
                a.WalkCurBox = a.WalkDestBox;
                break;
            }
            if (a.Walkbox == a.WalkDestBox) break;
            int next = NextBox(a.Walkbox, a.WalkDestBox);
            if (next < 0)
            {
                a.WalkDestBox = a.Walkbox;
                a.Moving |= Actor.LastLeg;
                return;
            }
            a.WalkCurBox = next;
            if (FindPathTowards(a, a.Walkbox, next, a.WalkDestBox, out int fx, out int fy)) break;
            if (CalcMovementFactor(a, fx, fy) != 0) return;
            SetBox(a, a.WalkCurBox);
        }
        a.Moving |= Actor.LastLeg;
        CalcMovementFactor(a, a.WalkDestX, a.WalkDestY);
    }

    /// <summary>
    /// ScummVM <c>calcMovementFactor</c> (v7+): a leg towards x,y at the walk speed (y speed first, x capped),
    /// in 16.16 fixed point; the actor turns to face along it. Then the first step.
    /// </summary>
    private int CalcMovementFactor(Actor a, int nextX, int nextY)
    {
        if (a.X == nextX && a.Y == nextY) return 0;
        int diffX = nextX - a.X, diffY = nextY - a.Y;
        int deltaY = a.SpeedY << 16;
        if (diffY < 0) deltaY = -deltaY;
        int deltaX = deltaY * diffX;
        if (diffY != 0) deltaX /= diffY;
        else deltaY = 0;
        if ((uint)Math.Abs(deltaX >> 16) > (uint)a.SpeedX)
        {
            deltaX = a.SpeedX << 16;
            if (diffX < 0) deltaX = -deltaX;
            deltaY = deltaX * diffY;
            if (diffX != 0) deltaY /= diffX;
            else deltaX = 0;
        }
        a.WalkXFrac = a.WalkYFrac = 0;
        a.WalkCurX = a.X;
        a.WalkCurY = a.Y;
        a.WalkNextX = nextX;
        a.WalkNextY = nextY;
        a.WalkDeltaX = deltaX;
        a.WalkDeltaY = deltaY;
        a.TargetFacing = NormalizeAngle((int)(Math.Atan2(deltaX, -deltaY) * 180 / Math.PI));
        return WalkStep(a);
    }

    /// <summary>
    /// ScummVM <c>actorWalkStep</c> (v7+): the walk animation starts (or turns) as needed, the actor enters the
    /// leg's box once it's in it, and moves a step scaled by its size; 0 at the leg's end (the in-leg flag stays,
    /// as v7+ leaves it).
    /// </summary>
    private int WalkStep(Actor a)
    {
        a.NeedRedraw = true;
        int nextFacing = UpdateDirection(a);
        if ((a.Moving & Actor.InLeg) == 0 || a.Facing != nextFacing)
        {
            if (a.WalkFrame != a.Frame || a.Facing != nextFacing) StartWalkAnim(a, 1, nextFacing);
            a.Moving |= Actor.InLeg;
        }
        if (a.Walkbox != a.WalkCurBox && IsInBox(a, a.WalkCurBox)) SetBox(a, a.WalkCurBox);

        int distX = Math.Abs(a.WalkNextX - a.WalkCurX), distY = Math.Abs(a.WalkNextY - a.WalkCurY);
        if (Math.Abs(a.X - a.WalkCurX) >= distX && Math.Abs(a.Y - a.WalkCurY) >= distY) return 0;

        int tmpX = (a.X << 16) + a.WalkXFrac + (a.WalkDeltaX >> 8) * a.ScaleX;
        a.WalkXFrac = (ushort)tmpX;
        a.X = (short)(tmpX >> 16);
        int tmpY = (a.Y << 16) + a.WalkYFrac + (a.WalkDeltaY >> 8) * a.ScaleY;
        a.WalkYFrac = (ushort)tmpY;
        a.Y = (short)(tmpY >> 16);

        if (Math.Abs(a.X - a.WalkCurX) > distX) a.X = a.WalkNextX;
        if (Math.Abs(a.Y - a.WalkCurY) > distY) a.Y = a.WalkNextY;
        return 1;
    }

    /// <summary>
    /// ScummVM <c>startWalkAnim</c>: the actor's walk script gets it (actor, command, direction) if it has one;
    /// otherwise 1 starts walking that way, 2 only faces it, 3 stops (turns to it and stands).
    /// </summary>
    private void StartWalkAnim(Actor a, int cmd, int angle)
    {
        if (angle == -1) angle = a.Facing;
        if (a.WalkScript != 0)
        {
            _vm.RunScript(a.WalkScript, freezeResistant: true, args: [a.Number, cmd, angle]);
            return;
        }
        switch (cmd)
        {
            case 1:
                SetDirection(a, angle);
                StartAnim(a, a.WalkFrame);
                break;
            case 2:
                SetDirection(a, angle);
                break;
            case 3:
                TurnTo(a, angle);
                StartAnim(a, a.StandFrame);
                break;
        }
    }
}
