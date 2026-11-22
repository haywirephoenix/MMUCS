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
    /// <summary>
    /// Whether its z-planes are ORed onto the room's (the engine's dbAllowMaskOr): COMI treats every strip
    /// as transparent, so such an object only adds masking and its empty strips leave the room's alone.
    /// Otherwise (IMHD flags bit 16, version 801) its planes overwrite the room's, empty strips included.
    /// </summary>
    public bool MaskOr { get; init; } = true;
    public ScummBlock Block { get; init; }

    public string Summary => $"{X},{Y}   {Width}×{Height}   frames {ImageCount}";

    public override string ToString()
    {
        return string.IsNullOrEmpty(Name) ? $"Object {Index}" : Name;
    }
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
    public IReadOnlyList<RoomObject> Objects { get; init; } = [];
    public IReadOnlyList<Walkbox> Walkboxes { get; init; } = [];
    /// <summary>The room's scale slots (SCAL), which walkboxes may scale their actors by.</summary>
    public IReadOnlyList<ScaleSlot> ScaleSlots { get; init; } = [];

    /// <summary>How many images (states) an OBIM's header (IMHD) declares; 0 for a hotspot with none.</summary>
    public static int ImageCountOf(ScummBlock obim)
    {
        return obim?.Tag == ScummTag.OBIM && obim.FindChild(ScummTag.IMHD) is { } imhd ? ImageCountOf(imhd.DataSpan) : 0;
    }

    private static int ImageCountOf(ReadOnlySpan<byte> imhd)
    {
        return imhd.Length >= 48 ? (int)imhd.U32LE(44) : 0;
    }

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
                Name = System.Text.Encoding.ASCII.GetString(s[..len]),
                ImageCount = ImageCountOf(s),
                X = s.S32LE(48),
                Y = s.S32LE(52),
                Width = (int)s.U32LE(56),
                Height = (int)s.U32LE(60),
                // v8 IMHD: version at 40, flags at 68 (ScummVM's resetRoomObject).
                MaskOr = s.Length < 72 || s.U32LE(40) != 801 || (s.U32LE(68) & 16) == 0,
                Block = child,
            });
        }

        int roomNo = 0;
        string name = null;
        // The parser stamps roomNo/roomName onto the LFLF (from LOFF + RNAM) while parsing.
        var meta = lflf?.GetMetaDataDict();
        if (meta != null)
        {
            if (meta.TryGetValue(MetaValue.From(ScummMeta.LFLF.roomNo), out var no)) roomNo = (int)no;
            if (meta.TryGetValue(MetaValue.From(ScummMeta.LFLF.roomName), out var nm)) name = (string)nm;
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
            ScaleSlots = Scumm.Walkboxes.ParseScaleSlots(room.FindChildRecursive(ScummTag.SCAL)),
        };
        return true;
    }

    private Dictionary<RoomObject, int> _drawSlots;

    /// <summary>
    /// The objects in the order the engine draws them, later ones over earlier ones: ScummVM's
    /// drawRoomObjects goes from the last local object slot down to the first, and the slots follow the
    /// room's OBCD order (each OBIM joins its object's slot by id). So the room's first object is drawn
    /// last, on top (hotel-ex's pot over the barbecue animation behind it).
    /// </summary>
    public IEnumerable<RoomObject> InDrawOrder(IEnumerable<RoomObject> objects)
    {
        var slots = _drawSlots ??= DrawSlots();
        return System.Linq.Enumerable.OrderByDescending(objects, o => slots.TryGetValue(o, out int s) ? s : int.MaxValue - o.Index);
    }

    private Dictionary<RoomObject, int> DrawSlots()
    {
        var slots = new Dictionary<RoomObject, int>();
        try
        {
            var root = Lflf ?? Room;
            while (root.Parent != null) root = root.Parent;
            var idOf = new Dictionary<string, int>();
            foreach (var o in RoomDataExport.ReadObjectTable(root)) idOf.TryAdd(o.Name, o.Id);
            var slotOf = new Dictionary<int, int>();
            int slot = 0;
            // In file order (the parser may group them under a container).
            foreach (var obcd in (Lflf ?? Room).FindChildrenRecursive(ScummTag.OBCD))
            {
                var cdhd = obcd.FindChild(ScummTag.CDHD);
                // v8 CDHD: version (u32), then the object id (u16).
                if (cdhd != null && cdhd.DataLength >= 6) slotOf.TryAdd(cdhd.DataSpan[4] | (cdhd.DataSpan[5] << 8), slot);
                slot++;
            }
            foreach (var o in Objects)
                if (o.Name != null && idOf.TryGetValue(o.Name, out int id) && slotOf.TryGetValue(id, out int s)) slots[o] = s;
        }
        catch { /* falls back to the OBIM order */ }
        return slots;
    }
}
