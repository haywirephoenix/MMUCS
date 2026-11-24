using System;
using System.Collections.Generic;
using System.Threading;
using MMUCS.Core.Imaging;

namespace MMUCS.Core.Scumm;

/// <summary>One limb (layer) of a chore: the AKSQ range its sequence plays through.</summary>
public readonly record struct AkosLimb(int Index, int Code, int Start, int Length)
{
    public int End => Start + Length;
}

/// <summary>A cel placed relative to the actor's origin (top-left corner, unflipped).</summary>
public readonly record struct AkosPlacedCel(int Cel, int X, int Y, int Width, int Height);

/// <summary>A composed costume frame: the cels of every limb at one point in their sequences.</summary>
public sealed class AkosFrame
{
    public List<AkosPlacedCel> Cels { get; } = [];
    public bool IsEmpty => Cels.Count == 0;

    /// <summary>Bounds relative to the actor origin.</summary>
    public (int Left, int Top, int Right, int Bottom) Bounds
    {
        get
        {
            if (Cels.Count == 0) return default;
            int l = int.MaxValue, t = int.MaxValue, r = int.MinValue, b = int.MinValue;
            foreach (var c in Cels)
            {
                l = Math.Min(l, c.X); t = Math.Min(t, c.Y);
                r = Math.Max(r, c.X + c.Width); b = Math.Max(b, c.Y + c.Height);
            }
            return (l, t, r, b);
        }
    }
}

/// <summary>
/// AKOS chores as the engine plays them (ScummVM akos.cpp): an animation index picks an AKCH entry,
/// which lists the limbs it drives (a 16-bit mask, MSB = limb 0) and an AKSQ range per limb. Each
/// step of a limb's sequence draws one cel, several cels at explicit offsets (DrawMany, used for
/// almost every COMI frame), nothing (EmptyCel), or is a control op. A frame is every limb's
/// current step drawn together.
/// </summary>
public static class AkosFrames
{
    public const int InitFrame = 1, StandFrame = 3;

    private const int OpEmptyCel = 0xC001, OpDrawMany = 0xC020, OpDrawCondA = 0xC021, OpDrawCondB = 0xC022, OpDrawMany2 = 0xC025;

    /// <summary>
    /// The chore of an animation in one of the costume's directions: anim × 4 + dir for four-direction
    /// costumes (0 W, 1 E, 2 S, 3 N), anim × 8 + dir for eight-direction ones (0 N, then clockwise),
    /// as the engine indexes them (ScummVM akosDecodeData).
    /// </summary>
    public static int ChoreIndex(AkosData d, int anim, int dir)
    {
        return anim * d.DirectionCount + dir;
    }

    /// <summary>The costume direction for a facing in degrees (0 north, 90 east, 180 south).</summary>
    public static int DirectionOf(AkosData d, int degrees)
    {
        degrees = ((degrees % 360) + 360) % 360;
        if (d.DirectionCount == 8)
        {
            // ScummVM toSimpleDir: sectors around N, NE, E, SE, S, SW, W, NW.
            ReadOnlySpan<int> bounds = [22, 72, 107, 157, 202, 252, 287, 337];
            for (int i = 1; i < 8; i++)
                if (degrees >= bounds[i - 1] && degrees <= bounds[i]) return i;
            return 0;
        }
        // ScummVM newDirToOldDir.
        if (degrees >= 71 && degrees <= 109) return 1;
        if (degrees >= 109 && degrees <= 251) return 2;
        if (degrees >= 251 && degrees <= 289) return 0;
        return 3;
    }

    /// <summary>
    /// Whether the engine draws the costume mirrored for this facing: ScummVM's AkosRenderer::setFacing
    /// draws "to the right" unless the actor faces west (newDirToOldDir 0), or the costume says it's
    /// never mirrored (AKHD flags bit 0); west-facing frames are the others flipped.
    /// </summary>
    public static bool IsMirrored(AkosData d, int degrees)
    {
        degrees = ((degrees % 360) + 360) % 360;
        return degrees is >= 251 and <= 289 && !d.Header.MirroredCostume;
    }

    /// <summary>Names of the costume's directions, by direction index.</summary>
    public static IReadOnlyList<string> DirectionNames(AkosData d)
    {
        return d.DirectionCount == 8
        ? ["N", "NE", "E", "SE", "S", "SW", "W", "NW"]
        : ["W", "E", "S", "N"];
    }

    public static int ChoreCount(AkosData d)
    {
        return Math.Min(d.Header.ChoreCount, d.ChoreData.Length / 2);
    }

    public static bool HasChore(AkosData d, int chore)
    {
        var ch = d.ChoreData.Span;
        return chore >= 0 && chore < ChoreCount(d) && ch.U16LE(chore * 2) != 0;
    }

    /// <summary>The limbs a chore starts (init/stop/resume entries carry no sequence and are skipped).</summary>
    public static List<AkosLimb> GetLimbs(AkosData d, int chore)
    {
        var list = new List<AkosLimb>();
        var ch = d.ChoreData.Span;
        if (chore < 0 || chore >= ChoreCount(d)) return list;
        int p = ch.U16LE(chore * 2);
        if (p == 0 || p + 2 > ch.Length) return list;

        ushort mask = ch.U16LE(p);
        p += 2;
        for (int limb = 0; mask != 0 && p < ch.Length; limb++, mask <<= 1)
        {
            if ((mask & 0x8000) == 0) continue;
            byte code = ch[p++];
            if (code is 1 or 4 or 5) continue;
            if (p + 4 > ch.Length) break;
            list.Add(new AkosLimb(limb, code, ch.U16LE(p), ch.U16LE(p + 2)));
            p += 4;
        }
        return list;
    }

    /// <summary>
    /// The default chore an actor shows when placed: stand, then init (facing <paramref name="degrees"/>
    /// if it has that direction, else another), then the first that exists.
    /// </summary>
    public static int FindDefaultChore(AkosData d, int degrees = 180)
    {
        int n = d.DirectionCount, facing = DirectionOf(d, degrees);
        foreach (int anim in new[] { StandFrame, InitFrame })
            for (int k = 0; k < n; k++)
            {
                int chore = ChoreIndex(d, anim, (facing + k) % n);
                if (HasChore(d, chore)) return chore;
            }
        for (int i = 0; i < ChoreCount(d); i++)
            if (HasChore(d, i)) return i;
        return -1;
    }

    private static int ReadCode(ReadOnlySpan<byte> seq, int pos)
    {
        return (seq[pos] & 0x80) != 0 && pos + 1 < seq.Length ? (seq[pos] << 8) | seq[pos + 1] : seq[pos];
    }

    private static bool IsDrawStep(int code)
    {
        return (code & 0xC000) != 0xC000 || code is OpEmptyCel or OpDrawMany;
    }

    /// <summary>Byte length of the step at <paramref name="pos"/>, or 0 if unknown.</summary>
    public static int StepLength(ReadOnlySpan<byte> seq, int pos)
    {
        int code = ReadCode(seq, pos);
        if ((code & 0xC000) != 0xC000) return (code & 0x8000) != 0 ? 2 : 1;
        if (code == OpDrawMany)
        {
            if (pos + 2 >= seq.Length) return 0;
            int n = seq[pos + 2], r = pos + 3;
            for (int i = 0; i < n && r + 4 < seq.Length; i++) r += (seq[r + 4] & 0x80) != 0 ? 6 : 5;
            return r - pos;
        }
        return code switch
        {
            0xC001 or 0xC086 or 0xC09F or 0xC0FF => 2,
            0xC015 or 0xC016 or 0xC017 or 0xC018 or 0xC019 or 0xC050 or 0xC060 or 0xC061
                or 0xC080 or 0xC081 or 0xC083 or 0xC08A or 0xC08D or 0xC08E => 3,
            0xC030 or 0xC084 or 0xC089 or 0xC08B or 0xC08C => 4,
            0xC010 or 0xC031 or 0xC040 or 0xC042 or 0xC044 => 5,
            0xC085 or 0xC087 or >= 0xC090 and <= 0xC095 => 6,
            >= 0xC070 and <= 0xC075 or 0xC082 => 7,
            _ => 0,
        };
    }

    /// <summary>AKSQ positions of a limb's frames (draw steps) in order; control ops are skipped.</summary>
    public static List<int> GetFrameSteps(AkosData d, AkosLimb limb)
    {
        var steps = new List<int>();
        var seq = d.Sequence.Span;
        int pos = limb.Start, end = Math.Min(limb.End, seq.Length);
        if (limb.Length == 0) end = seq.Length; // no length: read until the first unknown op / end marker
        while (pos < end && steps.Count < 4096)
        {
            int code = ReadCode(seq, pos);
            if (IsDrawStep(code)) steps.Add(pos);
            int len = StepLength(seq, pos);
            if (len <= 0 || code == 0xC0FF) break;
            if (limb.Length == 0 && code == OpEmptyCel && steps.Count > 0) break;
            pos += len;
        }
        return steps;
    }

    /// <summary>Adds the cels drawn by the step at <paramref name="pos"/>.</summary>
    public static void AddStep(AkosData d, int pos, AkosFrame frame, ref int xmove, ref int ymove)
    {
        var seq = d.Sequence.Span;
        if (pos < 0 || pos >= seq.Length) return;
        int code = ReadCode(seq, pos);
        if (code == OpEmptyCel) return;

        if (code == OpDrawMany)
        {
            int n = seq[pos + 2], r = pos + 3;
            for (int i = 0; i < n && r + 4 < seq.Length; i++)
            {
                int x = (short)seq.U16LE(r), y = (short)seq.U16LE(r + 2);
                int celCode = ReadCode(seq, r + 4);
                r += (seq[r + 4] & 0x80) != 0 ? 6 : 5;
                AddCel(d, celCode & 0xFFF, xmove + x, ymove + y, frame);
            }
            return;
        }

        if ((code & 0xC000) == 0xC000) return; // control op
        int cel = code & 0xFFF;
        if (cel >= d.CelInfos.Length) return;
        var ci = d.CelInfos[cel];
        AddCel(d, cel, xmove + ci.RelX, ymove + ci.RelY, frame);
        xmove += ci.MoveX;
        ymove -= ci.MoveY;
    }

    private static void AddCel(AkosData d, int cel, int x, int y, AkosFrame frame)
    {
        if (cel < 0 || cel >= d.CelInfos.Length) return;
        var ci = d.CelInfos[cel];
        if (ci.Width == 0 || ci.Height == 0) return;
        frame.Cels.Add(new AkosPlacedCel(cel, x, y, ci.Width, ci.Height));
    }

    /// <summary>Composes a chore's frame with each limb at step <paramref name="step"/> (wrapping per limb).</summary>
    public static AkosFrame Compose(AkosData d, int chore, int step = 0)
    {
        var frame = new AkosFrame();
        int xmove = 0, ymove = 0;
        foreach (var limb in GetLimbs(d, chore))
        {
            var steps = GetFrameSteps(d, limb);
            if (steps.Count == 0) continue;
            AddStep(d, steps[step % steps.Count], frame, ref xmove, ref ymove);
        }
        return frame;
    }

    /// <summary>
    /// A chore's frame as the engine shows it on an actor: setting a costume plays its init animation
    /// (ScummVM setActorCostume → startAnimActor(initFrame)), which starts every limb it lists; a later
    /// chore restarts only its own limbs, stops those it gives code 1, and leaves the rest on init's.
    /// Wally's stand animation in cannon drives only his body: his head stays init's.
    /// </summary>
    public static AkosFrame ComposeAfterInit(AkosData d, int chore, int step = 0)
    {
        int init = ChoreIndex(d, InitFrame, d.DirectionCount > 0 ? chore % d.DirectionCount : 0);
        if (chore / Math.Max(1, d.DirectionCount) == InitFrame || !HasChore(d, init)) return Compose(d, chore, step);
        var limbs = new SortedDictionary<int, AkosLimb>();
        foreach (int c in new[] { init, chore })
        {
            var ch = d.ChoreData.Span;
            int p = ch.U16LE(c * 2);
            if (p == 0 || p + 2 > ch.Length) continue;
            ushort mask = ch.U16LE(p);
            p += 2;
            for (int limb = 0; mask != 0 && p < ch.Length; limb++, mask <<= 1)
            {
                if ((mask & 0x8000) == 0) continue;
                byte code = ch[p++];
                if (code == 1) { limbs.Remove(limb); continue; }
                if (code is 4 or 5) continue; // stop / resume: the limb keeps its sequence
                if (p + 4 > ch.Length) break;
                limbs[limb] = new AkosLimb(limb, code, ch.U16LE(p), ch.U16LE(p + 2));
                p += 4;
            }
        }
        var frame = new AkosFrame();
        int xmove = 0, ymove = 0;
        foreach (var limb in limbs.Values)
        {
            var steps = GetFrameSteps(d, limb);
            if (steps.Count > 0) AddStep(d, steps[step % steps.Count], frame, ref xmove, ref ymove);
        }
        return frame;
    }

    /// <summary>
    /// The frame an actor shows with its limbs at these AKSQ positions, in limb order (ScummVM
    /// <c>AkosRenderer::drawLimb</c> for each active limb that isn't stopped): a cel, a list of cels
    /// (0xC020, 0xC025 with its extra offset, the conditional 0xC021/0xC022 drawn like their plain
    /// forms), or nothing for a control step.
    /// </summary>
    public static AkosFrame ComposeAt(AkosData d, IEnumerable<int> positions)
    {
        var frame = new AkosFrame();
        var seq = d.Sequence.Span;
        int xmove = 0, ymove = 0;
        foreach (int start in positions)
        {
            int pos = start;
            if (pos < 0 || pos + 1 >= seq.Length) continue;
            int code = ReadCode(seq, pos);
            if (code is OpDrawCondA or OpDrawCondB)
            {
                if (pos + 3 >= seq.Length) continue;
                pos += seq[pos + 3] + 2;
                code = code == OpDrawCondA ? OpDrawMany : OpDrawMany2;
            }
            if (code == OpDrawMany2) pos += 4;
            if (code is OpDrawMany or OpDrawMany2)
            {
                if (pos + 2 >= seq.Length) continue;
                int n = seq[pos + 2], r = pos + 3;
                for (int i = 0; i < n && r + 4 < seq.Length; i++)
                {
                    int x = (short)seq.U16LE(r), y = (short)seq.U16LE(r + 2);
                    int celCode = ReadCode(seq, r + 4);
                    r += (seq[r + 4] & 0x80) != 0 ? 6 : 5;
                    AddCel(d, celCode & 0xFFF, xmove + x, ymove + y, frame);
                }
                continue;
            }
            AddStep(d, pos, frame, ref xmove, ref ymove);
        }
        return frame;
    }

    /// <summary>Number of steps before every limb of a chore is back at its start (capped).</summary>
    public static int CycleLength(AkosData d, int chore)
    {
        int lcm = 1;
        foreach (var limb in GetLimbs(d, chore))
        {
            int n = GetFrameSteps(d, limb).Count;
            if (n <= 0) continue;
            lcm = Lcm(lcm, n);
            if (lcm > 512) return 512;
        }
        return lcm;
    }

    private static int Lcm(int a, int b)
    {
        int x = a, y = b;
        while (y != 0) (x, y) = (y, x % y);
        return a / x * b;
    }

    /// <summary>
    /// The palette index a costume colour is drawn in (ScummVM <c>AkosRenderer::setPalette</c>): the actor's own
    /// colour for it when <paramref name="remap"/> has one (not 0xFF), else the costume's (AKPL), or the colour
    /// itself in a costume that uses the palette as it is.
    /// </summary>
    public static int Global(byte local, ReadOnlySpan<byte> akpl, bool passthrough, int[] remap = null) =>
        remap != null && local < remap.Length && remap[local] != 0xFF ? remap[local] & 0xFF
        : passthrough ? local : local < akpl.Length ? akpl[local] : local;

    /// <summary>Writes palette index <paramref name="global"/>'s colour, from the screen's colours when given, else the costume's resolved ones.</summary>
    private static void Paint(byte[] rgba, int o, int global, ScummColor[] colors, byte[] screen)
    {
        if (screen != null && global * 3 + 2 < screen.Length)
        {
            rgba[o] = screen[global * 3];
            rgba[o + 1] = screen[global * 3 + 1];
            rgba[o + 2] = screen[global * 3 + 2];
        }
        else
        {
            var col = global < colors.Length ? colors[global] : new ScummColor(1, 0, 1, 1);
            rgba[o] = (byte)(col.R * 255);
            rgba[o + 1] = (byte)(col.G * 255);
            rgba[o + 2] = (byte)(col.B * 255);
        }
        rgba[o + 3] = 255;
    }

    /// <summary>
    /// A shadow pixel (colours 1–7): without <paramref name="shadow"/>, translucent black where nothing is
    /// drawn yet; in shadow mode 3, a pixel of the shadow layer; in the other modes it isn't one (false), and
    /// is drawn in its own colour as the engine does.
    /// </summary>
    private static bool Shade(byte[] rgba, int o, int global, AkosShadow shadow)
    {
        if (shadow == null)
        {
            if (rgba[o + 3] == 0) { rgba[o] = rgba[o + 1] = rgba[o + 2] = 0; rgba[o + 3] = 102; }
            return true;
        }
        if (shadow.Mode != 3) return false;
        shadow.Shade(o, global);
        return true;
    }

    /// <summary>
    /// Renders a frame with the costume's resolved colours. Shadow indices (1–7) become translucent
    /// black, or with <paramref name="shadow"/> go as the actor's shadow mode says. <paramref name="originX"/>/<paramref name="originY"/> give the actor origin within the image.
    /// </summary>
    /// <param name="mirror">Flip it about the actor's origin (<see cref="IsMirrored"/>).</param>
    /// <param name="screen">The colours on the screen (256 RGB triples) to draw in instead of the costume's resolved ones, as the engine does.</param>
    /// <param name="remap">The actor's own colours (<see cref="Global"/>), or null.</param>
    public static RgbaImage Render(AkosData d, AkosFrame frame, out int originX, out int originY, bool mirror = false, CancellationToken token = default,
        byte[] screen = null, int[] remap = null, AkosShadow shadow = null)
    {
        originX = originY = 0;
        shadow?.Reset();
        if (frame == null || frame.IsEmpty) return null;
        var (l, t, r, b) = frame.Bounds;
        int w = r - l, h = b - t;
        if (w <= 0 || h <= 0 || w > 4096 || h > 4096) return null;
        originX = -l;
        originY = -t;

        var rgba = new byte[w * h * 4];
        shadow?.Start(w, h);
        var akpl = d.Palette.Span;
        var colors = d.ResolvedColors;
        bool passthrough = d.IsPassthroughPalette;
        byte transparent = d.TransparentIndex;

        foreach (var c in frame.Cels)
        {
            var surface = GetCel(d, c.Cel, token);
            if (surface == null) continue;
            int x0 = c.X - l, y0 = c.Y - t;
            for (int y = 0; y < surface.Height; y++)
            {
                int dy = y0 + y;
                if (dy < 0 || dy >= h) continue;
                for (int x = 0; x < surface.Width; x++)
                {
                    int dx = x0 + x;
                    if (dx < 0 || dx >= w) continue;
                    byte local = surface.Pixels[y * surface.Pitch + x];
                    if (local == transparent) continue;
                    int global = Global(local, akpl, passthrough, remap);
                    int o = (dy * w + dx) * 4;
                    if (global > 0 && global < 8 && Shade(rgba, o, global, shadow)) continue;
                    Paint(rgba, o, global, colors, screen);
                    shadow?.Clear(o);
                }
            }
        }
        if (mirror)
        {
            for (int y = 0; y < h; y++)
                for (int i = y * w, j = i + w - 1; i < j; i++, j--)
                    for (int k = 0; k < 4; k++)
                        (rgba[i * 4 + k], rgba[j * 4 + k]) = (rgba[j * 4 + k], rgba[i * 4 + k]);
            originX = w - originX;
            shadow?.Mirror();
        }
        return new RgbaImage(w, h, rgba);
    }

    /// <summary>
    /// ScummVM's bigCostumeScaleTable (akos.cpp): the 8-bit bit-reversal permutation. A cel's column or
    /// row at table index i is kept at scale s when entry i &lt; s; the table repeats every 256.
    /// </summary>
    private static readonly byte[] ScaleTable = BuildScaleTable();

    private static byte[] BuildScaleTable()
    {
        var t = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            int r = 0;
            for (int b = 0; b < 8; b++) if ((i & (1 << b)) != 0) r |= 0x80 >> b;
            t[i] = (byte)r;
        }
        return t;
    }

    private static bool Kept(int index, int scale) => ScaleTable[index & 0xFF] < scale;

    /// <summary>
    /// Where the engine draws each of a cel's columns at <paramref name="scale"/>, relative to the actor's
    /// origin (ScummVM BaseCostumeRenderer::paintCelByleRLECommon and byleRLEDecode, AKOS's 384-entry
    /// table). The walk starts at index 384 − |offset|, so it isn't the same as scaling about the origin:
    /// cels at different offsets drop different columns, which is how cannon's three gunner pieces,
    /// two columns apart in the art, meet without a gap. A column that doesn't advance is overdrawn
    /// by the next one.
    /// </summary>
    private static int[] ScaledColumns(int offset, int width, int scale, bool mirror)
    {
        const int Size = 384;
        var cols = new int[width];
        int step = -1, move = offset, x = 0;
        if (move < 0) { move = -move; step = 1; }
        if (!mirror)
        {
            // _drawActorToRight
            int j = Size - move, start = j;
            for (int i = 0; i < move; i++) if (Kept(j++, scale)) x -= step;
            for (int c = 0, idx = start; c < width; c++, idx++)
            {
                cols[c] = x;
                if (Kept(idx, scale)) x++;
            }
        }
        else
        {
            int j = Size + move, start = j;
            for (int i = 0; i < move; i++) if (Kept(j--, scale)) x += step;
            for (int c = 0, idx = start; c < width; c++, idx--)
            {
                cols[c] = x;
                if (Kept(idx, scale)) x--;
            }
        }
        return cols;
    }

    /// <summary>Where the engine draws each of a cel's rows at <paramref name="scale"/>, or int.MinValue for a dropped row.</summary>
    private static int[] ScaledRows(int offset, int height, int scale)
    {
        const int Size = 384;
        var rows = new int[height];
        int step = -1, move = offset, y = 0;
        if (move < 0) { move = -move; step = 1; }
        int j = Size - move, start = j;
        for (int i = 0; i < move; i++) if (Kept(j++, scale)) y -= step;
        for (int r = 0, idx = start; r < height; r++, idx++)
            rows[r] = Kept(idx, scale) ? y++ : int.MinValue;
        return rows;
    }

    /// <summary>
    /// Renders a frame at an actor's scale (255 is full size) the way the engine draws it: each cel scaled
    /// on its own with SCUMM's scale table (<see cref="ScaledColumns"/>), so the result is drawn unscaled.
    /// At 255 this is <see cref="Render"/>.
    /// </summary>
    public static RgbaImage RenderScaled(AkosData d, AkosFrame frame, int scale, out int originX, out int originY, bool mirror = false, CancellationToken token = default,
        byte[] screen = null, int[] remap = null, AkosShadow shadow = null)
    {
        if (scale >= 255) return Render(d, frame, out originX, out originY, mirror, token, screen, remap, shadow);
        originX = originY = 0;
        shadow?.Reset();
        if (frame == null || frame.IsEmpty) return null;
        scale = Math.Max(scale, 1);

        var placed = new List<(IndexedSurface Cel, int[] Cols, int[] Rows)>(frame.Cels.Count);
        int l = int.MaxValue, t = int.MaxValue, r = int.MinValue, b = int.MinValue;
        foreach (var c in frame.Cels)
        {
            var surface = GetCel(d, c.Cel, token);
            if (surface == null) continue;
            var cols = ScaledColumns(c.X, surface.Width, scale, mirror);
            var rows = ScaledRows(c.Y, surface.Height, scale);
            foreach (int x in cols) { l = Math.Min(l, x); r = Math.Max(r, x + 1); }
            foreach (int y in rows) if (y != int.MinValue) { t = Math.Min(t, y); b = Math.Max(b, y + 1); }
            placed.Add((surface, cols, rows));
        }
        int w = r - l, h = b - t;
        if (l == int.MaxValue || t == int.MaxValue || w <= 0 || h <= 0 || w > 4096 || h > 4096) return null;
        originX = -l;
        originY = -t;

        var rgba = new byte[w * h * 4];
        shadow?.Start(w, h);
        var akpl = d.Palette.Span;
        var colors = d.ResolvedColors;
        bool passthrough = d.IsPassthroughPalette;
        byte transparent = d.TransparentIndex;
        foreach (var (surface, cols, rows) in placed)
            for (int x = 0; x < surface.Width; x++)
            {
                int dx = cols[x] - l;
                for (int y = 0; y < surface.Height; y++)
                {
                    if (rows[y] == int.MinValue) continue;
                    byte local = surface.Pixels[y * surface.Pitch + x];
                    if (local == transparent) continue;
                    int global = Global(local, akpl, passthrough, remap);
                    int o = ((rows[y] - t) * w + dx) * 4;
                    if (global > 0 && global < 8 && Shade(rgba, o, global, shadow)) continue;
                    Paint(rgba, o, global, colors, screen);
                    shadow?.Clear(o);
                }
            }
        return new RgbaImage(w, h, rgba);
    }

    /// <summary>Union of a chore's frame bounds over <paramref name="steps"/> steps (relative to the actor origin).</summary>
    public static (int Left, int Top, int Right, int Bottom) CycleBounds(AkosData d, int chore, int steps)
    {
        int l = int.MaxValue, t = int.MaxValue, r = int.MinValue, b = int.MinValue;
        for (int i = 0; i < Math.Max(1, steps); i++)
        {
            var f = Compose(d, chore, i);
            if (f.IsEmpty) continue;
            var (Left, Top, Right, Bottom) = f.Bounds;
            l = Math.Min(l, Left); t = Math.Min(t, Top); r = Math.Max(r, Right); b = Math.Max(b, Bottom);
        }
        return l == int.MaxValue ? default : (l, t, r, b);
    }

    /// <summary>
    /// Composes a frame into one surface of the costume's own (local) indices, so it renders like a
    /// single cel. <paramref name="bounds"/> fixes the canvas (e.g. a whole animation's extent);
    /// by default it is the frame's own bounds.
    /// </summary>
    public static IndexedSurface ComposeSurface(AkosData d, AkosFrame frame,
        (int Left, int Top, int Right, int Bottom)? bounds = null, CancellationToken token = default)
    {
        if (frame == null) return null;
        var (l, t, r, b) = bounds ?? frame.Bounds;
        int w = r - l, h = b - t;
        if (w <= 0 || h <= 0 || w > 4096 || h > 4096) return null;
        byte transparent = d.TransparentIndex;
        var pixels = new byte[w * h];
        if (transparent != 0) Array.Fill(pixels, transparent);

        foreach (var c in frame.Cels)
        {
            var cel = GetCel(d, c.Cel, token);
            if (cel == null) continue;
            int x0 = c.X - l, y0 = c.Y - t;
            for (int y = 0; y < cel.Height; y++)
            {
                int dy = y0 + y;
                if (dy < 0 || dy >= h) continue;
                for (int x = 0; x < cel.Width; x++)
                {
                    int dx = x0 + x;
                    if (dx < 0 || dx >= w) continue;
                    byte v = cel.Pixels[y * cel.Pitch + x];
                    if (v != transparent) pixels[dy * w + dx] = v;
                }
            }
        }
        return new IndexedSurface { Width = w, Height = h, Pitch = w, Pixels = pixels, TransparentIndex = transparent };
    }

    /// <summary>Decodes a cel, sharing the costume's decoded-cel cache.</summary>
    public static IndexedSurface GetCel(AkosData d, int cel, CancellationToken token = default)
    {
        if (cel < 0 || cel >= d.CelOffsets.Length) return null;
        long key = AkosCelCache.BuildCelCacheKey(d, cel);
        lock (d.DecodedCels)
            if (d.DecodedCels.TryGetValue(key, out var cached)) return cached;
        var surface = AkosDecoders.DecodeCel(d, cel, token);
        if (surface != null)
            lock (d.DecodedCels) d.DecodedCels[key] = surface;
        return surface;
    }
}
