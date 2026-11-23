using System;
using System.Collections.Generic;
using MMUCS.Core.Scumm;

namespace MMUCS.Runtime.Scripts;

/// <summary>
/// The engine's limits from the index's MAXS block (v8: two 50-byte version strings, then u32 counts;
/// ScummVM <c>ScummEngine_v8::readMAXS</c>), and the arrays the index defines before any script runs
/// (AARY). <see cref="Comi"/> holds COMI's values for when no index is at hand.
/// </summary>
public sealed record GameLimits
{
    public int Variables { get; init; }
    public int BitVariables { get; init; }
    public int Scripts { get; init; }
    public int Sounds { get; init; }
    public int Charsets { get; init; }
    public int Costumes { get; init; }
    public int Rooms { get; init; }
    public int GlobalObjects { get; init; }
    public int LocalObjects { get; init; }
    public int NewNames { get; init; }
    public int FlObjects { get; init; }
    public int Inventory { get; init; }
    /// <summary>Array ids run 1 to <see cref="Arrays"/> − 1.</summary>
    public int Arrays { get; init; }
    public int Verbs { get; init; }

    /// <summary>
    /// Arrays defined by the index, in order: the variable that points at each, and its dimensions as
    /// <see cref="VmState.DefineArray"/> takes them.
    /// </summary>
    public IReadOnlyList<(int Variable, int Dim2, int Dim1)> IndexArrays { get; init; } = [];

    /// <summary>
    /// Each global object's starting state, home room and classes, by object number, from the index's
    /// DOBJ (v8: a u32 count, then per object a 40-byte name, state, room and u32 classes; ScummVM
    /// <c>ScummEngine_v8::readGlobalObjects</c>).
    /// </summary>
    public IReadOnlyList<(byte State, byte Room, uint Classes)> IndexObjects { get; init; } = [];

    /// <summary>COMI's MAXS (the values ScummVM's comments give).</summary>
    public static GameLimits Comi { get; } = new()
    {
        Variables = 1500, BitVariables = 2048, Scripts = 458, Sounds = 789, Charsets = 1, Costumes = 446, Rooms = 95,
        GlobalObjects = 1401, LocalObjects = 200, NewNames = 100, FlObjects = 128, Inventory = 80, Arrays = 200, Verbs = 50,
    };

    /// <summary>The limits from the MAXS and AARY blocks under <paramref name="root"/>, or null without a MAXS.</summary>
    public static GameLimits Read(ScummBlock root)
    {
        ScummBlock maxs = null, aary = null, dobj = null;
        foreach (var b in Blocks.Walk(root, 3))
        {
            if (b.Tag == ScummTag.MAXS) maxs ??= b;
            else if (b.Tag == ScummTag.AARY) aary ??= b;
            else if (b.Tag == ScummTag.DOBJ) dobj ??= b;
        }
        if (maxs == null || maxs.DataLength < 100 + 17 * 4) return null;

        var data = maxs.DataMemory;
        int U(int i) => data.Span.S32LE(100 + i * 4);
        var limits = new GameLimits
        {
            Variables = U(0), BitVariables = U(1), /* 2: unused (40) */ Scripts = U(3), Sounds = U(4), Charsets = U(5),
            Costumes = U(6), Rooms = U(7), /* 8: unused (80) */ GlobalObjects = U(9), /* 10: unused (60) */
            LocalObjects = U(11), NewNames = U(12), FlObjects = U(13), Inventory = U(14), Arrays = U(15), Verbs = U(16),
        };
        if (aary != null) limits = limits with { IndexArrays = ReadArrays(aary.DataSpan) };
        if (dobj != null) limits = limits with { IndexObjects = ReadObjects(dobj.DataSpan) };
        return limits;
    }

    private static List<(byte, byte, uint)> ReadObjects(ReadOnlySpan<byte> d)
    {
        var list = new List<(byte, byte, uint)>();
        if (d.Length < 4) return list;
        int n = (int)d.U32LE(0);
        for (int i = 0, p = 4; i < n && p + 46 <= d.Length; i++, p += 46)
            list.Add((d[p + 40], d[p + 41], d.U32LE(p + 42)));
        return list;
    }

    /// <summary>
    /// v8 AARY: (variable, a, b) u32 triples until a 0 variable. A second dimension (b ≠ 0) comes first;
    /// otherwise a is the only one (ScummVM <c>ScummEngine_v8::readArrayFromIndexFile</c>).
    /// </summary>
    private static List<(int, int, int)> ReadArrays(ReadOnlySpan<byte> d)
    {
        var list = new List<(int, int, int)>();
        for (int p = 0; p + 12 <= d.Length; p += 12)
        {
            int variable = d.S32LE(p), a = d.S32LE(p + 4), b = d.S32LE(p + 8);
            if (variable == 0) break;
            list.Add(b != 0 ? (variable, b, a) : (variable, a, b));
        }
        return list;
    }
}
