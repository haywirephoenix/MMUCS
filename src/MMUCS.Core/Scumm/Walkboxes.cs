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

public static class Walkboxes
{
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
