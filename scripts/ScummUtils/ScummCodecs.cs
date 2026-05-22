using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
public static class ScummCodecs
{
    
    public enum ECodec
    {
        Raw,
        RawT,
        ZigZagV,
        ZigZagVT,
        ZigZagH,
        ZigZagHT,
        MajMin,
        MajMinT,
        MajMinHE,
        MajMinHET,
        UNKNOWN,
    }
    
    
    public static string EcodecToName(ECodec eCodec)
    {
        switch (eCodec)
        {
            case ECodec.Raw: return "Raw";
            case ECodec.RawT: return "RawT";
            case ECodec.ZigZagV:  return "ZigZagV";
            case ECodec.ZigZagVT:  return "ZigZagT";
            case ECodec.ZigZagH: return "ZigZagH";
            case ECodec.ZigZagHT: return "ZigZagHT";
            case ECodec.MajMin: return "MajMin";
            case ECodec.MajMinT: return "MajMinT";
            // case ECodec.MajMinHE: DecodeMajMinHE(src, dst, pitch, height, codec, false); break;
            // case ECodec.MajMinHET: DecodeMajMinHE(src, dst, pitch, height, codec, true); break;
            case ECodec.UNKNOWN:
            default: return "UNKNOWN";
        }
    }

    public static ECodec ByteToECodec(this byte codec)
    {
        switch (codec)
        {

            case BMCOMP_RAW256: return ECodec.Raw;
            // DecodeRaw(src, dst, pitch, height, codec, false);
            /*
            case BMCOMP_TOWNS_2:
                unkDecode8(src, pitch, src, numLinesToProcess);       // Ender - Zak256/Indy256
                break;

            case BMCOMP_TOWNS_3:
                unkDecode9(src, pitch, src, numLinesToProcess);       // Ender - Zak256/Indy256
                break;

            case BMCOMP_TOWNS_4:
                unkDecode10(src, pitch, src, numLinesToProcess);      // Ender - Zak256/Indy256
                break;

            case BMCOMP_TOWNS_7:
                unkDecode11(src, pitch, src, numLinesToProcess);      // Ender - Zak256/Indy256
                break;

            case BMCOMP_TRLE8BIT:
                // Used in 3DO versions of HE games
                transpStrip = true;
                drawStrip3DO(src, pitch, src, numLinesToProcess, true);
                break;

            case BMCOMP_RLE8BIT:
                drawStrip3DO(src, pitch, src, numLinesToProcess, false);
                break;

            case BMCOMP_PIX32:
                // Used in Amiga version of Monkey Island 1
                drawStripEGA(src, pitch, src, numLinesToProcess);
                break;*/

            case BMCOMP_ZIGZAG_V4:
            case BMCOMP_ZIGZAG_V5:
            case BMCOMP_ZIGZAG_V6:
            case BMCOMP_ZIGZAG_V7:
            case BMCOMP_ZIGZAG_V8:
                return ECodec.ZigZagV;
            // DecodeZigZagV(src, dst, pitch, height, codec, false);
            // drawStripBasicV(src, pitch, src, numLinesToProcess, false);

            case BMCOMP_ZIGZAG_H4:
            case BMCOMP_ZIGZAG_H5:
            case BMCOMP_ZIGZAG_H6:
            case BMCOMP_ZIGZAG_H7:
            case BMCOMP_ZIGZAG_H8:
                return ECodec.ZigZagH;
            // DecodeZigZagH(src, dst, pitch, height, codec, false);
            // drawStripBasicH(src, pitch, src, numLinesToProcess, false);

            case BMCOMP_ZIGZAG_VT4:
            case BMCOMP_ZIGZAG_VT5:
            case BMCOMP_ZIGZAG_VT6:
            case BMCOMP_ZIGZAG_VT7:
            case BMCOMP_ZIGZAG_VT8:
                return ECodec.ZigZagVT;
            // DecodeZigZagV(src, dst, pitch, height, codec,true);
            // drawStripBasicV(dst, dstPitch, src, numLinesToProcess, true);

            case BMCOMP_ZIGZAG_HT4:
            case BMCOMP_ZIGZAG_HT5:
            case BMCOMP_ZIGZAG_HT6:
            case BMCOMP_ZIGZAG_HT7:
            case BMCOMP_ZIGZAG_HT8:
                return ECodec.ZigZagHT;
            // DecodeZigZagH(src, dst, pitch, height, codec, true);
            // drawStripBasicH(src, pitch, src, numLinesToProcess, true);

            case BMCOMP_MAJMIN_H4:
            case BMCOMP_MAJMIN_H5:
            case BMCOMP_MAJMIN_H6:
            case BMCOMP_MAJMIN_H7:
            case BMCOMP_MAJMIN_H8:
            case BMCOMP_RMAJMIN_H4:
            case BMCOMP_RMAJMIN_H5:
            case BMCOMP_RMAJMIN_H6:
            case BMCOMP_RMAJMIN_H7:
            case BMCOMP_RMAJMIN_H8:
                return ECodec.MajMin;
            // DecodeMajMin(src, dst, pitch, height, codec, false);
            // drawStripComplex(src, pitch, src, numLinesToProcess, false);

            case BMCOMP_MAJMIN_HT4:
            case BMCOMP_MAJMIN_HT5:
            case BMCOMP_MAJMIN_HT6:
            case BMCOMP_MAJMIN_HT7:
            case BMCOMP_MAJMIN_HT8:
            case BMCOMP_RMAJMIN_HT4:
            case BMCOMP_RMAJMIN_HT5:
            case BMCOMP_RMAJMIN_HT6:
            case BMCOMP_RMAJMIN_HT7:
            case BMCOMP_RMAJMIN_HT8:
                return ECodec.MajMinT;
            // DecodeMajMin(src, dst, pitch, height, codec, true);
            // drawStripComplex(src, pitch, src, numLinesToProcess, true);

            case BMCOMP_NMAJMIN_H4:
            case BMCOMP_NMAJMIN_H5:
            case BMCOMP_NMAJMIN_H6:
            case BMCOMP_NMAJMIN_H7:
            case BMCOMP_NMAJMIN_H8:
                return ECodec.MajMinHE;
            // DecodeMajMinHE(src, dst, pitch, height, codec, false);
            // drawStripHE(src, pitch, src, 8, numLinesToProcess, false);

            case BMCOMP_CUSTOM_RU_TR: // Triggered by Russian water
            case BMCOMP_NMAJMIN_HT4:
            case BMCOMP_NMAJMIN_HT5:
            case BMCOMP_NMAJMIN_HT6:
            case BMCOMP_NMAJMIN_HT7:
            case BMCOMP_NMAJMIN_HT8:
                return ECodec.MajMinHET;
            // DecodeMajMinHE(src, dst, pitch, height, codec, true);
            // drawStripHE(src, pitch, src, 8, numLinesToProcess, true);

            case BMCOMP_TPIX256:
                return ECodec.RawT;
            // DecodeRaw(src, dst, pitch, height, codec, true);
            // drawStripRaw(src, pitch, src, numLinesToProcess, true);
            default: return ECodec.UNKNOWN;
        }
    }

    public static void LogMissingCodec(byte codec)
    {
        GD.PrintErr($"missing codec {codec.ByteToCodecName()}");
    }
    
    
    public const byte BMCOMP_RAW256           = 0x01;
    public const byte BMCOMP_TOWNS_2          = 0x02;
    public const byte BMCOMP_TOWNS_3          = 0x03;
    public const byte BMCOMP_TOWNS_4          = 0x04;
    public const byte BMCOMP_TOWNS_7          = 0x07;
    public const byte BMCOMP_TRLE8BIT         = 0x08;
    public const byte BMCOMP_RLE8BIT          = 0x09;
    public const byte BMCOMP_PIX32            = 0x0A;
    public const byte BMCOMP_ZIGZAG_V4        = 0x0E;
    public const byte BMCOMP_ZIGZAG_V5        = 0x0F;
    public const byte BMCOMP_ZIGZAG_V6        = 0x10;
    public const byte BMCOMP_ZIGZAG_V7        = 0x11;
    public const byte BMCOMP_ZIGZAG_V8        = 0x12;
    public const byte BMCOMP_ZIGZAG_H0        = 0x14;
    public const byte BMCOMP_ZIGZAG_H4        = 0x18;
    public const byte BMCOMP_ZIGZAG_H5        = 0x19;
    public const byte BMCOMP_ZIGZAG_H6        = 0x1A;
    public const byte BMCOMP_ZIGZAG_H7        = 0x1B;
    public const byte BMCOMP_ZIGZAG_H8        = 0x1C;
    public const byte BMCOMP_ZIGZAG_VT0       = 0x1E;
    public const byte BMCOMP_ZIGZAG_VT4       = 0x22;
    public const byte BMCOMP_ZIGZAG_VT5       = 0x23;
    public const byte BMCOMP_ZIGZAG_VT6       = 0x24;
    public const byte BMCOMP_ZIGZAG_VT7       = 0x25;
    public const byte BMCOMP_ZIGZAG_VT8       = 0x26;
    public const byte BMCOMP_ZIGZAG_HT0       = 0x28;
    public const byte BMCOMP_ZIGZAG_HT4       = 0x2C;
    public const byte BMCOMP_ZIGZAG_HT5       = 0x2D;
    public const byte BMCOMP_ZIGZAG_HT6       = 0x2E;
    public const byte BMCOMP_ZIGZAG_HT7       = 0x2F;
    public const byte BMCOMP_ZIGZAG_HT8       = 0x30;
    public const byte BMCOMP_MAJMIN_H0        = 0x3C;
    public const byte BMCOMP_MAJMIN_H4        = 0x40;
    public const byte BMCOMP_MAJMIN_H5        = 0x41;
    public const byte BMCOMP_MAJMIN_H6        = 0x42;
    public const byte BMCOMP_MAJMIN_H7        = 0x43;
    public const byte BMCOMP_MAJMIN_H8        = 0x44;
    public const byte BMCOMP_MAJMIN_HT0       = 0x50;
    public const byte BMCOMP_MAJMIN_HT4       = 0x54;
    public const byte BMCOMP_MAJMIN_HT5       = 0x55;
    public const byte BMCOMP_MAJMIN_HT6       = 0x56;
    public const byte BMCOMP_MAJMIN_HT7       = 0x57;
    public const byte BMCOMP_MAJMIN_HT8       = 0x58;
    public const byte BMCOMP_RMAJMIN_H0       = 0x64;
    public const byte BMCOMP_RMAJMIN_H4       = 0x68;
    public const byte BMCOMP_RMAJMIN_H5       = 0x69;
    public const byte BMCOMP_RMAJMIN_H6       = 0x6A;
    public const byte BMCOMP_RMAJMIN_H7       = 0x6B;
    public const byte BMCOMP_RMAJMIN_H8       = 0x6C;
    public const byte BMCOMP_RMAJMIN_HT0      = 0x78;
    public const byte BMCOMP_RMAJMIN_HT4      = 0x7C;
    public const byte BMCOMP_RMAJMIN_HT5      = 0x7D;
    public const byte BMCOMP_RMAJMIN_HT6      = 0x7E;
    public const byte BMCOMP_RMAJMIN_HT7      = 0x7F;
    public const byte BMCOMP_RMAJMIN_HT8      = 0x80;
    public const byte BMCOMP_NMAJMIN_H0       = 0x82;
    public const byte BMCOMP_NMAJMIN_H4       = 0x86;
    public const byte BMCOMP_NMAJMIN_H5       = 0x87;
    public const byte BMCOMP_NMAJMIN_H6       = 0x88;
    public const byte BMCOMP_NMAJMIN_H7       = 0x89;
    public const byte BMCOMP_NMAJMIN_H8       = 0x8A;
    public const byte BMCOMP_NMAJMIN_HT0      = 0x8C;
    public const byte BMCOMP_NMAJMIN_HT4      = 0x90;
    public const byte BMCOMP_NMAJMIN_HT5      = 0x91;
    public const byte BMCOMP_NMAJMIN_HT6      = 0x92;
    public const byte BMCOMP_NMAJMIN_HT7      = 0x93;
    public const byte BMCOMP_NMAJMIN_HT8      = 0x94;
    public const byte BMCOMP_TPIX256          = 0x95;
    public const byte BMCOMP_SOLID_COLOR_FILL = 0x96;
    public const byte BMCOMP_CUSTOM_RU_TR     = 0x8F;

    public static string ByteToCodecName(this byte codec)
    {
        return CompressionTypes.TryGetValue(codec, out string codecName) 
            ? codecName 
            : $"Unknown codec 0x{codec:X2}";
    }

    // 3. If you need to find the byte by its string name:
    public static byte CodecToByte(this string codecName)
    {
        var pair = CompressionTypes.FirstOrDefault(x => x.Value == codecName);
        if (pair.Value != null)
        {
            return pair.Key;
        }
        throw new ArgumentException($"Unknown codec name: {codecName}");
    }

    private static readonly Dictionary<byte, string> CompressionTypes = new()
    {
        {
            BMCOMP_RAW256           , "BMCOMP_RAW256" },
        {
            BMCOMP_TOWNS_2          , "BMCOMP_TOWNS_2" },
        {
            BMCOMP_TOWNS_3          , "BMCOMP_TOWNS_3" },
        {
            BMCOMP_TOWNS_4          , "BMCOMP_TOWNS_4" },
        {
            BMCOMP_TOWNS_7          , "BMCOMP_TOWNS_7" },
        {
            BMCOMP_TRLE8BIT         , "BMCOMP_TRLE8BIT" },
        {
            BMCOMP_RLE8BIT          , "BMCOMP_RLE8BIT" },
        {
            BMCOMP_PIX32            , "BMCOMP_PIX32" }, // Also BMCOMP_ZIGZAG_V0
        {
            BMCOMP_ZIGZAG_V4        , "BMCOMP_ZIGZAG_V4" },
        {
            BMCOMP_ZIGZAG_V5        , "BMCOMP_ZIGZAG_V5" },
        {
            BMCOMP_ZIGZAG_V6        , "BMCOMP_ZIGZAG_V6" },
        {
            BMCOMP_ZIGZAG_V7        , "BMCOMP_ZIGZAG_V7" },
        {
            BMCOMP_ZIGZAG_V8        , "BMCOMP_ZIGZAG_V8" },
        {
            BMCOMP_ZIGZAG_H0        , "BMCOMP_ZIGZAG_H0" },
        {
            BMCOMP_ZIGZAG_H4        , "BMCOMP_ZIGZAG_H4" },
        {
            BMCOMP_ZIGZAG_H5        , "BMCOMP_ZIGZAG_H5" },
        {
            BMCOMP_ZIGZAG_H6        , "BMCOMP_ZIGZAG_H6" },
        {
            BMCOMP_ZIGZAG_H7        , "BMCOMP_ZIGZAG_H7" },
        {
            BMCOMP_ZIGZAG_H8        , "BMCOMP_ZIGZAG_H8" },
        {
            BMCOMP_ZIGZAG_VT0       , "BMCOMP_ZIGZAG_VT0" },
        {
            BMCOMP_ZIGZAG_VT4       , "BMCOMP_ZIGZAG_VT4" },
        {
            BMCOMP_ZIGZAG_VT5       , "BMCOMP_ZIGZAG_VT5" },
        {
            BMCOMP_ZIGZAG_VT6       , "BMCOMP_ZIGZAG_VT6" },
        {
            BMCOMP_ZIGZAG_VT7       , "BMCOMP_ZIGZAG_VT7" },
        {
            BMCOMP_ZIGZAG_VT8       , "BMCOMP_ZIGZAG_VT8" },
        {
            BMCOMP_ZIGZAG_HT0       , "BMCOMP_ZIGZAG_HT0" },
        {
            BMCOMP_ZIGZAG_HT4       , "BMCOMP_ZIGZAG_HT4" },
        {
            BMCOMP_ZIGZAG_HT5       , "BMCOMP_ZIGZAG_HT5" },
        {
            BMCOMP_ZIGZAG_HT6       , "BMCOMP_ZIGZAG_HT6" },
        {
            BMCOMP_ZIGZAG_HT7       , "BMCOMP_ZIGZAG_HT7" },
        {
            BMCOMP_ZIGZAG_HT8       , "BMCOMP_ZIGZAG_HT8" },
        {
            BMCOMP_MAJMIN_H0        , "BMCOMP_MAJMIN_H0" },
        {
            BMCOMP_MAJMIN_H4        , "BMCOMP_MAJMIN_H4" },
        {
            BMCOMP_MAJMIN_H5        , "BMCOMP_MAJMIN_H5" },
        {
            BMCOMP_MAJMIN_H6        , "BMCOMP_MAJMIN_H6" },
        {
            BMCOMP_MAJMIN_H7        , "BMCOMP_MAJMIN_H7" },
        {
            BMCOMP_MAJMIN_H8        , "BMCOMP_MAJMIN_H8" },
        {
            BMCOMP_MAJMIN_HT0       , "BMCOMP_MAJMIN_HT0" },
        {
            BMCOMP_MAJMIN_HT4       , "BMCOMP_MAJMIN_HT4" },
        {
            BMCOMP_MAJMIN_HT5       , "BMCOMP_MAJMIN_HT5" },
        {
            BMCOMP_MAJMIN_HT6       , "BMCOMP_MAJMIN_HT6" },
        {
            BMCOMP_MAJMIN_HT7       , "BMCOMP_MAJMIN_HT7" },
        {
            BMCOMP_MAJMIN_HT8       , "BMCOMP_MAJMIN_HT8" },
        {
            BMCOMP_RMAJMIN_H0       , "BMCOMP_RMAJMIN_H0" },
        {
            BMCOMP_RMAJMIN_H4       , "BMCOMP_RMAJMIN_H4" },
        {
            BMCOMP_RMAJMIN_H5       , "BMCOMP_RMAJMIN_H5" },
        {
            BMCOMP_RMAJMIN_H6       , "BMCOMP_RMAJMIN_H6" },
        {
            BMCOMP_RMAJMIN_H7       , "BMCOMP_RMAJMIN_H7" },
        {
            BMCOMP_RMAJMIN_H8       , "BMCOMP_RMAJMIN_H8" },
        {
            BMCOMP_RMAJMIN_HT0      , "BMCOMP_RMAJMIN_HT0" },
        {
            BMCOMP_RMAJMIN_HT4      , "BMCOMP_RMAJMIN_HT4" },
        {
            BMCOMP_RMAJMIN_HT5      , "BMCOMP_RMAJMIN_HT5" },
        {
            BMCOMP_RMAJMIN_HT6      , "BMCOMP_RMAJMIN_HT6" },
        {
            BMCOMP_RMAJMIN_HT7      , "BMCOMP_RMAJMIN_HT7" },
        {
            BMCOMP_RMAJMIN_HT8      , "BMCOMP_RMAJMIN_HT8" },
        {
            BMCOMP_NMAJMIN_H0       , "BMCOMP_NMAJMIN_H0" },
        {
            BMCOMP_NMAJMIN_H4       , "BMCOMP_NMAJMIN_H4" },
        {
            BMCOMP_NMAJMIN_H5       , "BMCOMP_NMAJMIN_H5" },
        {
            BMCOMP_NMAJMIN_H6       , "BMCOMP_NMAJMIN_H6" },
        {
            BMCOMP_NMAJMIN_H7       , "BMCOMP_NMAJMIN_H7" },
        {
            BMCOMP_NMAJMIN_H8       , "BMCOMP_NMAJMIN_H8" },
        {
            BMCOMP_NMAJMIN_HT0      , "BMCOMP_NMAJMIN_HT0" },
        {
            BMCOMP_NMAJMIN_HT4      , "BMCOMP_NMAJMIN_HT4" },
        {
            BMCOMP_NMAJMIN_HT5      , "BMCOMP_NMAJMIN_HT5" },
        {
            BMCOMP_NMAJMIN_HT6      , "BMCOMP_NMAJMIN_HT6" },
        {
            BMCOMP_NMAJMIN_HT7      , "BMCOMP_NMAJMIN_HT7" },
        {
            BMCOMP_NMAJMIN_HT8      , "BMCOMP_NMAJMIN_HT8" },
        {
            BMCOMP_TPIX256          , "BMCOMP_TPIX256" },
        {
            BMCOMP_SOLID_COLOR_FILL , "BMCOMP_SOLID_COLOR_FILL" },
        {
            BMCOMP_CUSTOM_RU_TR     , "BMCOMP_CUSTOM_RU_TR"
        }
    };
    
    public ref struct MajMinCodec
    {
        private bool _repeatMode;
        private int _repeatCount;
        private byte _shift;
        private byte _color;
        private ScummDecoders.BitReaderLSB _br;

        public void Setup(byte shift, ReadOnlySpan<byte> src)
        {
            _repeatMode = false;
            _repeatCount = 0;
            _shift = shift;
        
            // Grab the initial baseline color (*src)
            _color = src[0]; 
        
            // Feed the rest of the stream into your standard BitReaderLSB (src + 1)
            _br = new ScummDecoders.BitReaderLSB(src.Slice(1)); 
        }

        public void DecodeLine(Span<byte> lineBuffer, int width)
        {
            for (int i = 0; i < width; i++)
            {
                if (!_repeatMode)
                {
                    if (_br.ReadBit() != 0)
                    {
                        if (_br.ReadBit() != 0)
                        {
                            int diff = _br.ReadBits(3) - 4;
                            if (diff != 0)
                            {
                                _color = (byte)(_color + diff);
                            }
                            else
                            {
                                _repeatMode = true;
                                _repeatCount = _br.ReadBits(8) - 1;
                                if (_repeatCount < 0) _repeatCount = 0;
                            }
                        }
                        else
                        {
                            _color = (byte)_br.ReadBits(_shift);
                        }
                    }
                }
                else
                {
                    _repeatCount--;
                    if (_repeatCount <= 0) 
                        _repeatMode = false;
                }

                // Store the result in the line history buffer
                lineBuffer[i] = _color;

                if (_br.Exhausted) 
                    return;
            }
        }
    }
}