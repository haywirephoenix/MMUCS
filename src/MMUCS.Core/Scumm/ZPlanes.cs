using System;
using System.Collections.Generic;
using System.Linq;

namespace MMUCS.Core.Scumm;

/// <summary>
/// One z-plane: a 1-bit mask over an image, stored as SCUMM keeps it, one byte per 8-pixel strip per
/// row (the leftmost pixel in the high bit). A set bit hides the pixels of an actor masked by this plane.
/// </summary>
public sealed class ZPlane
{
    public int Strips { get; }
    public int Height { get; }
    /// <summary>Data[row * Strips + strip].</summary>
    public byte[] Data { get; }

    public ZPlane(int strips, int height)
    {
        Strips = strips;
        Height = height;
        Data = new byte[strips * height];
    }

    public int Width => Strips * 8;

    public bool IsSet(int x, int y) =>
        x >= 0 && y >= 0 && y < Height && x < Strips * 8 && (Data[y * Strips + (x >> 3)] & (0x80 >> (x & 7))) != 0;

    public ZPlane Clone()
    {
        var c = new ZPlane(Strips, Height);
        Data.CopyTo(c.Data, 0);
        return c;
    }
}

/// <summary>
/// Decodes the z-planes of COMI's (v8) room and object images and builds the mask the engine draws
/// actors against. Layout, as ScummVM reads it: an image's SMAP holds a ZPLN → WRAP with one ZSTR per
/// plane (ZSTR k is plane k + 1). Each ZSTR → WRAP starts with an OFFS block of one 32-bit offset per
/// strip (from the WRAP's data; 0 for an empty strip) followed by the strips' run-length data.
/// </summary>
public static class ZPlanes
{
    /// <summary>How many z-planes the room has (planes 1..N): the RMHD's z-buffer count.</summary>
    public static int Count(RoomModel room) =>
        room?.Rmhd is { DataLength: >= 20 } rmhd ? (int)Math.Clamp(rmhd.DataSpan.U32LE(16), 0u, 7u) : 0;

    /// <summary>The room background's planes, index 0 = plane 1 (null where the image has no such plane).</summary>
    public static ZPlane[] Room(RoomModel room)
    {
        var smap = room?.Room?.FindChild(ScummTag.IMAG)?.FindChild(ScummTag.WRAP)?.FindChild(ScummTag.SMAP);
        return smap == null || room.Width <= 0 ? Array.Empty<ZPlane>() : Decode(smap, room.Width, room.Height);
    }

    /// <summary>An object image's planes for one of its images (frame), index 0 = plane 1.</summary>
    public static ZPlane[] Object(RoomObject obj, int frame)
    {
        var wrap = obj?.Block?.FindChild(ScummTag.IMAG)?.FindChild(ScummTag.WRAP);
        var smaps = wrap?.Children.Where(c => c.Tag == ScummTag.SMAP).ToList();
        if (smaps == null || smaps.Count == 0 || obj.Width <= 0 || obj.Height <= 0) return Array.Empty<ZPlane>();
        return Decode(smaps[Math.Clamp(frame, 0, smaps.Count - 1)], obj.Width, obj.Height);
    }

    /// <summary>Decodes the planes under one SMAP for an image of the given size.</summary>
    public static ZPlane[] Decode(ScummBlock smap, int width, int height)
    {
        var zstrs = smap?.FindChild(ScummTag.ZPLN)?.FindChild(ScummTag.WRAP)?.Children.Where(c => c.Tag == ScummTag.ZSTR).ToList();
        if (zstrs == null || zstrs.Count == 0) return Array.Empty<ZPlane>();
        int strips = (width + 7) / 8;
        var planes = new ZPlane[zstrs.Count];
        for (int k = 0; k < zstrs.Count; k++)
        {
            var wrap = zstrs[k].FindChild(ScummTag.WRAP);
            if (wrap == null) continue;
            planes[k] = DecodePlane(wrap.DataSpan, strips, height);
        }
        return planes;
    }

    /// <summary>One plane from a ZSTR's WRAP data: OFFS (8-byte header, a u32 per strip), then the strips.</summary>
    internal static ZPlane DecodePlane(ReadOnlySpan<byte> d, int strips, int height)
    {
        var plane = new ZPlane(strips, height);
        for (int strip = 0; strip < strips; strip++)
        {
            int at = 8 + strip * 4;
            if (at + 4 > d.Length) break;
            int offset = (int)d.U32LE(at);
            if (offset <= 0 || offset >= d.Length) continue; // an empty strip
            DecompressStrip(d[offset..], plane.Data, strip, strips, height);
        }
        return plane;
    }

    /// <summary>
    /// ScummVM's decompressMaskImg: a control byte with the high bit set repeats the next byte
    /// (count = low 7 bits), otherwise copies that many bytes; one byte per row, down the strip.
    /// </summary>
    internal static void DecompressStrip(ReadOnlySpan<byte> src, byte[] dst, int strip, int strips, int height)
    {
        int p = 0, row = 0;
        while (row < height && p < src.Length)
        {
            int b = src[p++];
            if ((b & 0x80) != 0)
            {
                b &= 0x7F;
                if (p >= src.Length) break;
                byte c = src[p++];
                do { dst[row * strips + strip] = c; row++; } while (--b > 0 && row < height);
            }
            else
            {
                do
                {
                    if (p >= src.Length) return;
                    dst[row * strips + strip] = src[p++];
                    row++;
                } while (--b > 0 && row < height);
            }
        }
    }

    /// <summary>
    /// The mask as the engine has it once the room is drawn: the background's planes, with each shown
    /// object's planes written over its strips (an empty object strip clears the room's there; a plane
    /// the object doesn't have leaves the room's). Index 0 = plane 1; <paramref name="count"/> planes.
    /// </summary>
    public static ZPlane[] Compose(ZPlane[] room, int count, int width, int height, IEnumerable<(RoomObject Object, ZPlane[] Planes)> objects)
    {
        int strips = (width + 7) / 8;
        var result = new ZPlane[count];
        for (int k = 0; k < count; k++)
            result[k] = k < room.Length && room[k] != null && room[k].Strips == strips ? room[k].Clone() : new ZPlane(strips, height);
        foreach (var (obj, planes) in objects)
        {
            int sx = obj.X >> 3;
            for (int k = 0; k < count && k < planes.Length; k++)
            {
                var src = planes[k];
                if (src == null) continue;
                var dst = result[k];
                for (int row = 0; row < src.Height; row++)
                {
                    int y = obj.Y + row;
                    if (y < 0 || y >= height) continue;
                    for (int s = 0; s < src.Strips; s++)
                    {
                        int x = sx + s;
                        if (x >= 0 && x < strips) dst.Data[y * strips + x] = src.Data[row * src.Strips + s];
                    }
                }
            }
        }
        return result;
    }
}
