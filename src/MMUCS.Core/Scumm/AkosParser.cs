using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using MMUCS.Core.Diagnostics;
using MMUCS.Core.Imaging;

namespace MMUCS.Core.Scumm;


public class AkosParser
{
    /// <summary>
    /// Parses the AKOS block structure using ReadOnlyMemory to avoid redundant array allocations.
    /// This version is non-blocking and safe to run in Task.Run.
    /// </summary>
    public AkosData Parse(ScummBlock akosBlock, ScummColor[] globalPalette = null)
    {
        var data = new AkosData();
        data.Offset = akosBlock.Offset;
        data.SourceKey = akosBlock.SourceKey;

        var roomBlock = akosBlock.Parent.FindChild(ScummTag.ROOM);
        var apals = roomBlock.FindChildrenRecursive(ScummTag.APAL);
        ScummBlock apal = apals[0];

        //edge case for 77.skulisle - change to width based or lookup later
        if (data.Offset == 61970978) //data.Offset == 0x03B19A22
        {
            data.DefaultPaletteIndex = 1;
            apal                     = apals[1];
        }
        else
        {
            data.DefaultPaletteIndex = 0;
        }

        if (apal != null)
            globalPalette = ScummDecoders.ExtractPalette(apal);

        var ahkd = akosBlock.FindChild(ScummTag.AKHD);
        var akpl = akosBlock.FindChild(ScummTag.AKPL);
        var akci = akosBlock.FindChild(ScummTag.AKCI);
        var akcd = akosBlock.FindChild(ScummTag.AKCD);
        var akof = akosBlock.FindChild(ScummTag.AKOF);
        var aksq = akosBlock.FindChild(ScummTag.AKSQ);
        var akch = akosBlock.FindChild(ScummTag.AKCH); // Choreography
        var akfo = akosBlock.FindChild(ScummTag.AKFO); // Frame Offsets
        var rgbs = akosBlock.FindChild(ScummTag.RGBS);
        
        if (ahkd != null) _ParseAKHD(data, ahkd);

        // Data Blobs
                                                
        // if (akci != null) data.CelInfoRaw = akci.DataSpan.ToArray().AsMemory();
        // if (akcd != null) data.CelData = akcd.DataSpan.ToArray().AsMemory();
        // if (aksq != null) data.Sequence = aksq.DataSpan.ToArray().AsMemory();
        // if (rgbs != null) data.Rgbs = rgbs.DataSpan.ToArray().AsMemory();
        
        if (akci != null) data.CelInfoRaw = akci.DataMemory;
        if (akcd != null) data.CelData    = akcd.DataMemory;
        if (aksq != null) data.Sequence   = aksq.DataMemory;
        if (rgbs != null) data.Rgbs       = rgbs.DataMemory;
        
        if (akpl != null)
        {
            data.Palette = akpl.DataMemory;
            data.AkplSize = (uint)akpl.DataLength;
        }

        if (akof != null) _ParseAKOF(data, akof);
        if (akci != null) _ParseAKCI(data, akci);

        if (akch != null) data.ChoreOffsets = akch.DataSpan.ReadU16Table();
        if (akfo != null) data.FrameOffsets = akfo.DataSpan.ReadU16Table();

        _ResolvePalette(data, globalPalette);

        return data;
    }
    
    public void UpdatePaletteOnly(AkosData data, ScummBlock akosBlock, int paletteIndex)
    {
        ScummColor[] targetPalette = null;

        var roomBlock = akosBlock.Parent?.FindChild(ScummTag.ROOM);
        if (roomBlock != null)
        {
            var palsBlock = roomBlock.FindChild(ScummTag.PALS);
            var wrapBlock = palsBlock?.FindChild(ScummTag.WRAP);
            var apals = wrapBlock?.FindChildrenRecursive(ScummTag.APAL);

            if (apals != null && apals.Count > 0)
            {
                var chosenApal = apals[Math.Clamp(paletteIndex, 0, apals.Count - 1)];
                targetPalette = ScummDecoders.ExtractPalette(chosenApal);
            }
        }

        _ResolvePalette(data, targetPalette);
    }
    
    private void _ResolvePalette(AkosData data, ScummColor[] globalPalette)
    {
        data.ResolvedColors = new ScummColor[256];
        // for (int i = 0; i < 256; i++) data.ResolvedColors[i] = new ScummColor(1, 0, 1, 1); // debug pink

        if (data.IsPassthroughPalette)
        {
            // AKCD bytes index the room palette directly — copy it straight through
            if (globalPalette != null)
            {
                int limit = Math.Min(256, globalPalette.Length);
                for (int i = 0; i < limit; i++)
                    data.ResolvedColors[i] = globalPalette[i];
            }
            // Transparent index for passthrough costumes is always 255
            data.ResolvedColors[255] = new ScummColor(0, 0, 0, 0);
            return;
        }

        // --- Local-index costume ---
        var akplSpan = data.Palette.Span;

        if (!data.Rgbs.IsEmpty)
        {
            // RGBS present: local index i → direct RGB values.
            // Map into the global slots so the shader finds them at globalIdx.
            var rgbSpan = data.Rgbs.Span;
            for (int i = 0; i < akplSpan.Length; i++)
            {
                int globalIdx = akplSpan[i];
                int rIdx      = i * 3;
                if (rIdx + 2 < rgbSpan.Length)
                {
                    data.ResolvedColors[globalIdx] = new ScummColor(
                        rgbSpan[rIdx    ] / 255f,
                        rgbSpan[rIdx + 1] / 255f,
                        rgbSpan[rIdx + 2] / 255f,
                        1.0f);
                }
            }
        }
        else if (globalPalette != null)
        {
            // No RGBS: fall back to the room palette
            int limit = Math.Min(256, globalPalette.Length);
            for (int i = 0; i < limit; i++)
                data.ResolvedColors[i] = globalPalette[i];
        }

        // Slot 0 is always transparent for local-index costumes
        data.ResolvedColors[0] = new ScummColor(0, 0, 0, 0);
    }

    private void _ParseAKHD(AkosData data, ScummBlock block)
    {
        block.GetMetadataItem(ScummMeta.AKHD.VersionNo, out var versionNumber);
        block.GetMetadataItem(ScummMeta.AKHD.Codec, out var celCompressionCodec);
        block.GetMetadataItem(ScummMeta.AKHD.CelsCount, out var celsCount);
        block.GetMetadataItem(ScummMeta.AKHD.Flags, out var flags);
        block.GetMetadataItem(ScummMeta.AKHD.ChoreCount, out var choreCount);
        block.GetMetadataItem(ScummMeta.AKHD.LayerCount, out var layerCount);

        data.Header = new AkosHeader
        {
            VersionNumber = (ushort)versionNumber,
            CelCompressionCodec = (ushort)celCompressionCodec,
            CelsCount = (ushort)celsCount,
            CostumeFlags = (ushort)flags,
            ChoreCount = (ushort)choreCount,
            LayerCount = (ushort)layerCount
        };
    }

    private void _ParseAKOF(AkosData data, ScummBlock block)
    {
        int count = block.DataLength / 6;
        data.CelOffsets = new AkosOffset[count];
        var span = block.DataSpan;
        for (int i = 0; i < count; i++)
        {
            data.CelOffsets[i] = new AkosOffset
            {
                AkcdOffset = span.U32LE(i * 6),
                AkciOffset = span.U16LE(i * 6 + 4),
            };
        }
    }

    private void _ParseAKCI(AkosData data, ScummBlock block)
    {
        int count = block.DataLength / 12;
        data.CelInfos = new AkosCelInfo[count];
        var span = block.DataSpan;
        for (int i = 0; i < count; i++)
        {
            int o = i * 12;
            data.CelInfos[i] = new AkosCelInfo
            {
                Width = span.U16LE(o + 0),
                Height = span.U16LE(o + 2),
                RelX = (short)span.U16LE(o + 4),
                RelY = (short)span.U16LE(o + 6),
                MoveX = (short)span.U16LE(o + 8),
                MoveY = (short)span.U16LE(o + 10),
                is640Width = span.U16LE(o + 0) == 640
            };
        }
    }
    
   
    

    /// <summary>
    /// Decodes the animation choreography steps. This is called lazily.
    /// </summary>
    public void _DecodeChores(AkosData data)
    {
        if (data.CelOffsets == null || data.Sequence.IsEmpty) return;

        // Note: AKCH block is actually where ChoreOffsets come from. 
        // Ensure you have logic to populate ChoreOffsets if not done in Parse().

        int dirs = data.DirectionCount;
        int anims = data.ChoreOffsets.Length / dirs;
        var chores = new List<AkosChore>();

        for (int anim = 0; anim < anims; anim++)
        {
            var chore = new AkosChore
            {
                AnimIndex = anim,
                DisplayName = $"Anim {anim}"
            };

            for (int dir = 0; dir < dirs; dir++)
            {
                int idx = anim * dirs + dir;
                if (idx >= data.ChoreOffsets.Length) break;

                ushort seqOffset = data.ChoreOffsets[idx];
                var direction = new AkosChoreDirection
                {
                    DirIndex = dir,
                    SequenceOffset = seqOffset
                };

                if (seqOffset != 0)
                    direction.Steps = _DecodeSequence(data, seqOffset);

                chore.Directions.Add(direction);
            }
            chores.Add(chore);
        }
        data.Chores = chores;
    }

    public List<AkosChoreStep> DecodeSingleSequence(AkosData data, int offset)
    {
        return _DecodeSequence(data, offset);
    }

    public List<AkosChoreStep> _DecodeSequence(AkosData data, int offset)
    {
        var steps = new List<AkosChoreStep>();
        var seq = data.Sequence.Span;
        int pos = offset;
        int maxSteps = 1000;

        while (pos < seq.Length && maxSteps-- > 0)
        {
            int stepOffset = pos;
            ushort code = seq[pos];

            // Handle multi-byte opcodes (0x80 bit)
            if ((code & 0x80) != 0 && pos + 1 < seq.Length)
                code = (ushort)((code << 8) | seq[pos + 1]);

            var step = new AkosChoreStep
            {
                Offset = stepOffset,
                RawCode = code
            };

            if ((code & 0xFF00) == 0xC000)
            {
                step.Kind = code == 0xC001 ? AkosStepKind.End : AkosStepKind.Control;
                steps.Add(step);
                if (step.Kind == AkosStepKind.End) break;
                pos += _ControlOpcodeSize(code);
                continue;
            }

            step.Kind = AkosStepKind.DrawSingle;
            step.CelIndex = code & 0x0FFF;
            steps.Add(step);
            pos += (code & 0x8000) != 0 ? 2 : 1;
        }
        return steps;
    }

    private int _ControlOpcodeSize(ushort code) => code switch
    {
        0xC001 or 0xC002 => 2,
        0xC010 or 0xC015 => 5,
        0xC020 or 0xC021 or 0xC022 => 3,
        0xC030 => 3,
        _ => (code & 0x8000) != 0 ? 2 : 1,
    };
}