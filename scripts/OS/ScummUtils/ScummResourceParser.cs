using Godot;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;


public class ScummResourceParser
{
    // COMI uses XOR obfuscation with key 0x69
    private const byte XorKey = 0x69;

    // Max bytes to keep as RawData per block (avoid huge memory use for AKCD etc)
    private const int MaxRawDataBytes = 256 * 1024;

    public event Action<int, int> ProgressChanged; // current, total bytes

    public ScummResourceParser()
    {
        ScummStatics.RoomOffsets.Clear();
        ScummStatics.RoomNames.Clear();
        ScummStatics.RoomOffsets.Clear();
    }
    
    public ScummBlock Parse(string filePath)
    {
        // _rnamBlock = null;
        byte[] raw = File.ReadAllBytes(filePath);

        // COMI .la0 files are XOR obfuscated
        if (_IsObfuscated(raw))
            _XorDecrypt(raw);

        var root = new ScummBlock
        {
            Tag = 0,
            TagName = Path.GetFileName(filePath),
            FullName = "Scumm Data File",
            Offset = 0,
            Size = raw.Length,
            DataOffset = 0,
            DataLength = raw.Length,
            FileData = raw,
        };

        _ParseChildren(raw, 0, raw.Length, root);

        _DecodeMetadata(root);

        return root;
    }

    private void _ParseChildren(byte[] data, int start, int end, ScummBlock parent, bool silent = false)
    {
        ReadOnlySpan<byte> span = data;
        int pos = start;

        while (pos + 8 <= end)
        {
            uint tag = ScummTag.Read(data, pos);

            if (!ScummTag.TryGetMeta(tag, out _) && tag != ScummTag.WRAP)
            {
                pos++; // Scan for alignment
                continue;
            }

            // COMI sizes are Big Endian
            int size = BinaryPrimitives.ReadInt32BigEndian(span.Slice(pos + 4, 4));

            if (size < 8 || (long)pos + size > end) break;

            var block = new ScummBlock
            {
                Tag = tag,
                Offset = pos,
                Size = size,
                DataOffset = pos + 8,
                DataLength = size - 8,
                FileData = data,
                Parent = parent,
            };
            
            if (ScummTag.TryGetMeta(tag, out var meta))
            {
                block.TagName = meta.TagName;
                block.FullName = meta.FullName;
                block.Description = meta.Description;
            }
            else
            {
                block.TagName = ScummTag.ToString(tag); // Fallback to raw chars
            }

            parent.Children.Add(block);

            // Use uint comparison for container logic
            if (_IsContainer(tag))
            {
                // If it's a WRAP, the actual data starts 8 bytes in
                _ParseChildren(data, pos + 8, pos + size, block, silent: true);
            }

            pos += size;
        }
    }

    // These container tags have child blocks inside them
    private static readonly HashSet<uint> ContainerTags = new()
    {
        ScummTag.LECF,
        ScummTag.LFLF,
        ScummTag.ROOM,
        ScummTag.RMIM,
        ScummTag.IMAG,
        ScummTag.OBIM,
        ScummTag.OBCD,
        ScummTag.AKOS,
        ScummTag.CHAR,
        ScummTag.WRAP, // Critical for nested metadata
        ScummTag.PALS,
        ScummTag.RMSC, // Room Scripts container
        ScummTag.BSTR,
        // ScummTag.OFFS,
        ScummTag.SMAP,
        ScummTag.ZPLN,
        ScummTag.SOUN,
        ScummTag.MAP,
        ScummTag.iMUS,
        ScummTag.FRMT,
        ScummTag.REGN,
        ScummTag.STOP,
        ScummTag.DATA,

    };


    private bool _IsContainer(uint tag) => ContainerTags.Contains(tag);



    // private HashSet<string> missingtags = new();

    private void _AddNameDescription(ScummBlock block)
    {
        if (!ScummTag.TryGetMeta(block.Tag, out var meta))
        {
            // if (missingtags.Add(block.Tag))
            // {
            //     GD.Print(block.Tag);
            // }
            return;
        }
        block.FullName = meta.FullName;
        block.Description = meta.Description;
        block.TagName = meta.TagName;
    }

    private void _DecodeMetadata(ScummBlock block)
    {
        // Pre: leaf/header blocks
        switch (block.Tag)
        {
            case ScummTag.RNAM: DecodeRNAM(block); break;
            case ScummTag.LOFF: DecodeLOFF(block); break;
            case ScummTag.RMHD: DecodeRMHD(block); break;
            case ScummTag.OBIM: DecodeOBIM(block); break;
            case ScummTag.CDHD: DecodeCDHD(block); break;
            case ScummTag.OBNA: DecodeOBNA(block); break;
            case ScummTag.BOXD: DecodeBOXD(block); break;
            case ScummTag.AKOS: DecodeAKOS(block); break;
            case ScummTag.AKHD: DecodeAKHD(block); break;
        }

        foreach (var child in block.Children)
        {
            _AddNameDescription(child);
            _DecodeMetadata(child);
        }

        // Post: containers that need child data ready first
        switch (block.Tag)
        {
            case ScummTag.OBCD: DecodeOBCD(block); break;
            case ScummTag.LFLF: DecodeLFLF(block); break;
        }
        
        // if (block.Tag == ScummTag.LFLF && _rnamBlock != null)
        // {
        //     block.Metadata["Room Name"] = _rnamBlock.roomNames[lflfsNamed];
        //     lflfsNamed++;
        // }
    }

    private void DecodeAKHD(ScummBlock block)
    {
        var akhd = block.DataSpan;
        // var akpl = block.FindChild(ScummTag.AKPL).DataSpan;
        ushort versionNumber = akhd.U16LE(0);
        ushort flags         = akhd.U16LE(2);
        ushort choreCount = akhd.U16LE(4);
        ushort celsCount     = akhd.U16LE(6);
        ushort codec         = akhd.U16LE(8);
        ushort layerCount = akhd.U16LE(10);
        bool   mirror        = (flags & 1) != 0;
        bool hasManyDirections = (flags & 2) != 0;
        
        void Set(ScummMeta.AKHD key, Variant val) => block.SetMetaDataItem(key, val, true);
        
        
        var dataMap = new (ScummMeta.AKHD Key, Variant Val)[] {
            (ScummMeta.AKHD.VersionNo,  versionNumber),
            (ScummMeta.AKHD.Flags,    flags),
            (ScummMeta.AKHD.ChoreCount,    choreCount),
            (ScummMeta.AKHD.CelsCount,     celsCount),
            (ScummMeta.AKHD.Codec,   codec),
            (ScummMeta.AKHD.LayerCount,   layerCount),
            (ScummMeta.AKHD.Mirror,   mirror),
            (ScummMeta.AKHD.HasManyDirections,   hasManyDirections),
        };
        
        foreach (var item in dataMap)
        {
            Set(item.Key, item.Val);
        }
    }
    private void DecodeAKOS(ScummBlock block)
    {
        // var akhd = block.FindChild(ScummTag.AKHD).DataSpan;
        
    }
    private void DecodeLOFF(ScummBlock block)
    {
        var span  = block.DataSpan;
        int count = span[0];   // 1 byte, matches readRoomsOffsets()
        int pos   = 1;

        for (int i = 0; i < count && pos + 5 <= span.Length; i++, pos += 5)
        {
            int roomNo = span[pos];
            int offset = (int)span.U32LE(pos + 1);
            ScummStatics.RoomOffsets[offset] = roomNo;
        }
    }

    private void DecodeLFLF(ScummBlock block)
    {
        // if (_rnamBlock == null) return;

        if (!ScummStatics.RoomOffsets.TryGetValue(block.Offset, out int roomNo)) return;
        
        block.SetMetaDataItem(ScummMeta.LFLF.roomNo, roomNo);
        // block.FullName = $"Room {roomNo}";
        
        // GD.Print($"[DecodeLFLF] a names: {ScummStatics.RoomNames.Count}");

        // Room 0 is the index room — no name expected
        if (roomNo <= 0) return;

        // Genuinely unexpected — room number exceeds what RNAM described
        if (roomNo >= ScummStatics.RoomNames.Count)
        {
            GD.PrintErr($"[LFLF] roomNo {roomNo} exceeds RNAM list ({ScummStatics.RoomNames.Count})");
            return;
        }

        string name = ScummStatics.RoomNames[roomNo];
        if (name.Length == 0) return; // valid gap in RNAM, just no name assigned

        
        block.SetMetaDataItem(ScummMeta.LFLF.roomName, name);
        block.FullName = $"Room {roomNo} — {name}";
    }

    // private List<string> roomNames = new();

    private void DecodeRNAM(ScummBlock block)
    {
        // List<string> roomNames = new(); // indexed list the caller asked for
        // Dictionary<int, string> roomLookup = new(); // fast reverse lookup
        
        var span = block.DataSpan;
        int pos = 0;


        ScummStatics.RoomNames.Add(string.Empty); // slot 0 — always empty
        var nameBytes = new byte[9];

        while (pos < span.Length)
        {
            int roomNo = span[pos++];
            if (roomNo == 0) break;

            if (pos + 9 > span.Length)
            {
                GD.PrintErr($"[RNAM] Truncated at room {roomNo}, pos={pos}");
                break;
            }

            for (int i = 0; i < 9; i++)
                nameBytes[i] = (byte)(~span[pos + i] & 0xFF);
            pos += 9;

            int len = Array.IndexOf(nameBytes, (byte)0);
            if (len < 0) len = 9;
            string name = Encoding.ASCII.GetString(nameBytes, 0, len);

            // Pad the list if room numbers are sparse (shouldn't happen in COMI,
            // but defensive code never hurts)
            while (ScummStatics.RoomNames.Count <= roomNo)
                ScummStatics.RoomNames.Add(string.Empty);

            ScummStatics.RoomNames[roomNo] = name;
            ScummStatics.RoomLookup[roomNo] = name;
            
            // GD.Print($"added room {roomNo} {name}");
        }
        
        // GD.Print($"[DecodeRNAM] names: {ScummStatics.RoomNames.Count}");

        // Expose both representations so consumers can choose
        // _roomNames = _roomNames; // List<string>  — index = room number
        // roomLookup = roomLookup; // Dictionary<int,string> — reverse lookup

        // int count = roomLookup.Count;
        // block.FullName = $"Room Names ({count} rooms)";
        
        // GD.Print($"[RNAM] {count} rooms:");
        // foreach (var kv in roomLookup.OrderBy(k => k.Key))
            // GD.Print($"  {kv.Key,3}: {kv.Value}");
    }

    private void DecodeRMHD(ScummBlock block)
    {
        var span = block.DataSpan;

        block.SetMetaDataItem(ScummMeta.RMHD.version,span.U32LE(0));
        block.SetMetaDataItem(ScummMeta.RMHD.width,span.U32LE(4));
        block.SetMetaDataItem(ScummMeta.RMHD.height,span.U32LE(8));
        block.SetMetaDataItem(ScummMeta.RMHD.numObjects,span.U32LE(12));
        block.SetMetaDataItem(ScummMeta.RMHD.zBuffer,span.U32LE(16));
        block.SetMetaDataItem(ScummMeta.RMHD.transpar,span.U32LE(20));
    }

    private void DecodeCDHD(ScummBlock block)
    {
        var s = block.DataSpan;
        block.SetMetaDataItem(ScummMeta.CDHD.objId,s.U16LE(0));
        // V8 CDHD has no x/y/w/h — those live in IMHD
        if (s.Length >= 4) block.SetMetaDataItem(ScummMeta.CDHD.flags,s.U16LE(2));
        if (s.Length >= 6) block.SetMetaDataItem(ScummMeta.CDHD.walkX,s.S16LE(4));
        if (s.Length >= 8) block.SetMetaDataItem(ScummMeta.CDHD.walkY,s.S16LE(6));
        if (s.Length >= 9) block.SetMetaDataItem(ScummMeta.CDHD.actorDir,s.Get(8));
    }

    private void DecodeOBNA(ScummBlock block)
    {
        // Null-terminated ASCII string
        var span = block.DataSpan;
        int len = 0;
        while (len < span.Length && span[len] != 0) len++;
        var name = Encoding.ASCII.GetString(span.Slice(0, len).ToArray());

        block.SetMetaDataItem(ScummMeta.OBNA.name, name);
        // block.Metadata["name"] = 
        block.FullName = (string)name;
    }

    // private void DecodeOBCD(ScummBlock block)
    // {
    //     var cdhd = block.FindChild(ScummTag.CDHD);
    //     if (cdhd == null) return;
    //
    //     // Mirror CDHD fields onto OBCD so consumers don't need to dig
    //     foreach (var kv in cdhd.Metadata)
    //         block.Metadata[kv.Key] = kv.Value;
    // }

    private void DecodeOBCD(ScummBlock block)
    {
        var cdhd = block.FindChild(ScummTag.CDHD);
        if (cdhd == null)
        {
            GD.Print($"OBCD at 0x{block.Offset:X}, children: {block.Children.Count}");
            GD.Print($"  CDHD: {("NULL")}");
        }

        if (cdhd == null) return;
        var cdhdmeta = cdhd.GetMetaDataDict();
        block.SetMetaDataDict(cdhdmeta);
        // foreach (var kv in metadata)
        //     block.Metadata[kv.Key] = kv.Value;

        var obna = block.FindChild(ScummTag.OBNA);
        if (obna != null && obna.GetMetadataItem(ScummMeta.OBNA.name, out var name))
            block.SetMetaDataItem(ScummMeta.OBNA.name, name);
    }

    // private void DecodeOBIM(ScummBlock block)
    // {
    //     var span = block.DataSpan;
    //     block.Metadata["objId"] = span.U16LE(0);
    //
    //     var imhd = block.FindChild(ScummTag.IMHD);
    //     if (imhd != null) {
    //         var iSpan = imhd.DataSpan;
    //         block.Metadata["width"] = iSpan.U16LE(0);
    //         block.Metadata["height"] = iSpan.U16LE(2);
    //         block.Metadata["relX"] = iSpan.S16LE(4); // Offset relative to CDHD pos
    //         block.Metadata["relY"] = iSpan.S16LE(6);
    //     }
    // }

    private void DecodeOBIM(ScummBlock block)
    {
        var imhd = block.FindChild(ScummTag.IMHD);
        if (imhd == null)
            return;

        var s = imhd.DataSpan;

        // Fixed 32-byte string
        int len = 0;
        while (len < 32 && s[len] != 0)
            len++;

        block.SetMetaDataItem(ScummMeta.OBIM.name,
            Encoding.ASCII.GetString(s.Slice(0, len).ToArray()));

       block.SetMetaDataItem(ScummMeta.OBIM.version,s.U32LE(40));
       block.SetMetaDataItem(ScummMeta.OBIM.imageCount,s.U32LE(44));

       block.SetMetaDataItem(ScummMeta.OBIM.x,s.S32LE(48));
       block.SetMetaDataItem(ScummMeta.OBIM.y,s.S32LE(52));
       block.SetMetaDataItem(ScummMeta.OBIM.width,s.U32LE(56));
       block.SetMetaDataItem(ScummMeta.OBIM.height,s.U32LE(60));

       block.SetMetaDataItem(ScummMeta.OBIM.actorDir,s.U32LE(64));

        // Only exists in COMI 801+
        uint version = s.U32LE(40);
        if (version >= 801 && s.Length >= 72)
            block.SetMetaDataItem(ScummMeta.OBIM.flags, s.U32LE(68));

        // Hotspots begin after flags
        int hotspotBase = (version >= 801) ? 72 : 68;

        // for (int i = 0; i < 15; i++)
        // {
        //     int off = hotspotBase + i * 8;
        //
        //     if (off + 8 > s.Length)
        //         break;
        //
        //     block.Metadata[$"hotspot{i}x"] = s.S32LE(off);
        //     block.Metadata[$"hotspot{i}y"] = s.S32LE(off + 4);
        // }
    }

    private void DecodeBOXD(ScummBlock block)
    {
        var span = block.DataSpan;
        int numBoxes = span.U16LE(0);

        // Each box is 20 bytes in V8
        // Format: 4 corners (X1, Y1, X2, Y2, X3, Y3, X4, Y4) + flags + zplane
        // for (int i = 0; i < numBoxes; i++)
        // {
        //     int offset = 2 + (i * 20);
        //     var boxName = $"box_{i}";
        //    
        //  
        //     block.Metadata[$"{boxName}_x1"] = span.S16LE(offset);
        //     block.Metadata[$"{boxName}_y1"] = span.S16LE(offset + 2);
        //     block.Metadata[$"{boxName}_x2"] = span.S16LE(offset + 4);
        //     block.Metadata[$"{boxName}_y2"] = span.S16LE(offset + 6);
        //     
        // }
    }


    private static bool _LooksLikeContainer(byte[] data, int start, int end)
    {
        end = Math.Min(end, data.Length);
        if (end - start < 8) return false;

        int pos = start;
        int count = 0;

        while (pos + 8 <= end)
        {
            bool tagOk = true;
            for (int i = 0; i < 4; i++)
            {
                byte b = data[pos + i];
                if (b < 0x20 || b > 0x7E)
                {
                    tagOk = false;
                    break;
                }
            }
            if (!tagOk) break;

            int size = _ReadBEInt(data, pos + 4);
            if (size < 8 || (long)pos + size > end) break;

            pos += size;
            count++;
        }

        return count > 0;
    }

    // ── Obfuscation ───────────────────────────────────────────────────────────

    private bool _IsObfuscated(byte[] data)
    {
        if (data.Length < 4) return false;

        // Read the first 4 bytes as a Big-Endian uint
        uint firstFour = BinaryPrimitives.ReadUInt32BigEndian(data);

        // XOR the whole uint with the key repeated (0x69696969)
        uint decrypted = firstFour ^ 0x69696969;

        // Check against LECF or RNAM constants
        return decrypted == ScummTag.LECF || decrypted == ScummTag.RNAM;
    }
    // private bool _IsObfuscated(byte[] data)
    // {
    //     // LECF tag after XOR decode = 0x4C 0x45 0x43 0x46
    //     // Check if first bytes XOR'd with 0x69 give "LECF" or "RNAM"
    //     if (data.Length < 4) return false;
    //     string plain = $"{(char)(data[0] ^ XorKey)}{(char)(data[1] ^ XorKey)}{(char)(data[2] ^ XorKey)}{(char)(data[3] ^ XorKey)}";
    //     return plain == "LECF" || plain == "RNAM";
    // }

    private void _XorDecrypt(byte[] data)
    {

        for (int i = 0; i < data.Length; i++)
            data[i] ^= XorKey;
    }

    // ── Binary helpers ────────────────────────────────────────────────────────

    private static string _ReadTag(byte[] b, int pos) =>
        $"{(char)b[pos]}{(char)b[pos + 1]}{(char)b[pos + 2]}{(char)b[pos + 3]}";

    private static int _ReadBEInt(byte[] b, int pos) =>
        (b[pos] << 24) | (b[pos + 1] << 16) | (b[pos + 2] << 8) | b[pos + 3];
}