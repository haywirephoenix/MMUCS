using System;
using System.Collections.Generic;

namespace MMUCS.Core.Scumm;

/// <summary>Placement data for an object image (OBIM / IMHD) inside a room.</summary>
public sealed class RoomObject
{
    public int Index { get; init; }
    public string Name { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int ImageCount { get; init; }
    public ScummBlock Block { get; init; }

    public string Summary => $"{X},{Y}   {Width}×{Height}   frames {ImageCount}";

    public override string ToString() => string.IsNullOrEmpty(Name) ? $"Object {Index}" : Name;
}

/// <summary>
/// UI-agnostic summary of a room (LFLF/ROOM) used by viewers: header, palettes, objects, walkboxes.
/// </summary>
public sealed class RoomModel
{
    public int RoomNo { get; init; }
    public string Name { get; init; }
    public ScummBlock Lflf { get; init; }
    public ScummBlock Room { get; init; }
    public ScummBlock Rmhd { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int TransparentIndex { get; init; }
    public int PaletteCount { get; init; }
    public IReadOnlyList<RoomObject> Objects { get; init; } = Array.Empty<RoomObject>();
    public IReadOnlyList<Walkbox> Walkboxes { get; init; } = Array.Empty<Walkbox>();

    /// <summary>Resolves a room from any block inside (or equal to) an LFLF.</summary>
    public static bool TryCreate(ScummBlock any, out RoomModel model)
    {
        model = null;
        if (any == null) return false;

        ScummBlock lflf = any.Tag == ScummTag.LFLF ? any : any.FindParent(ScummTag.LFLF);
        ScummBlock room = any.Tag == ScummTag.ROOM ? any : (lflf?.FindChild(ScummTag.ROOM) ?? any.FindParent(ScummTag.ROOM));
        if (room == null) return false;
        lflf ??= room.Parent;

        var rmhd = room.FindChild(ScummTag.RMHD);
        if (rmhd == null) return false;

        var rs = rmhd.DataSpan;
        int width = (int)rs.U32LE(4);
        int height = (int)rs.U32LE(8);
        int transparent = (int)rs.U32LE(20);

        RoomDecoders.TryGetRoomInfo(rmhd, room, out var info);

        var objects = new List<RoomObject>();
        int idx = 0;
        foreach (var child in room.Children)
        {
            if (child.Tag != ScummTag.OBIM) continue;
            var imhd = child.FindChild(ScummTag.IMHD);
            if (imhd == null) { idx++; continue; }

            var s = imhd.DataSpan;
            int len = 0;
            while (len < 32 && len < s.Length && s[len] != 0) len++;

            objects.Add(new RoomObject
            {
                Index = idx++,
                Name = System.Text.Encoding.ASCII.GetString(s.Slice(0, len)),
                ImageCount = (int)s.U32LE(44),
                X = s.S32LE(48),
                Y = s.S32LE(52),
                Width = (int)s.U32LE(56),
                Height = (int)s.U32LE(60),
                Block = child,
            });
        }

        int roomNo = 0;
        string name = null;
        if ((lflf != null && ScummStatics.RoomOffsets.TryGetValue(lflf.Offset, out int no))
            || ScummStatics.RoomOffsets.TryGetValue(room.Offset, out no))
        {
            roomNo = no;
            if (no > 0 && no < ScummStatics.RoomNames.Count) name = ScummStatics.RoomNames[no];
        }

        model = new RoomModel
        {
            RoomNo = roomNo,
            Name = name,
            Lflf = lflf,
            Room = room,
            Rmhd = rmhd,
            Width = width,
            Height = height,
            TransparentIndex = transparent,
            PaletteCount = Math.Max(1, info.TotalPalettes),
            Objects = objects,
            Walkboxes = Scumm.Walkboxes.Parse(room.FindChildRecursive(ScummTag.BOXD)),
        };
        return true;
    }
}
