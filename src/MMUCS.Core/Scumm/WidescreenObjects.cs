using System;
using System.Threading;

namespace MMUCS.Core.Scumm;

/// <summary>
/// Object images widened for a view wider than the engine's 640 screen. Some objects are only the part the
/// 640 screen shows, so a wider view shows where they stop. Each fix copies the missing part from the costume
/// frame the scene ends on into the image's indices, so it shows and hides with the object's state.
/// </summary>
public static class WidescreenObjects
{
    /// <summary>
    /// What's added to an object's image: the last frame of the costume's animation <c>Anim</c> as an actor at
    /// <c>ActorX,ActorY</c> facing <c>Facing</c> draws it (unmirrored), the part left of <c>ClipRight</c>, under the
    /// object's own image.
    /// </summary>
    private sealed record Fix(int Room, string Object, int Image, int Costume, int Anim, int Facing, int ActorX, int ActorY, int ClipRight);

    private static readonly Fix[] Fixes =
    [
        // Room 22, the caber toss (global 197): Haggis (actor 3 at 788,411, costume 140) throws, and his throw (anim 6)
        // ends on cel 48 with the caber lying across x 236–450. Then he stands (anim 3, cel 43, no caber) and
        // clring-a-haggis-caber goes to state 1. Its image is the caber's end at 432–464: camera-at 752,340 puts the
        // 640 screen's left edge at 432, so the rest is off it. The widened image is the caber from cel 48 left of 432.
        new(22, "clring-a-haggis-caber", 0, 140, 6, 180, 788, 411, 432),
    ];

    /// <summary>
    /// Image <paramref name="image"/> of <paramref name="obj"/> as a wide view shows it, with where it goes in the
    /// room, or the image unchanged at the object's place when it has no fix.
    /// </summary>
    public static (IndexedSurface Surface, int X, int Y) Widen(RoomModel room, RoomObject obj, int image, IndexedSurface surface,
        int transparent, CancellationToken token = default)
    {
        if (obj == null) return (surface, 0, 0);
        int ox = obj.X, oy = obj.Y;
        var none = (surface, ox, oy);
        if (room == null || surface == null) return none;
        foreach (var f in Fixes)
        {
            if (f.Room != room.RoomNo || f.Image != image || !string.Equals(f.Object, obj.Name, StringComparison.Ordinal)) continue;
            if (RoomActors.CostumeBlock(room.Lflf ?? room.Room, f.Costume) is not { } akos) return none;
            var d = AkosParser.Parse(akos);
            if (LastFrame(d, f.Anim, f.Facing) is not { IsEmpty: false } frame) return none;
            foreach (var c in frame.Cels)
                if (AkosFrames.GetCel(d, c.Cel, token) is { } cel)
                    (surface, ox, oy) = Add(surface, ox, oy, transparent, d, cel, f.ActorX + c.X, f.ActorY + c.Y, f.ClipRight);
            return (surface, ox, oy);
        }
        return none;
    }

    /// <summary>The frame an animation stops on: each limb at its last draw step.</summary>
    internal static AkosFrame LastFrame(AkosData d, int anim, int facing)
    {
        int chore = AkosFrames.ChoreIndex(d, anim, AkosFrames.DirectionOf(d, facing));
        if (!AkosFrames.HasChore(d, chore)) return null;
        var frame = new AkosFrame();
        int xmove = 0, ymove = 0;
        foreach (var limb in AkosFrames.GetLimbs(d, chore))
            if (AkosFrames.GetFrameSteps(d, limb) is { Count: > 0 } steps)
                AkosFrames.AddStep(d, steps[^1], frame, ref xmove, ref ymove);
        return frame;
    }

    /// <summary>
    /// The object's image (at <paramref name="ox"/>,<paramref name="oy"/>) with the cel's pixels (at
    /// <paramref name="cx"/>,<paramref name="cy"/>) left of <paramref name="clipRight"/> and outside the image under it,
    /// in the room palette's indices; the costume's shadow colours (1–7) are left out.
    /// </summary>
    internal static (IndexedSurface Surface, int X, int Y) Add(IndexedSurface surface, int ox, int oy, int transparent,
        AkosData d, IndexedSurface cel, int cx, int cy, int clipRight)
    {
        int left = Math.Min(ox, cx), top = Math.Min(oy, cy);
        int right = Math.Max(ox + surface.Width, Math.Min(cx + cel.Width, clipRight));
        int bottom = Math.Max(oy + surface.Height, cy + cel.Height);
        int w = right - left, h = bottom - top;
        var pixels = new byte[w * h];
        Array.Fill(pixels, (byte)transparent);

        var akpl = d.Palette.Span;
        bool passthrough = d.IsPassthroughPalette;
        byte celTransparent = d.TransparentIndex;
        for (int y = 0; y < cel.Height; y++)
            for (int x = 0; x < cel.Width && cx + x < clipRight; x++)
            {
                byte local = cel.Pixels[y * cel.Pitch + x];
                if (local == celTransparent) continue;
                int global = AkosFrames.Global(local, akpl, passthrough);
                if (global is > 0 and < 8) continue;
                pixels[(cy + y - top) * w + cx + x - left] = (byte)global;
            }

        for (int y = 0; y < surface.Height; y++)
            for (int x = 0; x < surface.Width; x++)
            {
                byte v = surface.Pixels[y * surface.Pitch + x];
                if (v != transparent) pixels[(oy + y - top) * w + ox + x - left] = v;
            }

        var widened = new IndexedSurface { Width = w, Height = h, Pitch = w, Pixels = pixels, TransparentIndex = (byte)transparent };
        return (widened, left, top);
    }
}
