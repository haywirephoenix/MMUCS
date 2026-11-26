using System;
using System.Collections.Generic;
using System.Linq;
using MMUCS.Core.Scumm;

/// <summary>
/// <c>costume N [ANIM…] [--dir D]</c>: a costume's chores (AKCH) as the engine reads them, and with ANIM,
/// each limb's AKSQ steps in order: the op, and the cels a draw step places (relative to the actor).
/// </summary>
internal static class CostumeCommand
{
    public static int Run(ScummBlock root, List<string> a, Func<List<string>, string, string> takeOption)
    {
        string dirOption = takeOption(a, "--dir");
        if (a.Count < 2 || !int.TryParse(a[1], out int number)) throw new UsageException("costume N [ANIM…] [--dir D]");
        var anims = new List<int>();
        foreach (string s in a.Skip(2))
            anims.Add(int.TryParse(s, out int n) ? n : throw new UsageException($"{s} isn't an animation number"));
        if (RoomActors.CostumeBlock(root, number) is not { } akos) throw new UsageException($"no costume {number}");
        var d = AkosParser.Parse(akos);
        var dirNames = AkosFrames.DirectionNames(d);
        int? onlyDir = dirOption == null ? null
            : int.TryParse(dirOption, out int dn) ? dn
            : dirNames.ToList().IndexOf(dirOption.ToUpperInvariant()) is >= 0 and var di ? di
            : throw new UsageException($"--dir {dirOption}: a direction number or one of {string.Join(" ", dirNames)}");

        // The shadow colours its cels draw with (1–7: a shadow table, set by build-palette-shadow), and how often.
        var shadows = new Dictionary<int, int>();
        for (int cel = 0; cel < d.CelOffsets.Length; cel++)
            if (AkosFrames.GetCel(d, cel) is { } surface)
                foreach (byte v in surface.Pixels)
                    if (v != d.TransparentIndex && AkosFrames.Global(v, d.Palette.Span, d.IsPassthroughPalette) is int g and > 0 and < 8)
                        shadows[g] = shadows.GetValueOrDefault(g) + 1;
        Console.WriteLine($"shadow colours: {(shadows.Count == 0 ? "none" : string.Join(", ", shadows.OrderBy(kv => kv.Key).Select(kv => $"table {kv.Key} ({kv.Value} pixels)")))}");

        int chores = AkosFrames.ChoreCount(d);
        Console.WriteLine($"costume {number}: {chores} chores, {d.DirectionCount} directions ({string.Join(" ", dirNames)}), " +
                          $"{d.CelInfos.Length} cels, AKSQ {d.Sequence.Length} bytes");
        Console.WriteLine($"exported as {MMUCS.Core.Scumm.CostumeCelMods.FolderOf(akos)?.Replace('\\', '/') ?? "(in no room)"}");

        var ch = d.ChoreData.Span;
        int animCount = (chores + d.DirectionCount - 1) / d.DirectionCount;
        foreach (int anim in anims.Count > 0 ? anims : Enumerable.Range(0, animCount))
            for (int dir = 0; dir < d.DirectionCount; dir++)
            {
                if (onlyDir is int od && od != dir) continue;
                int chore = AkosFrames.ChoreIndex(d, anim, dir);
                if (!AkosFrames.HasChore(d, chore)) continue;
                Console.WriteLine($"anim {anim} {dirNames[dir]} (chore {chore}): {LimbCodes(ch, chore)}");
                if (anims.Count == 0) continue;
                foreach (var limb in AkosFrames.GetLimbs(d, chore))
                    PrintSteps(d, limb);
            }
        return 0;
    }

    /// <summary>Every limb an AKCH entry names, with its code: 1 clears, 4 stops, 5 resumes, others start a range.</summary>
    private static string LimbCodes(ReadOnlySpan<byte> ch, int chore)
    {
        int p = ch.U16LE(chore * 2);
        ushort mask = ch.U16LE(p);
        p += 2;
        var parts = new List<string>();
        for (int limb = 0; mask != 0 && p < ch.Length; limb++, mask <<= 1)
        {
            if ((mask & 0x8000) == 0) continue;
            byte code = ch[p++];
            if (code is 1 or 4 or 5)
            {
                parts.Add($"limb {limb} {code switch { 1 => "clear", 4 => "stop", _ => "resume" }}");
                continue;
            }
            parts.Add($"limb {limb} code {code} AKSQ {ch.U16LE(p)}+{ch.U16LE(p + 2)}");
            p += 4;
        }
        return parts.Count == 0 ? "no limbs" : string.Join(", ", parts);
    }

    private static void PrintSteps(AkosData d, AkosLimb limb)
    {
        var seq = d.Sequence.Span;
        // A chore with no length runs on until the sequence's end op (C0FF) or one this listing doesn't know.
        int pos = limb.Start, end = limb.Length == 0 ? seq.Length : Math.Min(limb.End, seq.Length);
        while (pos < end)
        {
            int code = (seq[pos] & 0x80) != 0 && pos + 1 < seq.Length ? (seq[pos] << 8) | seq[pos + 1] : seq[pos];
            int len = AkosFrames.StepLength(seq, pos);
            var frame = new AkosFrame();
            int xm = 0, ym = 0;
            AkosFrames.AddStep(d, pos, frame, ref xm, ref ym);
            string what = frame.IsEmpty
                ? (code & 0xC000) == 0xC000 ? $"op {code:X4} {Hex(seq, pos + 2, Math.Max(0, len - 2))}" : $"cel {code & 0xFFF} (empty)"
                : string.Join(" ", frame.Cels.Select(c => $"cel {c.Cel} {c.X},{c.Y} {c.Width}x{c.Height}"));
            if (!frame.IsEmpty)
            {
                var (l, t, r, b) = frame.Bounds;
                what = $"[{l},{t}–{r},{b}] {what}";
            }
            Console.WriteLine($"  limb {limb.Index} {pos,5}  {what}");
            if (len <= 0) { Console.WriteLine("  (unknown op: stops here)"); break; }
            if (code == 0xC0FF) break;
            pos += len;
        }
    }

    private static string Hex(ReadOnlySpan<byte> s, int at, int n)
    {
        var parts = new List<string>();
        for (int i = 0; i < n && at + i < s.Length; i++) parts.Add(s[at + i].ToString("X2"));
        return string.Join(" ", parts);
    }
}
