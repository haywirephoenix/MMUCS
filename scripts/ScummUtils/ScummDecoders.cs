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
    
    public static bool TryGetObimInfo(ScummBlock obimBlock, out ObimDecoders.ObimInfo info)
    => ObimDecoders.TryGetObimInfo(obimBlock, out info);
    
    public static bool TryDecodeObimFrame(
        ScummBlock objectBlock,
        CancellationToken token,
        int frameIndex,
        int paletteIndex,
        out ObimDecoders.ObimDecodeRawResult result,
        out Image bakedImage,
        bool generateImage = false)
    => ObimDecoders.TryDecodeObimFrame(objectBlock, token, frameIndex, paletteIndex, out result, out bakedImage, generateImage);
    
    #endregion

    #region ROOM
    
    public static bool DecodeBackgroundImage(
        int roomId,
        ScummBlock _rmhdBlock,
        ScummBlock _roomBlock,
        int paletteIndex,
        CancellationToken token,
        out Image backgroundImage,
        out byte[] indexedPixels,
        out int outPitch,
        out ReadOnlySpan<byte> roomPalette,
        bool generateImage = false) =>
        RoomDecoders.DecodeBackgroundImage(roomId, _rmhdBlock, _roomBlock, paletteIndex, token, out backgroundImage, out indexedPixels, out outPitch, out roomPalette, generateImage);
    
    public static bool TryGetRoomInfo(ScummBlock rmhdBlock, ScummBlock roomBlock, out RoomDecoders.RoomInfo info)
    =>  RoomDecoders.TryGetRoomInfo(rmhdBlock, roomBlock, out info);
    
    #endregion

    #region Common

   public static bool TryDecodeSmapBlock(
    ScummBlock smapBlock,
    int width,
    int height,
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
}