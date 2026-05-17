using System;
using Godot;
using System.Collections.Generic;

/// <summary>
/// Parsed representation of an AKOS costume resource.
/// Mirrors the C++ structs and data pointers in akos.cpp.
/// </summary>
public class AkosData
{
    public long Offset;
    public bool IsPassthroughPalette =>
        !Palette.IsEmpty && Palette.Span[0] == 0xFF;

    public byte TransparentIndex => IsPassthroughPalette ? (byte)255 : (byte)0;
    // ── Data Blobs (Memory views) ────────────────────────────────────────────
    public ReadOnlyMemory<byte> CelInfoRaw;
    public ReadOnlyMemory<byte> Palette;
    public ReadOnlyMemory<byte> CelData;
    public ReadOnlyMemory<byte> Sequence;
    public ReadOnlyMemory<byte> Rgbs;

    // ── Header & Metadata ────────────────────────────────────────────────────
    public AkosHeader Header;
    public uint AkplSize;
    public Color[] ResolvedColors = Array.Empty<Color>();
    
    // ── Tables (Parsed upfront) ──────────────────────────────────────────────
    public AkosOffset[] CelOffsets = Array.Empty<AkosOffset>();
    public AkosCelInfo[] CelInfos = Array.Empty<AkosCelInfo>();
    public ushort[] ChoreOffsets = Array.Empty<ushort>(); // From AKCH
    public ushort[] FrameOffsets = Array.Empty<ushort>(); // From AKFO
    
    // ── Lazy-loaded data ─────────────────────────────────────────────────────
    public List<AkosChore> Chores; 
    // public readonly Dictionary<int, Image> DecodedCels = new();
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
    public short RelX;    // draw position relative to actor origin
    public short RelY;
    public short MoveX;   // advance actor origin after drawing
    public short MoveY;
}

public enum AkosCodec : ushort
{
    ByleRLE  = 1,   // AKOS_BYLE_RLE_CODEC
    CdatRLE  = 5,   // AKOS_CDAT_RLE_CODEC  (BOMP)
    MajMin   = 16,   // AKOS_RUN_MAJMIN_CODEC
    TRLE     = 32,  // AKOS_TRLE_CODEC (HE)
}

/// <summary>
/// A single resolved chore (animation) with its per-direction sequence offsets.
/// </summary>
public struct AkosChore()
{
    public int AnimIndex = 0;
    public string DisplayName = null;
    public List<AkosChoreDirection> Directions = new();
}

public struct AkosChoreDirection()
{
    public int DirIndex = 0;           // 0-3 or 0-7
    public ushort SequenceOffset = 0;  // into AKSQ
    public List<AkosChoreStep> Steps = new();
}

/// <summary>
/// A decoded step inside a chore sequence — either a draw command or a control op.
/// </summary>
public struct AkosChoreStep()
{
    public int Offset = 0;
    public AkosStepKind Kind = AkosStepKind.DrawSingle;
    public int CelIndex = 0;       // valid when Kind == DrawSingle / DrawMany
    public readonly List<int> MultiCels = new();
    public readonly string OpcodeName = null;  // for non-draw control ops
    public ushort RawCode = 0;
}

public enum AkosStepKind { DrawSingle, DrawMany, Control, Empty, End }
