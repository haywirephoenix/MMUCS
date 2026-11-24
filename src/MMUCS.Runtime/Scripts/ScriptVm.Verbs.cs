using System;
using System.Collections.Generic;
using MMUCS.Core.Scumm;

namespace MMUCS.Runtime.Scripts;

/// <summary>
/// Verbs (ScummVM verbs.cpp and o8_verbOps for v7+): named texts the scripts place on the screen, such as
/// COMI's dialogue choices (verbs 500 and up). Every frame each verb that's on is drawn as blast text, in its
/// colour, its highlight colour under the mouse or its dim colour, and a click on one runs the verb script
/// with its number. Image verbs aren't drawn (COMI doesn't use them).
/// </summary>
public sealed partial class ScriptVm
{
    private sealed class VerbSlot
    {
        public int VerbId, Color, HiColor, DimColor, Charset, CurMode, SaveId, Key, ImgIndex;
        public bool Center, IsImage;
        /// <summary>Where the scripts put it (ScummVM <c>origLeft</c>, <c>curRect.top</c>) and where its text was last drawn.</summary>
        public int Left, Top;
        public (int Left, int Top, int Right, int Bottom) Rect;
        public byte[] Name;
    }

    private VerbSlot[] _verbs;
    private int _curVerb, _curVerbSlot, _verbLineSpacing;

    /// <summary>The height of a line in charset N (the font's), for the verbs' rectangles; 0 when there's no font.</summary>
    public Func<int, int> FontHeight { get; set; }

    private VerbSlot[] Verbs => _verbs ??= NewVerbs();

    private VerbSlot[] NewVerbs()
    {
        var verbs = new VerbSlot[Math.Max(1, State.Limits.Verbs)];
        for (int i = 0; i < verbs.Length; i++) verbs[i] = new VerbSlot();
        return verbs;
    }

    /// <summary>The verbs on now (their numbers and texts as drawn), for a report.</summary>
    public IEnumerable<(int Verb, string Text, (int Left, int Top, int Right, int Bottom) Rect)> VerbsShown
    {
        get
        {
            foreach (var v in Verbs)
                if (v.VerbId != 0 && v.CurMode != 0 && v.SaveId == 0 && !v.IsImage && v.Name != null)
                    yield return (v.VerbId, ConvertMessage(v.Name, out _), v.Rect);
        }
    }

    /// <summary>ScummVM <c>getVerbSlot</c>: the slot of verb <paramref name="id"/> in save set <paramref name="mode"/> (0: the live verbs), or 0.</summary>
    private int VerbSlotOf(int id, int mode)
    {
        var verbs = Verbs;
        for (int i = 1; i < verbs.Length; i++)
            if (verbs[i].VerbId == id && verbs[i].SaveId == mode) return i;
        return 0;
    }

    /// <summary>ScummVM <c>killVerb</c>.</summary>
    private void KillVerb(int slot)
    {
        if (slot == 0) return;
        var v = Verbs[slot];
        v.VerbId = 0;
        v.CurMode = 0;
        v.Name = null;
        v.SaveId = 0;
    }

    /// <summary>ScummVM <c>o8_verbOps</c>.</summary>
    private bool VerbOps(ScriptSlot s, ScriptInstruction ins)
    {
        var verbs = Verbs;
        if (ins.SubOp == 0x96)
        {
            _curVerb = Pop();
            _curVerbSlot = VerbSlotOf(_curVerb, 0);
            return true;
        }
        if (_curVerbSlot < 0 || _curVerbSlot >= verbs.Length) throw new ScriptException($"verb slot {_curVerbSlot} out of range");
        var v = verbs[_curVerbSlot];
        switch (ins.SubOp)
        {
            case 0x97: // new
                if (_curVerbSlot == 0)
                {
                    int slot = 1;
                    while (slot < verbs.Length && verbs[slot].VerbId != 0) slot++;
                    if (slot >= verbs.Length) throw new ScriptException("too many verbs");
                    _curVerbSlot = slot;
                }
                v = verbs[_curVerbSlot];
                (v.VerbId, v.Color, v.HiColor, v.DimColor, v.IsImage) = (_curVerb, 2, 0, 8, false);
                (v.Charset, v.CurMode, v.SaveId, v.Key, v.Center, v.ImgIndex) = (_stringDefaults[0].Charset, 0, 0, 0, false, 0);
                break;
            case 0x98: KillVerb(_curVerbSlot); break;
            case 0x99: v.Name = TextOf(s, ins); v.IsImage = false; v.ImgIndex = 0; break;
            case 0x9A: v.Top = Pop(); v.Left = Pop(); break;
            case 0x9B: v.CurMode = 1; break;
            case 0x9C: v.CurMode = 0; break;
            case 0x9D: v.Color = Pop(); break;
            case 0x9E: v.HiColor = Pop(); break;
            case 0xA0: v.DimColor = Pop(); break;
            case 0xA1: v.CurMode = 2; break;
            case 0xA2: v.Key = Pop(); break;
            case 0xA3: // image: not drawn here
            {
                int room = Pop(), obj = Pop();
                if (_curVerbSlot != 0 && obj != v.ImgIndex) (v.IsImage, v.ImgIndex) = (true, obj);
                _ = room;
                break;
            }
            case 0xA4: // name from a string variable
            {
                int a = Pop();
                v.Name = a == 0 ? [] : StringBytes(a);
                v.IsImage = false;
                v.ImgIndex = 0;
                break;
            }
            case 0xA5: v.Center = true; break;
            case 0xA6: v.Charset = Pop(); break;
            case 0xA7: _verbLineSpacing = Pop(); break;
            default: return false;
        }
        return true;
    }

    /// <summary>A string variable's bytes up to its end (ScummVM <c>getStringAddress</c>), or empty.</summary>
    private byte[] StringBytes(int array)
    {
        if (State.ArrayById(array) is not { Type: ArrayType.String } a) return [];
        var bytes = a.Bytes;
        int n = bytes.IndexOf((byte)0);
        return (n < 0 ? bytes : bytes[..n]).ToArray();
    }

    /// <summary>
    /// ScummVM <c>o6_saveRestoreVerbs</c> (v8's sub-ops 0x8D–0x8F): verbs <paramref name="a"/> to
    /// <paramref name="b"/> go to save set <paramref name="set"/> (hidden), come back from it, or are deleted
    /// from it.
    /// </summary>
    private bool SaveRestoreVerbs(ScriptInstruction ins)
    {
        int set = Pop(), b = Pop(), a = Pop();
        var verbs = Verbs;
        switch (ins.SubOp)
        {
            case 0x8D:
                for (; a <= b; a++)
                    if (VerbSlotOf(a, 0) is int slot and not 0 && verbs[slot].SaveId == 0) verbs[slot].SaveId = set;
                return true;
            case 0x8E:
                for (; a <= b; a++)
                {
                    if (VerbSlotOf(a, set) == 0) continue;
                    if (VerbSlotOf(a, 0) is int live and not 0) KillVerb(live);
                    verbs[VerbSlotOf(a, set)].SaveId = 0;
                }
                return true;
            case 0x8F:
                for (; a <= b; a++)
                    if (VerbSlotOf(a, set) is int slot and not 0) KillVerb(slot);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// ScummVM <c>findVerbAtPos</c>: the last slot whose verb is on, live and under the point (screen pixels),
    /// as its text was last drawn; 0 for none. A centred verb's left edge is ScummVM's odd sum, kept.
    /// </summary>
    public int FindVerbAt(int x, int y)
    {
        var verbs = Verbs;
        for (int i = verbs.Length - 1; i > 0; i--)
        {
            var v = verbs[i];
            if (v.CurMode != 1 || v.VerbId == 0 || v.SaveId != 0 || y < v.Rect.Top || y >= v.Rect.Bottom) continue;
            if (v.Center ? x < -(v.Rect.Right - 2 * v.Rect.Left) || x >= v.Rect.Right : x < v.Rect.Left || x >= v.Rect.Right) continue;
            return i;
        }
        return 0;
    }

    /// <summary>ScummVM <c>convertVerbMessage</c>'s lookup: the name of live text verb <paramref name="verb"/>, or null.</summary>
    private byte[] VerbName(int verb)
    {
        if (verb == 0) return null;
        var verbs = Verbs;
        for (int k = 1; k < verbs.Length; k++)
            if (verbs[k].VerbId == verb && !verbs[k].IsImage && verbs[k].SaveId == 0) return verbs[k].Name;
        return null;
    }

    /// <summary>The verb number of a slot (0 for none).</summary>
    public int VerbIdOf(int slot) => slot > 0 && slot < Verbs.Length ? Verbs[slot].VerbId : 0;

    /// <summary>The live verb that's on with key <paramref name="key"/> (ScummVM checkExecVerbs' key loop), or 0.</summary>
    public int VerbWithKey(int key)
    {
        var verbs = Verbs;
        for (int i = 1; i < verbs.Length; i++)
            if (verbs[i].VerbId != 0 && verbs[i].SaveId == 0 && verbs[i].CurMode == 1 && verbs[i].Key == key) return verbs[i].VerbId;
        return 0;
    }

    /// <summary>
    /// ScummVM <c>redrawVerbs</c> (every frame in v7+): each verb drawn, the one under the mouse in its highlight
    /// colour while the cursor is on.
    /// </summary>
    public void RedrawVerbs(int mouseX, int mouseY)
    {
        int over = CursorLevel > 0 ? FindVerbAt(mouseX, mouseY) : 0;
        var verbs = Verbs;
        for (int i = 0; i < verbs.Length; i++) DrawVerb(i, i == over && verbs[i].HiColor != 0);
    }

    /// <summary>
    /// ScummVM <c>ScummEngine_v7::drawVerb</c>: a text verb that's on and live goes in the blast text queue at its
    /// place (centred if it says so), in its colour; its rectangle is where the text went. In v8 a text too wide
    /// for the rest of the screen breaks at its last space that fits, the rest a line further down.
    /// </summary>
    private void DrawVerb(int slot, bool highlighted)
    {
        if (slot == 0) return;
        var v = Verbs[slot];
        if (v.SaveId != 0 || v.CurMode == 0 || v.VerbId == 0 || v.IsImage || v.Name == null) return;
        int color = v.CurMode == 2 ? v.DimColor : highlighted && v.HiColor != 0 ? v.HiColor : v.Color;
        string text = ConvertMessage(v.Name, out _);
        int x = v.Left, y = v.Top, height = FontHeight?.Invoke(v.Charset) ?? 0;
        int width = StringWidth?.Invoke(v.Charset, text) ?? 0;
        int left = v.Center ? x - width / 2 : x;
        v.Rect = (left, y, left + width, y + height * Math.Max(1, text.Split('\n').Length));

        int maxWidth = ScreenWidth - v.Rect.Left;
        if (width > maxWidth)
        {
            int cut = text.Length - 1, finalWidth = maxWidth;
            for (; cut >= 0; cut--)
            {
                if (text[cut] != ' ') continue;
                if ((finalWidth = StringWidth?.Invoke(v.Charset, text[..cut]) ?? 0) <= maxWidth) break;
            }
            string first = cut >= 0 ? text[..cut] : "", rest = cut >= 0 ? text[(cut + 1)..] : text;
            AddVerbText(first, x, y, color, v);
            AddVerbText(rest, x, y + _verbLineSpacing, color, v);
            v.Rect = (v.Rect.Left, v.Rect.Top, v.Rect.Left + finalWidth, v.Rect.Bottom + _verbLineSpacing);
        }
        else AddVerbText(text, x, y, color, v);
    }

    /// <summary>ScummVM <c>enqueueText</c>: a verb's line as blast text (nothing for an empty line or a single space).</summary>
    private void AddVerbText(string text, int x, int y, int color, VerbSlot v)
    {
        if (text.Length == 0 || text == " ") return;
        _blastTexts.Add(new ScreenText(text, x, y, color, v.Charset, v.Center, false));
    }
}
