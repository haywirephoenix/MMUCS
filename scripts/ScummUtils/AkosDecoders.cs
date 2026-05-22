
using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;
public static class AkosDecoders
{
    // Decode a single cel from AKCD using the costume's codec
    public static IndexedSurface DecodeCel(AkosData _akos, int celIndex, CancellationToken token)
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
            case AkosCodec.ByleRLE: // 1
                _DecodeByleRLE(_akos.CelData.Span, _akos.AkplSize, surface, ci, offset, token);

                break;
            case AkosCodec.CdatRLE: // 5
                DecodeCDAT(_akos.CelData.Span, surface, ci, offset, token);

                break;
            case AkosCodec.MajMin: // 16
                DecodeMajMinAkos(_akos.CelData.Span, surface, ci, offset, token);
                // _StubDecode(surface, new Color(0.4f, 0.2f, 0.8f, 0.5f));
                break;
            case AkosCodec.TRLE: // 32
                ScummDecoders.StubDecode(surface, new Color(0.8f, 0.4f, 0.2f, 0.5f));
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
    public static void DecodeMajMinAkos(ReadOnlySpan<byte> src, IndexedSurface surface, AkosCelInfo ci, AkosOffset offset, CancellationToken token)
    {
        int pos = (int)offset.AkcdOffset + 1; // skip sourceShift

        var reader = new ScummDecoders.BitReaderLSB(src, pos);

        int totalPixels = ci.Width * ci.Height;

        byte color = (byte)reader.ReadBits(8);

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

            if (reader.ReadBit() == 1)
            {
                if (reader.ReadBit() == 0)
                {
                    // absolute colour
                    color = (byte)reader.ReadBits(8);
                }
                else
                {
                    // signed 3-bit delta, or run if delta == 0
                    int delta = reader.ReadBits(3) - 4;

                    if (delta != 0)
                    {
                        // Signed delta in [-4, +3] (0 is the sentinel, not a valid delta)
                        color = (byte)(color + delta);
                    }
                    else
                    {
                        // delta == 0 sentinel: 8-bit run of the current colour
                        int run = reader.ReadBits(8);
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
    
}