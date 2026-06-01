using System;
using System.Threading;
using Godot;

public static class RoomDecoders
{
    public struct RoomInfo
    {
        public int TotalPalettes { get; }
        public int DefaultPaletteIndex { get; }

        public RoomInfo(int totalPalettes, int defaultPaletteIndex)
        {
            TotalPalettes = totalPalettes;
            DefaultPaletteIndex = defaultPaletteIndex;
        }
    }
    
    public static bool TryGetRoomInfo(ScummBlock rmhdBlock, ScummBlock roomBlock, out RoomInfo info)
    {
        info = default;
        if (rmhdBlock == null || roomBlock == null) return false;

        var palsBlock = roomBlock.FindChild(ScummTag.PALS);
        var wrapBlock = palsBlock?.FindChild(ScummTag.WRAP);
        var apals = wrapBlock?.FindChildrenRecursive(ScummTag.APAL);
    
        int totalPalettes = apals?.Count ?? 0;
        
        int defaultPaletteIndex = 0; 
        defaultPaletteIndex = Mathf.Clamp(defaultPaletteIndex, 0, Mathf.Max(0, totalPalettes - 1));

        info = new RoomInfo(totalPalettes, defaultPaletteIndex);
        return true;
    }
    
    public static bool DecodeBackgroundImage(
        int roomId,
        ScummBlock _rmhdBlock,
        ScummBlock _roomBlock,
        int paletteIndex,
        CancellationToken token,
        out Image backgroundImage,
        out byte[] indexedPixels,
        out int outPitch,
        out ReadOnlySpan<byte> _roomPalette,
        bool generateImage = false)
    {
        backgroundImage = null;
        indexedPixels = null;
        outPitch = 0;
        _roomPalette = null;

        var rmhd = _rmhdBlock;
        if (rmhd == null) return false;
    
        var smap = _roomBlock.FindChildRecursive(ScummTag.SMAP);
        if (smap == null) return false;

        // Decode the raw SMAP bytes
        var rmhdSpan = rmhd.DataSpan;
        int width = (int)rmhdSpan.U32LE(4);
        int height = (int)rmhdSpan.U32LE(8);

        if (!ScummDecoders.TryDecodeSmapBlock(smap, width, height, token, out byte[] flatBuffer, out int pitch))
            return false;
    
        outPitch = pitch;
        indexedPixels = flatBuffer;

        var apals = _roomBlock.FindChild(ScummTag.PALS)?.FindChild(ScummTag.WRAP)?.FindChildrenRecursive(ScummTag.APAL);
        if (apals != null && apals.Count > 0)
        {
            int clampedIdx = Mathf.Clamp(paletteIndex, 0, apals.Count - 1);
            _roomPalette = apals[clampedIdx].DataSpan;
        }

        if (!generateImage) 
            return true; 
    
        var tempSurface = new IndexedSurface
        {
            Width = width,
            Height = height,
            Pitch = pitch,
            Pixels = flatBuffer
        };

        var (_, resolvedImage) = ResolveImageAndPalette(_roomBlock, rmhd, tempSurface, paletteIndex);
        backgroundImage = resolvedImage;

        return (backgroundImage != null);
    }
    
    public static (byte[] paletteBytes, Image image) ResolveImageAndPalette(ScummBlock room, ScummBlock rmhd, IndexedSurface surface, int paletteIndex)
    {
        var apals = room.FindChild(ScummTag.PALS)?.FindChild(ScummTag.WRAP)?.FindChildrenRecursive(ScummTag.APAL);
        if (apals == null || apals.Count == 0) return (null, null);

        int idx = Mathf.Clamp(paletteIndex, 0, apals.Count - 1);
        byte[] paletteBytes = apals[idx].DataSpan.ToArray();
    
        Image image;
        // Handle COMI Dual-Palette Edge-Case (banjo) - potentially convert to lookup in future
        if (room.Offset == 45980903 && apals.Count > 1)
        {
            int transparentIndex = (int)rmhd.DataSpan.U32LE(20);
            image = ScummImageUtils.CreateGodotImageDualPalette(
                surface.Pixels, surface.Width, surface.Height, surface.Pitch, apals[0].DataSpan, apals[1].DataSpan, 640, (byte)transparentIndex);
        }
        else
        {
            image = ScummImageUtils.CreateGodotImage(surface.Pixels, surface.Width, surface.Height, surface.Pitch, paletteBytes);
        }

        return (paletteBytes, image);
    }
}