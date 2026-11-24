using System;
using System.Collections.Generic;
using MMUCS.Core.Scumm;

namespace MMUCS.Runtime.Scripts;

/// <summary>
/// The room instructions (ScummVM <c>o8_roomOps</c>) and the kernel calls that change the screen's colours
/// or an actor's: single colours, intensity, saturation, transforms, switching to another of the room's
/// palettes, the shadow tables and remapping a costume's colours to the room's. Entering a room sets the
/// palette from its first one (ScummVM <c>initRoomSubBlocks</c>' <c>setCurrentPalette(0)</c>). <c>room fade</c>
/// only picks the transitions for the next room change; COMI's 129 is none at all, and its fades are the
/// scripts' own intensity loops, so it's kept and not drawn.
/// </summary>
public sealed partial class ScriptVm
{
    /// <summary>The colours on the screen.</summary>
    public ScreenPalette Palette { get; } = new();

    /// <summary>The room's palette in use (<c>room palette N</c>, ScummVM <c>_curPalIndex</c>).</summary>
    public int PaletteIndex { get; private set; }

    /// <summary>The transitions <c>room fade</c> picked: into the next room and out of this one (ScummVM <c>_switchRoomEffect</c> / <c>2</c>).</summary>
    public (int In, int Out) RoomEffects { get; private set; }

    /// <summary>The current room's palettes (its APAL blocks), in order.</summary>
    public List<byte[]> RoomPalettes(int room)
    {
        var list = new List<byte[]>();
        var apals = Scripts.RoomBlock(room)?.FindChild(ScummTag.ROOM)?.FindChild(ScummTag.PALS)?.FindChild(ScummTag.WRAP)?.FindChildrenRecursive(ScummTag.APAL);
        if (apals != null)
            foreach (var apal in apals) list.Add(apal.DataSpan.ToArray());
        return list;
    }

    /// <summary>Sets the screen's colours from palette <paramref name="index"/> of the current room (ScummVM <c>setCurrentPalette</c>).</summary>
    private void SetRoomPalette(int index)
    {
        var palettes = RoomPalettes(CurrentRoom);
        if (palettes.Count == 0) return;
        if (index < 0 || index >= palettes.Count) throw new ScriptException($"room {CurrentRoom} has no palette {index}");
        PaletteIndex = index;
        Palette.Set(palettes[index]);
    }

    private bool RoomOps(ScriptInstruction ins)
    {
        int a, b, c, d, e;
        switch (ins.SubOp)
        {
            case 0x52: d = Pop(); c = Pop(); b = Pop(); a = Pop(); Palette.SetColour(d, a, b, c); return true;
            case 0x57:
                a = Pop();
                if (a != 0) RoomEffects = (a & 0xFF, (a >> 8) & 0xFF);
                return true; // 0 is fadeIn(the room's effect): COMI's effects draw nothing
            case 0x58: e = Pop(); d = Pop(); c = Pop(); b = Pop(); a = Pop(); Palette.Darken(a, b, c, d, e); return true;
            case 0x59:
            {
                d = Pop(); c = Pop(); b = Pop(); a = Pop();
                var palettes = RoomPalettes(CurrentRoom);
                if (a < 0 || a >= palettes.Count) throw new ScriptException($"room {CurrentRoom} has no palette {a} to transform to");
                Palette.StartTransform(palettes[a], b, c, d);
                return true;
            }
            case 0x5C: SetRoomPalette(Pop()); return true;
            case 0x5F: e = Pop(); d = Pop(); c = Pop(); b = Pop(); a = Pop(); Palette.Desaturate(a, b, c, d, e); return true;
        }
        return false;
    }

    /// <summary>The palette's part of a frame, after the drawing (ScummVM <c>scummLoop_handleEffects</c>' <c>palManipulate</c>).</summary>
    public void EndFramePalette() => Palette.Step();

    /// <summary>
    /// kernel set 13 / 14, <c>remap-costume A, R,G,B</c> (ScummVM <c>remapActorPalette</c>): each of the costume's
    /// colours, its own RGB (RGBS) scaled by R/256, G/256 and B/256, becomes the nearest colour on the screen
    /// for the actor. An actor in special-draw keeps the costume's first 16 (its shadow colours).
    /// </summary>
    private void RemapCostume(int actor, int redScale, int greenScale, int blueScale, int threshold)
    {
        var a = Actors[actor];
        if (!Actors.IsInCurrentRoom(a) || Scripts.Costume(a.Costume) is not { } costume) return;
        var akpl = costume.Data.Palette.Span;
        var rgbs = costume.Data.Rgbs.Span;
        if (akpl.IsEmpty || rgbs.IsEmpty) return;
        for (int i = 0; i < akpl.Length && i * 3 + 2 < rgbs.Length && i < a.Palette.Length; i++)
        {
            if (a.ShadowMode != 0 && akpl[i] < 16) continue;
            a.Palette[i] = Palette.Nearest(rgbs[i * 3] * redScale >> 8, rgbs[i * 3 + 1] * greenScale >> 8, rgbs[i * 3 + 2] * blueScale >> 8, threshold);
        }
    }
}
