using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
public static class ScummDecoders
{

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

        backgroundImage = null;

        var rmhd = _rmhdBlock;
        if (rmhd == null) return false;

        var apal = _roomBlock.FindChildRecursive(ScummTag.APAL);
        if (apal == null) return false;
        /*
        if (ScummBackgroundCache.TryLoadFromCache(roomId,
            out int w,
            out int h,
            out int p,
            out byte[] indexedPixels))
        {

            var img = ScummBackgroundCache.CreateImageFromIndexed(
                indexedPixels,
                w,
                h,
                p,
                apal.DataSpan);

                backgroundImage = img;

                if (img != null) return true;
        }*/
        
        var rmhdSpan = rmhd.DataSpan;
        int width = (int)rmhdSpan.U32LE(4);
        int height = (int)rmhdSpan.U32LE(8);
        
        var smap = _roomBlock.FindChildRecursive(ScummTag.SMAP);
        if (smap == null) return false;

        var bstr = smap.FindChildRecursive(ScummTag.BSTR);
        var wrap = bstr?.FindChildRecursive(ScummTag.WRAP);
        var offs = wrap?.FindChildRecursive(ScummTag.OFFS);

        if (offs == null)
        {
            // SetBackground(null);
            // GD.Print("Background data blocks missing (OFFS or BSTR)");
            return false;
        }
        
        int numStrips = width / 8;
        int pitch = ((width / 8) + 1) * 8;
        byte[] flatBuffer = new byte[pitch * height];

        // OFFS block data is a table of offsets to strips
        var offsData = offs.DataSpan;
        int actualStripsInBlock = offs.DataLength / 4;
        int limit = Math.Min(actualStripsInBlock, numStrips);

        var fileSpan = new ReadOnlySpan<byte>(offs.FileData);

        for (int i = 0; i < limit; i++)
        {
            if (i % 5 == 0 && token.IsCancellationRequested) return false;

            uint stripOffset = offsData.U32LE(i * 4);

            if (stripOffset == 0 || stripOffset == 0xFFFFFFFF) continue;

            // stripSrc is the block-relative offset
            // var stripSrc = offsFullSpan.Slice((int)stripOffset);
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

            uint nextOffset = 0;

            if (i < limit - 1)
                nextOffset = offsData.U32LE((i + 1) * 4);

            if (stripOffset == nextOffset && i < limit - 1)
                continue;

            int stripLength;

            if (i < limit - 1)
            {
                uint next = offsData.U32LE((i + 1) * 4);
                stripLength = (int)(next - stripOffset);
            }
            else
            {
                stripLength = bstr.Offset + bstr.Size - absoluteOffset;
            }

            if (stripLength <= 0)
            {
                GD.PrintErr($"Strip {i}: invalid strip length");
                continue;
            }
            
            var stripSrc = fileSpan.Slice(absoluteOffset, stripLength);

            byte codec = stripSrc[0]; // First byte is the codec ID

            // GD.Print($"Strip {i}: codec 0x{codec:X2}");

            var compressedPayload = stripSrc.Slice(1);

            int destX = i * 8;
            
            Span<byte> destSpan = flatBuffer.AsSpan(destX, flatBuffer.Length - destX);

            switch (codec)
            {
                // BMCOMP_RAW256
                case 0x01:
                    ScummDecoders.DecodeRaw(compressedPayload, destSpan, pitch, height, codec);
                    break;
                // BMCOMP_RMAJMIN_H4-H8
                case 0x54:
                case 0x55:
                case 0x56:
                case 0x57:
                case 0x58:
                case 0x68:
                case 0x69:
                case 0x6A:
                case 0x6B:
                case 0x6C:
                    ScummDecoders.DecodeMajMin(compressedPayload, destSpan, pitch, height, codec);
                    break;
                // BMCOMP_ZIGZAG_V4-V8
                case 0x0E:
                case 0x0F:
                case 0x10:
                case 0x11:
                case 0x12:
                    // GD.Print($"strip {i} zigzag!");
                    ScummDecoders.DecodeZigZagV(compressedPayload, destSpan, pitch, height, codec);
                    break;
                case 0x18:
                case 0x19:
                case 0x1A:
                case 0x1B:
                case 0x1C:
                    DecodeZigZagH(compressedPayload, destSpan, pitch, height, codec);
                    break;

                default:
                    if (CompressionTypes.TryGetValue(codec, out var type))
                        GD.PrintErr($"Strip {i}: missing codec {type}");
                    else
                        GD.PrintErr($"Strip {i}: Unknown codec 0x{codec:X2}");

                    break;
            }

        }

        // backgroundImage = CreateGodotBGImage(flatBuffer, width, height, pitch, apal.DataSpan);

        /*

        ScummBackgroundCache.SaveIndexedBackground(
            GetBackgroundCachePath(roomId),
            width,
            height,
            pitch,
            flatBuffer);*/

        outPitch = pitch;
        indexedPixels = flatBuffer; // Return the raw indices for the cache
        backgroundImage = CreateGodotBGImage(flatBuffer, width, height, pitch, apal.DataSpan);
        return (backgroundImage != null);
    }

    private static Image CreateGodotBGImage(byte[] pixels, int width, int height, int pitch, ReadOnlySpan<byte> apal)
    {
        // 1. Create the base L8 image (Indexed/Grayscale)
        // We use pitch to ensure we only capture the actual pixel data if the buffer is padded
        byte[] sanitizedPixels = pixels;

        if (pitch != width)
        {
            sanitizedPixels = new byte[width * height];
            for (int y = 0; y < height; y++)
            {
                Array.Copy(pixels, y * pitch, sanitizedPixels, y * width, width);
            }
        }

        // Map the 8-bit indices to actual RGBA colors using the palette
        // SCUMM palettes are usually 768 bytes (256 * RGB)
        byte[] rgbaData = new byte[width * height * 4];

        for (int i = 0; i < width * height; i++)
        {
            int paletteIndex = sanitizedPixels[i] * 3;
            int rgbaIndex = i * 4;

            if (paletteIndex + 2 < apal.Length)
            {
                rgbaData[rgbaIndex + 0] = apal[paletteIndex + 0]; // R
                rgbaData[rgbaIndex + 1] = apal[paletteIndex + 1]; // G
                rgbaData[rgbaIndex + 2] = apal[paletteIndex + 2]; // B
                rgbaData[rgbaIndex + 3] = 255; // A (Opaque)
            }
        }
        
        var image = Image.CreateFromData(width, height, false, Image.Format.Rgba8, rgbaData);
        return image;
    }

    /* akos */

    public static async Task<IndexedSurface> GetCachedCelAsync(AkosData akos, int index, CancellationToken token)
    {
        long key = AkosCelCache.BuildCelCacheKey(akos, index);

        if (!akos.DecodedCels.TryGetValue(key, out var surface))
        {
            surface = await Task.Run(() => _DecodeCel(akos, index, token), token);
            if (surface != null) akos.DecodedCels[key] = surface;
        }
        return surface;
    }

    // Decode a single cel from AKCD using the costume's codec
    public static IndexedSurface _DecodeCel(AkosData _akos, int celIndex, CancellationToken token)
    {
        if (_akos.CelData.IsEmpty || _akos.CelOffsets == null || celIndex >= _akos.CelOffsets.Length)
            return null;

        var offset = _akos.CelOffsets[celIndex];

        // Use AkciOffset as a raw byte offset into AKCI data — matches C++ `akci + ciOff`
        ReadOnlySpan<byte> ciSpan = _akos.CelInfoRaw.Span;
        int ciOff = offset.AkciOffset;
        if (ciOff + 12 > ciSpan.Length) return null;

        var ci = new AkosCelInfo
        {
            Width = ciSpan.U16LE(ciOff + 0),
            Height = ciSpan.U16LE(ciOff + 2),
            RelX = (short)ciSpan.U16LE(ciOff + 4),
            RelY = (short)ciSpan.U16LE(ciOff + 6),
            MoveX = (short)ciSpan.U16LE(ciOff + 8),
            MoveY = (short)ciSpan.U16LE(ciOff + 10),
        };

        if (ci.Width == 0 || ci.Height == 0) return null;

        // var image  = Image.CreateEmpty(ci.Width, ci.Height, false, Image.Format.Rgba8);
        var surface = new IndexedSurface
        {
            Width = ci.Width,
            Height = ci.Height,
            Pitch = ci.Width,
            Pixels = new byte[ci.Width * ci.Height],
            TransparentIndex = 0
        };
        // var colors = _akos.ResolvedColors ?? Array.Empty<Color>();
        // uint akplSize = _akos.AkplSize;

        switch (_akos.Header.Codec)
        {
            case AkosCodec.ByleRLE:// 1
                _DecodeByleRLE(_akos.CelData.Span, _akos.AkplSize, surface, ci, offset, token);
                
                break;
            case AkosCodec.CdatRLE:// 5
                DecodeCDAT(_akos.CelData.Span, surface, ci, offset, token);
                
                break;
            case AkosCodec.MajMin:// 16
                DecodeMajMin(_akos.CelData.Span, surface, ci, offset, token);
                // _StubDecode(surface, new Color(0.4f, 0.2f, 0.8f, 0.5f));
                break;
            case AkosCodec.TRLE:// 32
                _StubDecode(surface, new Color(0.8f, 0.4f, 0.2f, 0.5f));
                break;
        }

        return surface;
    }

    public static void _DecodeByleRLE(ReadOnlySpan<byte> src, uint akplSize, IndexedSurface surface, AkosCelInfo ci, AkosOffset offset, CancellationToken token)
    {
        // ReadOnlySpan<byte> src = _akos.CelData.Span;
        int pos = (int)offset.AkcdOffset;

        int shr, mask;

        if (akplSize == 32)
        {
            shr = 3;
            mask = 7;
        }
        else if (akplSize == 64)
        {
            shr = 2;
            mask = 3;
        }
        else
        {
            shr = 4;
            mask = 15;
        }

        int x = 0;
        int y = 0;

        int total = ci.Width * ci.Height;
        int written = 0;

        while (written < total && pos < src.Length)
        {
            if (written % 100 == 0 && token.IsCancellationRequested)
                return;

            byte rep = src[pos++];

            byte color = (byte)(rep >> shr);


            int len = rep & mask;

            if (len == 0)
            {
                if (pos >= src.Length)
                    break;

                len = src[pos++];
            }

            for (int i = 0; i < len && written < total; i++, written++)
            {
                // if(color > 7)
                surface.SetPixel(x, y, color);

                if (++y >= ci.Height)
                {
                    y = 0;
                    x++;
                }
            }
        }
    }

    private static void _StubDecode(IndexedSurface surface, Color tint)
    {
        // Placeholder until codec is wired in — draws a colored grid
        // for (int y = 0; y < image.GetHeight(); y++)
        //     for (int x = 0; x < image.GetWidth(); x++)
        //         image.SetPixel(x, y, ((x / 8 + y / 8) % 2 == 0) ? tint : Colors.Transparent);
    }

    // CDAT (codec 5) — per-line RLE, same wire format as BOMP.
    // Each row: uint16 LE encoded length, then a token stream.
    // Token control byte b:
    //   count = (b >> 1) + 1
    //   b & 1 → run:     next byte is the colour, repeated `count` times
    //   else  → literal: next `count` bytes are pixels, copied directly
    public static void DecodeCDAT(ReadOnlySpan<byte> src, IndexedSurface surface, AkosCelInfo ci, AkosOffset offset, CancellationToken token)
    {
        int pos = (int)offset.AkcdOffset;

        for (int y = 0; y < ci.Height; y++)
        {
            if (token.IsCancellationRequested) return;

            if (pos + 2 > src.Length) break;

            // Read LE uint16 row length (excludes the 2-byte length word itself)
            int rowLen = src[pos] | (src[pos + 1] << 8);
            pos += 2;

            int rowEnd = pos + rowLen;
            int x = 0;

            while (x < ci.Width && pos < rowEnd)
            {
                byte ctrl = src[pos++];
                int count = (ctrl >> 1) + 1;
                if (count > ci.Width - x) count = ci.Width - x; // clamp to row

                if ((ctrl & 1) != 0)
                {
                    // Run: one colour repeated `count` times
                    if (pos >= rowEnd) break;
                    byte colour = src[pos++];
                    for (int i = 0; i < count; i++)
                        surface.SetPixel(x + i, y, colour);
                }
                else
                {
                    // Literal: `count` bytes copied directly
                    int avail = rowEnd - pos;
                    if (pos + count > rowEnd)
                    {
                        // Truncated row — copy what's available and stop
                        for (int i = 0; i < avail; i++)
                            surface.SetPixel(x + i, y, src[pos + i]);
                        pos += avail;
                        x += avail;
                        break;
                    }

                    for (int i = 0; i < count; i++)
                        surface.SetPixel(x + i, y, src[pos + i]);
                    pos += count;
                }

                x += count;
            }

            // Always advance to the next row boundary,
            // even if we decoded fewer than `width` pixels
            pos = rowEnd;
        }
    }

    // MajMin (codec 3) — MSB-first bitstream with delta/run encoding.
    // First byte is sourceShift (skipped — only used by scaled blit).
    // Then 8 bits: initial colour.
    // Each subsequent pixel:
    //   bit 0 → same colour, continue
    //   bit 10 → new absolute 8-bit colour
    //   bit 11 → signed 3-bit delta [-4..+3]; if delta == 0 read 8-bit run length
    // Pixels are written left-to-right, top-to-bottom (row-major).
    public static void DecodeMajMin(ReadOnlySpan<byte> src, IndexedSurface surface, AkosCelInfo ci, AkosOffset offset, CancellationToken token)
    {
        int pos = (int)offset.AkcdOffset + 1; // skip sourceShift

        uint bitBuf = 0;
        int bitsLeft = 0;

        int totalPixels = ci.Width * ci.Height;
        byte color = (byte)ReadBits(src, 8, ref pos, ref bitBuf, ref bitsLeft);

        int pixelsWritten = 0;
        int x = 0, y = 0;

        while (pixelsWritten < totalPixels)
        {
            if (pixelsWritten % 100 == 0 && token.IsCancellationRequested) return;

            surface.SetPixel(x, y, color);
            pixelsWritten++;
            if (++x >= ci.Width)
            {
                x = 0;
                y++;
            }

            if (pixelsWritten >= totalPixels) break;

            if (ReadBits(src, 1, ref pos, ref bitBuf, ref bitsLeft) == 1)
            {
                if (ReadBits(src, 1, ref pos, ref bitBuf, ref bitsLeft) == 0)
                {
                    // absolute colour
                    color = (byte)ReadBits(src, 8, ref pos, ref bitBuf, ref bitsLeft);
                }
                else
                {
                    // signed 3-bit delta, or run if delta == 0
                    int delta = ReadBits(src, 3, ref pos, ref bitBuf, ref bitsLeft) - 4;

                    if (delta != 0)
                    {
                        // Signed delta in [-4, +3] (0 is the sentinel, not a valid delta)
                        color = (byte)(color + delta);
                    }
                    else
                    {
                        // delta == 0 sentinel: 8-bit run of the current colour
                        int run = ReadBits(src, 8, ref pos, ref bitBuf, ref bitsLeft);
                        while (run-- > 0 && pixelsWritten < totalPixels)
                        {
                            surface.SetPixel(x, y, color);
                            pixelsWritten++;
                            if (++x >= ci.Width)
                            {
                                x = 0;
                                y++;
                            }
                        }
                    }
                }
            }
            // bit 0 → same colour, no change; loop writes it again naturally
        }
    }

    // MSB-first bit reader shared by DecodeMajMin.
    // Refills the buffer a byte at a time from the high end.
    private static int ReadBits(ReadOnlySpan<byte> src, int n, ref int pos, ref uint bitBuf, ref int bitsLeft)
    {
        while (bitsLeft < n)
        {
            bitBuf = (bitBuf << 8) | (pos < src.Length ? src[pos++] : 0u);
            bitsLeft += 8;
        }
        bitsLeft -= n;
        return (int)((bitBuf >> bitsLeft) & ((1u << n) - 1));
    }

    /* backgrounds */

    // =========================================================
    // decodeRoomMajMin
    // =========================================================
    private static void DecodeMajMin(
        ReadOnlySpan<byte> src,
        Span<byte> dst,
        int pitch,
        int height,
        byte codec)
    {
        if (src.Length < 3)
            return;

        int shift = codec - 100;

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

                if (br.Exhausted) return;
            }
        }
    }

    // =========================================================
    // decodeRoomZigZag
    // =========================================================
    private static void DecodeZigZagV(
        ReadOnlySpan<byte> src,
        Span<byte> dst,
        int pitch,
        int height,
        byte codec)
    {
        if (src.Length < 1)
            return;

        int bitsPerPixel = codec - 10;

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
        byte codec)
    {
        if (src.Length < 1)
            return;

        int bitsPerPixel = codec - 0x14;

        if (bitsPerPixel < 0)
            bitsPerPixel = codec - 0x22;

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

    // =========================================================
    // drawStripRaw
    // =========================================================

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


    private ref struct BitReaderLSB(ReadOnlySpan<byte> src, int startIndex = 0)
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
    

    public static readonly Dictionary<int, string> CompressionTypes = new Dictionary<int, string>
    {         
        { 0x01, "BMCOMP_RAW256"           },
        { 0x02, "BMCOMP_TOWNS_2"          },
        { 0x03, "BMCOMP_TOWNS_3"          },
        { 0x04, "BMCOMP_TOWNS_4"          },
        { 0x07, "BMCOMP_TOWNS_7"          },
        { 0x08, "BMCOMP_TRLE8BIT"         },
        { 0x09, "BMCOMP_RLE8BIT"          },
        { 0x0A, "BMCOMP_PIX32"            }, // Also BMCOMP_ZIGZAG_V0
        { 0x0E, "BMCOMP_ZIGZAG_V4"        },
        { 0x0F, "BMCOMP_ZIGZAG_V5"        },
        { 0x10, "BMCOMP_ZIGZAG_V6"        },
        { 0x11, "BMCOMP_ZIGZAG_V7"        },
        { 0x12, "BMCOMP_ZIGZAG_V8"        },
        { 0x14, "BMCOMP_ZIGZAG_H0"        },
        { 0x18, "BMCOMP_ZIGZAG_H4"        },
        { 0x19, "BMCOMP_ZIGZAG_H5"        },
        { 0x1A, "BMCOMP_ZIGZAG_H6"        },
        { 0x1B, "BMCOMP_ZIGZAG_H7"        },
        { 0x1C, "BMCOMP_ZIGZAG_H8"        },
        { 0x1E, "BMCOMP_ZIGZAG_VT0"       },
        { 0x22, "BMCOMP_ZIGZAG_VT4"       },
        { 0x23, "BMCOMP_ZIGZAG_VT5"       },
        { 0x24, "BMCOMP_ZIGZAG_VT6"       },
        { 0x25, "BMCOMP_ZIGZAG_VT7"       },
        { 0x26, "BMCOMP_ZIGZAG_VT8"       },
        { 0x28, "BMCOMP_ZIGZAG_HT0"       },
        { 0x2C, "BMCOMP_ZIGZAG_HT4"       },
        { 0x2D, "BMCOMP_ZIGZAG_HT5"       },
        { 0x2E, "BMCOMP_ZIGZAG_HT6"       },
        { 0x2F, "BMCOMP_ZIGZAG_HT7"       },
        { 0x30, "BMCOMP_ZIGZAG_HT8"       },
        { 0x3C, "BMCOMP_MAJMIN_H0"        },
        { 0x40, "BMCOMP_MAJMIN_H4"        },
        { 0x41, "BMCOMP_MAJMIN_H5"        },
        { 0x42, "BMCOMP_MAJMIN_H6"        },
        { 0x43, "BMCOMP_MAJMIN_H7"        },
        { 0x44, "BMCOMP_MAJMIN_H8"        },
        { 0x50, "BMCOMP_MAJMIN_HT0"       },
        { 0x54, "BMCOMP_MAJMIN_HT4"       },
        { 0x55, "BMCOMP_MAJMIN_HT5"       },
        { 0x56, "BMCOMP_MAJMIN_HT6"       },
        { 0x57, "BMCOMP_MAJMIN_HT7"       },
        { 0x58, "BMCOMP_MAJMIN_HT8"       },
        { 0x64, "BMCOMP_RMAJMIN_H0"       },
        { 0x68, "BMCOMP_RMAJMIN_H4"       },
        { 0x69, "BMCOMP_RMAJMIN_H5"       },
        { 0x6A, "BMCOMP_RMAJMIN_H6"       },
        { 0x6B, "BMCOMP_RMAJMIN_H7"       },
        { 0x6C, "BMCOMP_RMAJMIN_H8"       },
        { 0x78, "BMCOMP_RMAJMIN_HT0"      },
        { 0x7C, "BMCOMP_RMAJMIN_HT4"      },
        { 0x7D, "BMCOMP_RMAJMIN_HT5"      },
        { 0x7E, "BMCOMP_RMAJMIN_HT6"      },
        { 0x7F, "BMCOMP_RMAJMIN_HT7"      },
        { 0x80, "BMCOMP_RMAJMIN_HT8"      },
        { 0x82, "BMCOMP_NMAJMIN_H0"       },
        { 0x86, "BMCOMP_NMAJMIN_H4"       },
        { 0x87, "BMCOMP_NMAJMIN_H5"       },
        { 0x88, "BMCOMP_NMAJMIN_H6"       },
        { 0x89, "BMCOMP_NMAJMIN_H7"       },
        { 0x8A, "BMCOMP_NMAJMIN_H8"       },
        { 0x8C, "BMCOMP_NMAJMIN_HT0"      },
        { 0x90, "BMCOMP_NMAJMIN_HT4"      },
        { 0x91, "BMCOMP_NMAJMIN_HT5"      },
        { 0x92, "BMCOMP_NMAJMIN_HT6"      },
        { 0x93, "BMCOMP_NMAJMIN_HT7"      },
        { 0x94, "BMCOMP_NMAJMIN_HT8"      },
        { 0x95, "BMCOMP_TPIX256"          },
        { 0x96, "BMCOMP_SOLID_COLOR_FILL" },
        { 0x8F, "BMCOMP_CUSTOM_RU_TR"     }
    };

}