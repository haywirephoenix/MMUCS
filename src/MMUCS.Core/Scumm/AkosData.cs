using System;
using System.Collections.Generic;
using MMUCS.Core.Diagnostics;
using MMUCS.Core.Imaging;

namespace MMUCS.Core.Scumm;


/// <summary>
/// Parsed representation of an AKOS costume resource.
/// Mirrors the C++ structs and data pointers in akos.cpp.
/// </summary>
public class AkosData
{
    public long Offset;
    /// <summary><see cref="ScummBlock.SourceKey"/> of the AKOS block's file.</summary>
    public long SourceKey;
    public bool IsPassthroughPalette =>
        !Palette.IsEmpty && Palette.Span[0] == 0xFF;
    
    public int DefaultPaletteIndex { get; set; } = 0;

    public byte TransparentIndex => IsPassthroughPalette ? (byte)255 : (byte)0;
    // ── Data Blobs (Memory views) ────────────────────────────────────────────
    public ReadOnlyMemory<byte> CelInfoRaw;
    public ReadOnlyMemory<byte> Palette;
    public ReadOnlyMemory<byte> CelData;
    public ReadOnlyMemory<byte> Sequence;
    public ReadOnlyMemory<byte> Rgbs;
    /// <summary>Raw AKCH: chore offset table followed by each chore's limb definitions.</summary>
    public ReadOnlyMemory<byte> ChoreData;

    // ── Header & Metadata ────────────────────────────────────────────────────
    public AkosHeader Header;
    public uint AkplSize;
    public ScummColor[] ResolvedColors = Array.Empty<ScummColor>();
    
    // ── Tables (Parsed upfront) ──────────────────────────────────────────────
    public AkosOffset[] CelOffsets = Array.Empty<AkosOffset>();
    public AkosCelInfo[] CelInfos = Array.Empty<AkosCelInfo>();
    public ushort[] ChoreOffsets = Array.Empty<ushort>(); // From AKCH
    public ushort[] FrameOffsets = Array.Empty<ushort>(); // From AKFO
    
    // ── Lazy-loaded data ─────────────────────────────────────────────────────
    // public readonly Dictionary<int, RgbaImage> DecodedCels = new();
    public readonly Dictionary<long, IndexedSurface> DecodedCels = new();

    // ── Calculated Properties ────────────────────────────────────────────────
    public int DirectionCount => (Header.CostumeFlags & 2) != 0 ? 8 : 4;
    
    // Logic: ChoreCount is (Anims * Directions). 
    // We use Math.Max to avoid division by zero if a block is malformed.
    public int AnimCount => DirectionCount > 0 ? Header.ChoreCount / DirectionCount : 0;
}

public struct AkosHeader
{
    public ushort VersionNumber;
    public ushort CostumeFlags;
    public ushort ChoreCount;      // total chore entries (anims × directions)
    public ushort CelsCount;
    public ushort CelCompressionCodec;
    public ushort LayerCount;

    public bool HasManyDirections => (CostumeFlags & 2) != 0;
    public bool MirroredCostume   => (CostumeFlags & 1) != 0;

    public AkosCodec Codec => (AkosCodec)CelCompressionCodec;
}

public struct AkosOffset
{
    public uint AkcdOffset;   // byte offset into AKCD blob
    public ushort AkciOffset; // byte offset into AKCI blob
}

public struct AkosCelInfo
{
    public ushort Width;
    public ushort Height;
    public short  RelX;    // draw position relative to actor origin
    public short  RelY;
    public short  MoveX;   // advance actor origin after drawing
    public short  MoveY;
    public bool   is640Width;
}

public enum AkosCodec : ushort
{
    ByleRLE  = 1,   // AKOS_BYLE_RLE_CODEC
    CdatRLE  = 5,   // AKOS_CDAT_RLE_CODEC  (BOMP)
    MajMin   = 16,   // AKOS_RUN_MAJMIN_CODEC
    TRLE     = 32,  // AKOS_TRLE_CODEC (HE)
}

public struct CelExportConfig
{
    public bool AlphaEnabled { get; set; } = true;
    public bool ShadowColorEnabled { get; set; } = true;
    public float ShadowAmount { get; set; } = 0.4f;
    public bool UseLinearFilter { get; set; } = false;
    public CelExportConfig() {}
}
