using System;
using System.Collections.Generic;

namespace MMUCS.Core.Scumm;

public readonly record struct BoxPoint(int X, int Y);

/// <summary>A walkbox quad (corners in SCUMM order: UL, UR, LR, LL).</summary>
public readonly record struct Walkbox(
    int Index,
    BoxPoint UpperLeft,
    BoxPoint UpperRight,
    BoxPoint LowerRight,
    BoxPoint LowerLeft,
    uint Mask,
    uint Flags,
    uint ScaleSlot,
    uint Scale)
{
    /// <summary>SCUMM reserves box 0 (and unused slots) with -32000 sentinel coordinates.</summary>
    public bool IsPlaceholder => UpperLeft.X <= -32000 || UpperLeft.Y <= -32000;
}

/// <summary>A room's scale slot (SCAL): scale1 at y1 to scale2 at y2, interpolated in between.</summary>
public readonly record struct ScaleSlot(int Scale1, int Y1, int Scale2, int Y2);

public static class Walkboxes
{
    /// <summary>Parses a v8 SCAL block: per slot, u32 scale1, y1, scale2, y2 (ScummVM's loadRoom).</summary>
    public static List<ScaleSlot> ParseScaleSlots(ScummBlock scal)
    {
        var result = new List<ScaleSlot>();
        if (scal == null) return result;
        var s = scal.DataSpan;
        for (int o = 0; o + 16 <= s.Length; o += 16)
            result.Add(new ScaleSlot(s.S32LE(o), s.S32LE(o + 4), s.S32LE(o + 8), s.S32LE(o + 12)));
        return result;
    }

    /// <summary>
    /// The scale an actor standing in a box at x,y is drawn with, as ScummVM's getScale (v8): the box's
    /// scale slot interpolated by y when it has one, else the box's own scale. 255 is full size; the
    /// result is clipped to 1-255. A box or slot that doesn't exist gives 255.
    /// </summary>
    public static int ScaleAt(IReadOnlyList<Walkbox> boxes, IReadOnlyList<ScaleSlot> slots, int box, int x, int y)
    {
        if (box < 0 || box >= boxes.Count) return 255;
        var b = boxes[box];
        int slot = (int)b.ScaleSlot;
        if (slot == 0) return Math.Clamp((int)b.Scale, 1, 255);
        if (slots == null || slot > slots.Count) return 255;
        var sl = slots[slot - 1];
        if (sl.Y1 == sl.Y2) return 255; // ScummVM errors on such a slot ("Invalid scale slot")
        int scale = (sl.Scale2 - sl.Scale1) * (Math.Max(0, y) - sl.Y1) / (sl.Y2 - sl.Y1) + sl.Scale1;
        return Math.Clamp(scale, 1, 255);
    }

    /// <summary>
    /// Parses a BOXD block. V8 (COMI) layout: uint32 LE count, then per box 8 x int32 corner
    /// coordinates followed by mask/flags/scaleSlot/scale (+ padding). The stride is derived from the
    /// block length so minor per-version differences in trailing fields don't break parsing.
    /// </summary>
    public static List<Walkbox> Parse(ScummBlock boxd)
    {
        var result = new List<Walkbox>();
        if (boxd == null) return result;

        var s = boxd.DataSpan;
        if (s.Length < 4) return result;

        int count = (int)s.U32LE(0);
        if (count <= 0 || count > 1024) return result;

        int stride = (s.Length - 4) / count;
        if (stride < 32) return result; // not V8-shaped

        for (int i = 0; i < count; i++)
        {
            int o = 4 + i * stride;
            if (o + 32 > s.Length) break;

            result.Add(new Walkbox(
                i,
                new BoxPoint(s.S32LE(o + 0), s.S32LE(o + 4)),
                new BoxPoint(s.S32LE(o + 8), s.S32LE(o + 12)),
                new BoxPoint(s.S32LE(o + 16), s.S32LE(o + 20)),
                new BoxPoint(s.S32LE(o + 24), s.S32LE(o + 28)),
                stride >= 36 ? s.U32LE(o + 32) : 0,
                stride >= 40 ? s.U32LE(o + 36) : 0,
                stride >= 44 ? s.U32LE(o + 40) : 0,
                stride >= 48 ? s.U32LE(o + 44) : 0));
        }

        return result;
    }
}
