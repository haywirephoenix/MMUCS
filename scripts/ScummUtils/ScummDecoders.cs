using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;

public static class ScummDecoders
{

    #region OBIM
    public static bool DecodeObjectImage(
        ScummBlock objectBlock,
        CancellationToken token,
        out Image objectImage,
        out byte[] indexedPixels,
        out int outPitch,
        out Color[] resolvedPalette
    )
    {
        objectImage = null;
        indexedPixels = null;
        outPitch = 0;
        ReadOnlySpan<byte> _roomPalette;
        resolvedPalette = null;

        var imhd = objectBlock.FindChild(ScummTag.IMHD);
        if (imhd == null)
        {
            GD.PrintErr("IMHD tag not found");
            return false;
        }

        var room = objectBlock.FindParent(ScummTag.ROOM);
        var apal = room.FindChild(ScummTag.PALS).FindChildRecursive(ScummTag.APAL);
        if (apal == null)
        {
            GD.PrintErr("PALS tag not found");
            return false;
        }
        _roomPalette = apal.DataSpan;
        resolvedPalette = ExtractPalette(apal);

        var s = imhd.DataSpan;

        uint version = s.U32LE(40);

        int width = (int)s.U32LE(56);
        int height = (int)s.U32LE(60);

        if (width <= 0 || height <= 0) return false;

        var imag = objectBlock.FindChild(ScummTag.IMAG);
        if (imag == null)
        {
            GD.PrintErr("IMAG tag not found");
            return false;
        }

        var smap = imag.FindChildRecursive(ScummTag.SMAP);
        var bomp = imag.FindChildRecursive(ScummTag.BOMP);

        int pitch = Align8(width);
        byte[] buffer = new byte[pitch * height];
        byte transparentIndex = 5;

        if (smap != null)
        {
            if (!TryDecodeSmapBlock(smap, width, height, _roomPalette, token, out buffer, out pitch))
                return false;

            transparentIndex = 5;
        }
        else if (bomp != null)
        {
            buffer = DecodeBomp(
                bomp.FullSpan,
                width,
                height);

            transparentIndex = 255;
        }
        else
        {
            GD.PrintErr("SMAP and BOMP not found");
            return false;
        }

        indexedPixels = buffer;
        outPitch = pitch;

        objectImage = ScummImageUtils.CreateGodotImage(buffer, width, height, pitch, apal.DataSpan, transparentIndex);

        return true;
    }


    private static byte[] DecodeBomp(
        ReadOnlySpan<byte> data,
        int width,
        int height)
    {
        byte[] dst = new byte[width * height];

        // V8 header skip
        int srcPos = 16;

        for (int y = 0; y < height; y++)
        {
            if (srcPos + 2 > data.Length)
                break;

            int rowSize = data.U16LE(srcPos);
            srcPos += 2;

            if (rowSize <= 0 || srcPos + rowSize > data.Length)
                break;

            DecodeBompRow(
                data.Slice(srcPos, rowSize),
                dst,
                y * width,
                width);

            srcPos += rowSize;
        }

        return dst;
    }


    private static void DecodeBompRow(
        ReadOnlySpan<byte> src,
        byte[] dst,
        int dstPos,
        int width)
    {
        int x = 0;
        int pos = 0;

        while (x < width && pos < src.Length)
        {
            byte code = src[pos++];

            int count = (code >> 1) + 1;

            if (count > width - x)
                count = width - x;

            if ((code & 1) != 0)
            {
                // solid run
                byte color = src[pos++];

                for (int i = 0; i < count; i++)
                    dst[dstPos + x++] = color;
            }
            else
            {
                // literal run
                for (int i = 0; i < count; i++)
                    dst[dstPos + x++] = src[pos++];
            }
        }
    }
    #endregion

    #region Backgrounds
    public static bool DecodeBackgroundImage(
        int roomId,
        ScummBlock _rmhdBlock,
        ScummBlock _roomBlock,
        CancellationToken token,
        out Image backgroundImage,
        out byte[] indexedPixels,
        out int outPitch)
    {
        backgroundImage = null;
        indexedPixels = null;
        outPitch = 0;
        ReadOnlySpan<byte> _roomPalette;

        var rmhd = _rmhdBlock;
        if (rmhd == null) return false;

        var apal = _roomBlock.FindChildRecursive(ScummTag.APAL);
        if (apal == null) return false;

        _roomPalette = apal.DataSpan;

        var rmhdSpan = rmhd.DataSpan;
        int width = (int)rmhdSpan.U32LE(4);
        int height = (int)rmhdSpan.U32LE(8);

        var smap = _roomBlock.FindChildRecursive(ScummTag.SMAP);
        if (smap == null) return false;

        if (!TryDecodeSmapBlock(smap, width, height, _roomPalette, token, out byte[] flatBuffer, out int pitch))
        {
            return false;
        }

        outPitch = pitch;
        indexedPixels = flatBuffer;
        
        backgroundImage = ScummImageUtils.CreateGodotImage(flatBuffer, width, height, pitch, apal.DataSpan, LastStripOverride);
        return (backgroundImage != null);
    }
    #endregion


    #region Common

    public static byte LastStripOverride = 5;
   public static bool TryDecodeSmapBlock(
    ScummBlock smapBlock,
    int width,
    int height,
    ReadOnlySpan<byte> roompalette,
    CancellationToken token,
    out byte[] indexedPixels,
    out int outPitch)
    {
        indexedPixels = null;
        outPitch = 0;

        if (smapBlock == null) return false;
        

        var bstr = smapBlock.FindChildRecursive(ScummTag.BSTR);
        var wrap = bstr?.FindChildRecursive(ScummTag.WRAP);
        var offs = wrap?.FindChildRecursive(ScummTag.OFFS);

        if (offs == null) return false;

        // Pitch allocation handles padding out to an 8-pixel alignment boundary
        int pitch = width;
        byte[] flatBuffer = new byte[pitch * height];

        var offsData = offs.DataSpan;
        int actualStripsInBlock = (offs.DataLength) / 4;
        var fileSpan = new ReadOnlySpan<byte>(offs.FileData);

        // Loop through every defined offset in the table rather than assuming width / 8
        for (int i = 0; i < actualStripsInBlock; i++)
        {
            if (i % 5 == 0 && token.IsCancellationRequested) return false;

            uint stripOffset = offsData.U32LE(i * (4));
            
            // Skip null/unallocated strip offsets safely
            if (stripOffset == 0 || stripOffset == 0xFFFFFFFF) continue;

            int absoluteOffset = offs.Offset + (int)stripOffset;
            if (absoluteOffset >= offs.FileData.Length)
            {
                GD.PrintErr($"Strip {i}: absolute offset 0x{absoluteOffset:X} out of file bounds");
                continue;
            }

            int bstrEnd = bstr.Offset + bstr.Size;
            if (absoluteOffset < bstr.Offset || absoluteOffset >= bstrEnd)
            {
                GD.PrintErr($"Strip {i}: offset OOB vs BSTR");
                continue;
            }

            // Look ahead to find out how large this individual strip slice payload actually is
            // uint nextOffset = 0;
            // if (i < actualStripsInBlock - 1)
            //     nextOffset = offsData.U32LE((i + 1) * 4);

            // If this offset matches the next one, it represents an empty or identical skipped column
            // if (stripOffset == nextOffset && i < actualStripsInBlock - 1) continue;

            // int stripLength = (int)(nextOffset - stripOffset);;
           
            // if (i == actualStripsInBlock - 1){
            //     stripLength = bstrEnd - absoluteOffset ;
            // }

            // if (stripLength <= 0)
            // {
            //     GD.PrintErr($"Strip {i}: invalid strip length");
            //     continue;
            // }

            var stripSrc = fileSpan.Slice(absoluteOffset);
            byte codec = stripSrc[0];
            var compressedPayload = stripSrc.Slice(1);

            // Map column dynamically to visual coordinates
            int destX = i * 8;
            if (destX >= pitch)
            {
                GD.PrintErr($"Strip {i}: target position X={destX} exceeds buffer pitch allocation {pitch}");
                continue;
            }

            Span<byte> destSpan = flatBuffer.AsSpan(destX, flatBuffer.Length - destX);
            

            DecodeStrip(compressedPayload, destSpan, codec, pitch, height);
        }

        outPitch = pitch;
        indexedPixels = flatBuffer;
        return true;
    }
   
  
    
    private static void DecodeMajMin(
        ReadOnlySpan<byte> src,
        Span<byte> dst,
        int pitch,
        int height,
        byte codec,
        bool transpCheck)
    {
        if (src.Length < 3) return;
        
        int shift = codec - ScummCodecs.BMCOMP_RMAJMIN_H0;
        
        if(transpCheck) shift = codec - ScummCodecs.BMCOMP_RMAJMIN_HT0;
        
        if (shift < 0 || shift > 32) 
            return;

        byte color = src[0];

        var br = new BitReaderLSB(src.Slice(1));

        bool repeatMode = false;
        int repeatCount = 0;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                dst[(y * pitch) + x] = color;

                    if (!repeatMode)
                {
                    if (br.ReadBit() != 0)
                    {
                        if (br.ReadBit() != 0)
                        {
                            int diff = br.ReadBits(3) - 4;

                            if (diff != 0)
                            {
                                color = (byte)(color + (sbyte)diff);
                            }
                            else
                            {
                                repeatMode = true;
                                repeatCount = br.ReadBits(8) - 1;

                                if (repeatCount < 0)
                                    repeatCount = 0;
                            }
                        }
                        else
                            color = (byte)br.ReadBits(shift);
                    }
                }
                else
                {
                    repeatCount--;

                    if (repeatCount <= 0) repeatMode = false;
                }

                // if (br.Exhausted) return;
            }
        }
    }


    private static void DecodeZigZagV(
        ReadOnlySpan<byte> src,
        Span<byte> dst,
        int pitch,
        int height,
        byte codec,
        bool transpCheck)
    {
        if (src.Length < 1)
            return;
     
        int bitsPerPixel = codec - ScummCodecs.BMCOMP_PIX32;

        byte color = src[0];

        var br = new BitReaderLSB(src.Slice(1));

        sbyte inc = -1;

        for (int x = 0; x < 8; x++)
        {
            for (int y = 0; y < height; y++)
            {
                dst[(y * pitch) + x] = color;

                if (br.ReadBit() == 0)
                {
                    // unchanged
                }
                else if (br.ReadBit() == 0)
                {
                    color = (byte)br.ReadBits(bitsPerPixel);
                    inc = -1;
                }
                else if (br.ReadBit() == 0)
                {
                    color = (byte)(color + inc);
                }
                else
                {
                    inc = (sbyte)-inc;
                    color = (byte)(color + inc);
                }

                if (br.Exhausted)
                    return;
            }
        }
    }

    private static void DecodeZigZagH(
        ReadOnlySpan<byte> src,
        Span<byte> dst,
        int pitch,
        int height,
        byte codec,
        bool transpCheck)
    {
        if (src.Length < 1)
            return;

        int bitsPerPixel = codec - ScummCodecs.BMCOMP_ZIGZAG_H0;

        // if (bitsPerPixel < 0)
        //     bitsPerPixel = codec - ScummCodecs.BMCOMP_ZIGZAG_HT4;
        
        if(transpCheck) bitsPerPixel -= ScummCodecs.BMCOMP_ZIGZAG_H0;;

        byte color = src[0];

        var br = new BitReaderLSB(src.Slice(1));

        sbyte inc = -1;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                dst[(y * pitch) + x] = color;

                if (br.ReadBit() == 0)
                {
                    // unchanged
                }
                else if (br.ReadBit() == 0)
                {
                    color = (byte)br.ReadBits(bitsPerPixel);
                    inc = -1;
                }
                else if (br.ReadBit() == 0)
                {
                    color = (byte)(color + inc);
                }
                else
                {
                    inc = (sbyte)-inc;
                    color = (byte)(color + inc);
                }

                if (br.Exhausted)
                    return;
            }
        }
    }


    private static void DecodeRaw(
        ReadOnlySpan<byte> src,
        Span<byte> dst,
        int pitch,
        int height,
        byte transparentColor = 0,
        bool transpCheck = false)
    {
        int srcIdx = 0;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                if (srcIdx >= src.Length) return;

                byte color = src[srcIdx++];

                if (!transpCheck || color != transparentColor)
                {
                    // write the index directly to the buffer
                    dst[(y * pitch) + x] = color;
                }
            }
        }
    }

    
    private static void DecodeMajMinHE(
        ReadOnlySpan<byte> src,
        Span<byte> dst,
        int pitch,
        int height,
        byte codec,
        bool transpCheck)
    {
        ReadOnlySpan<sbyte> deltaColor = [-4, -3, -2, -1, 1, 2, 3, 4];

        if (src.Length < 4)
            return;

        byte color = src[0];
        var br = new BitReaderLSB(src.Slice(1));

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                if (!transpCheck || color != 0)
                    dst[(y * pitch) + x] = color;

                if (br.Exhausted) return;

                if (br.ReadBit() != 0)
                {
                    if (br.ReadBit() != 0)
                    {
                        color = (byte)(color + deltaColor[br.ReadBits(3)]);
                    }
                    else
                    {
                        int decompShr = codec - ScummCodecs.BMCOMP_NMAJMIN_H0;
                        color = (byte)br.ReadBits(decompShr);
                    }
                }
            }
        }
    }



    static void DecodeStrip(ReadOnlySpan<byte> src, Span<byte> dst, byte codec, int pitch, int height)
    {
        ScummCodecs.ECodec eCodec = codec.ByteToECodec();
        // GD.Print($"Decoding strip as {codec.ByteToCodecName()}");

        switch (eCodec)
        {
            case ScummCodecs.ECodec.Raw: DecodeRaw(src, dst, pitch, height, codec, false); break;
            case ScummCodecs.ECodec.RawT: DecodeRaw(src, dst, pitch, height, codec, true); break;
            case ScummCodecs.ECodec.ZigZagV: DecodeZigZagV(src, dst, pitch, height, codec, false); break;
            case ScummCodecs.ECodec.ZigZagVT: DecodeZigZagV(src, dst, pitch, height, codec, true); break;
            case ScummCodecs.ECodec.ZigZagH: DecodeZigZagH(src, dst, pitch, height, codec, false); break;
            case ScummCodecs.ECodec.ZigZagHT: DecodeZigZagH(src, dst, pitch, height, codec, true); break;
            case ScummCodecs.ECodec.MajMin: DecodeMajMin(src, dst, pitch, height, codec, false); break;
            case ScummCodecs.ECodec.MajMinT: DecodeMajMin(src, dst, pitch, height, codec, true); break;
            // case ScummCodecs.ECodec.MajMinHE: DecodeMajMinHE(src, dst, pitch, height, codec, false); break;
            // case ScummCodecs.ECodec.MajMinHET: DecodeMajMinHE(src, dst, pitch, height, codec, true); break;
            case ScummCodecs.ECodec.UNKNOWN:
            default: ScummCodecs.LogMissingCodec(codec); break;
        }


    }
    #endregion

    #region Helpers
    public static void StubDecode(IndexedSurface surface, Color tint)
    {
        // Placeholder until codec is wired in — draws a colored grid
        // for (int y = 0; y < image.GetHeight(); y++)
        //     for (int x = 0; x < image.GetWidth(); x++)
        //         image.SetPixel(x, y, ((x / 8 + y / 8) % 2 == 0) ? tint : Colors.Transparent);
    }
    private static int Align8(int value)
    {
        return (value + 7) & ~7;
    }
    private static int ResolveObjectStripOffset(ScummBlock smap, uint stripOffset)
    {
        int offstet = 0;
        if (stripOffset < smap.Size)
            offstet = smap.Offset; // SMAP-relative

        return offstet;
    }

    

    public static Color[] ExtractPalette(ScummBlock apalBlock)
    {
        ReadOnlySpan<byte> span = apalBlock.DataSpan;

        int numColors = span.Length / 3;
        Color[] palette = new Color[numColors];

        for (int i = 0; i < numColors; i++)
        {
            int r = span[i * 3];
            int g = span[i * 3 + 1];
            int b = span[i * 3 + 2];
            palette[i] = new Color(r / 255f, g / 255f, b / 255f, 1.0f);
        }
        return palette;
    }

    public ref struct BitReaderLSB(ReadOnlySpan<byte> src, int startIndex = 0)
    {
        private ReadOnlySpan<byte> _src = src;
        private int _index = startIndex;

        private uint _bits = 0;
        private int _numBits = 0;

        public bool Exhausted { get; private set; } = false;

        public int ReadBits(int count)
        {
            while (_numBits < count)
            {
                if (_index >= _src.Length)
                {
                    Exhausted = true;
                    return 0;
                }

                _bits |= (uint)(_src[_index++] << _numBits);
                _numBits += 8;
            }

            int value = (int)(_bits & ((1u << count) - 1));

            _bits >>= count;
            _numBits -= count;

            return value;
        }

        public int ReadBit()
        {
            return ReadBits(1);
        }
    }
    #endregion

    /*private static bool DecodeObjectSmap(
        ScummBlock smap,
        int width,
        int height,
        ScummBlock apal,
        out Image objectImage,
        out byte[] indexedPixels,
        out int outPitch,
        CancellationToken token)
    {
        objectImage   = null;
        indexedPixels = null;
        outPitch      = 0;

        var data = smap.FullSpan;

        int numStrips = width / 8;
        int pitch = Align8(width);

        byte[] buffer = new byte[pitch * height];

        int offsTable = 32;

        for (int strip = 0; strip < numStrips; strip++)
        {
            if (token.IsCancellationRequested)
                return false;

            int offsPos = offsTable + strip * 4;
            if (offsPos + 4 > data.Length)
                break;

            uint offset = data.U32LE(offsPos);

            if (offset == 0 || offset == 0xFFFFFFFF)
                continue;

            int absolute = smap.Offset + (int)offset;

            if (absolute < smap.Offset || absolute >= smap.Offset + smap.Size)
                continue;

            int stripLen;

            if (strip < numStrips - 1)
            {
                uint next = data.U32LE(offsTable + (strip + 1) * 4);
                stripLen = (int)(next - offset);
            }
            else
            {
                stripLen = (smap.Offset + smap.Size) - absolute;
            }

            if (stripLen <= 0)
                continue;

            var stripSrc = new ReadOnlySpan<byte>(smap.FileData, absolute, stripLen);

            byte codec = 1;//stripSrc[0]
            var payload = stripSrc.Slice(1);

            int destX = strip * 8;

            Span<byte> dst = buffer.AsSpan(destX);

            DecodeStrip(payload, dst, codec, pitch, height);
        }

        indexedPixels = buffer;
        outPitch = pitch;

        objectImage = ScummImageUtils.CreateGodotImage(buffer, width, height,pitch,apal.DataSpan, 255);
        return objectImage != null;
    }*/

    /*private static bool DecodeObjectBompGroup(
        ScummBlock imag,
        int width,
        int height,
        ScummBlock apal,
        out Image objectImage,
        out byte[] indexedPixels,
        out int outPitch,
        CancellationToken token)
    {
        objectImage = null;
        indexedPixels = null;
        outPitch = 0;

        int pitch = Align8(width);
        byte[] buffer = new byte[pitch * height];

        var bomps = imag.FindChildrenRecursive(ScummTag.BOMP);

        int index = 0;

        foreach (var bomp in bomps)
        {
            if (token.IsCancellationRequested)
                return false;

            var data = bomp.DataSpan;

            if (data.Length < 6)
                continue;

            int bompWidth  = data.U16LE(2);
            int bompHeight = data.U16LE(4);

            int w = bompWidth > 0 ? bompWidth : width;
            int h = bompHeight > 0 ? bompHeight : height;

            int srcPos = 0;

            for (int y = 0; y < h; y++)
            {
                if (srcPos + 2 > data.Length)
                    break;

                ushort rowSize = data.U16LE(srcPos);
                srcPos += 2;

                var row = data.Slice(srcPos, rowSize);
                srcPos += rowSize;

                Span<byte> dst = buffer.AsSpan(y * pitch, w);

                DecodeBompRow(row, dst, w);
            }

            index++;
        }

        indexedPixels = buffer;
        outPitch = pitch;

        objectImage = CreateGodotImage2(buffer, width, height, pitch, apal.DataSpan);
        return objectImage != null;
    }
    */
}