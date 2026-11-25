using System;
using System.Collections.Generic;
using System.Text;
using MMUCS.Core.Scumm;

namespace MMUCS.Runtime.Scripts;

/// <summary>A line on the screen (ScummVM's v7+ subtitle queue): page pixels, font (charset) and palette colour.</summary>
/// <remarks><see cref="OverActor"/>: placed over the talking actor in the room (an overhead talk line), rather than on the screen.</remarks>
public sealed record ScreenText(string Text, int X, int Y, int Color, int Charset, bool Center, bool Wrap, bool OverActor = false);

/// <summary>Plays the speech for a line: its tag names the voice file (<c>BCCB301</c> plays <c>BCCB301.IMX</c>).</summary>
public interface ISpeech
{
    void Start(string tag, int actor);
    void Stop();
    bool IsPlaying { get; }
    /// <summary>The talking actor's <c>frequency</c>, which the line plays at: 256 as recorded, higher faster and higher pitched.</summary>
    int Frequency { get; set; }
    /// <summary>Plays on unheard (voice mode 2, subtitles only), so the line still lasts as long as its speech.</summary>
    bool Muted { get; set; }
    /// <summary>The mouth's width and height in the speech <paramref name="aheadMs"/> past where it plays now, (0, 0) when none plays.</summary>
    (int Width, int Height) LipSync(int aheadMs);
}

/// <summary>
/// Talking and printing (ScummVM string.cpp, string_v7.cpp and actor.cpp's actorTalk / stopTalk for v8):
/// a line shows for <c>default-talk-delay</c> plus <c>char-inc</c> per letter jiffies and for as long as
/// its speech plays, with the actor's talk animation; <c>have-message</c> says a line is up, which
/// <c>wait-for-message</c> waits on.
/// </summary>
public sealed partial class ScriptVm
{
    /// <summary>ScummVM's StringTab: how the next line prints, and the default <c>begin</c> starts from.</summary>
    public sealed class StringSlot
    {
        public int X, Y, Color = 15, Charset;
        public bool Center, Overhead, Wrapping, NoTalkAnim;

        public StringSlot Copy() => (StringSlot)MemberwiseClone();

        public void CopyFrom(StringSlot o)
        {
            (X, Y, Color, Charset) = (o.X, o.Y, o.Color, o.Charset);
            (Center, Overhead, Wrapping, NoTalkAnim) = (o.Center, o.Overhead, o.Wrapping, o.NoTalkAnim);
        }
    }

    private readonly StringSlot[] _strings = [new(), new(), new(), new(), new()];
    /// <summary><c>cursorCommand charset</c> (v8): the charset talk lines start from.</summary>
    private void SetTalkCharset(int charset) => _stringDefaults[0].Charset = charset;

    private readonly StringSlot[] _stringDefaults =
    [
        new() { X = 2, Y = 5 }, new() { X = 2, Y = 5 }, new() { X = 2, Y = 5 }, new() { X = 2, Y = 5 }, new() { X = 2, Y = 5 },
    ];
    private readonly List<ScreenText> _subtitles = [];
    private readonly List<ScreenText> _blastTexts = [];
    private int _actorToPrintFor, _haveMsg, _talkDelay, _charsetColor, _charsetBufPos;
    private bool _keepText;
    /// <summary>Whether the line up has speech playing (ScummVM's <c>!_haveActorSpeechMsg</c>): voice mode 0 doesn't show its text.</summary>
    private bool _lineSpoken;

    /// <summary>Whether a key is held, by the engine's code (kernel-get 211); the frame loop answers it.</summary>
    public Func<int, bool> KeyState { get; set; }
    private string _charsetBuffer = "";

    /// <summary>The talk lines up now (the subtitle queue).</summary>
    public IReadOnlyList<ScreenText> Subtitles => _subtitles;

    /// <summary>
    /// What the last frame drew (ScummVM's blast text queue): the texts the scripts blasted during it, in
    /// order, then the talk lines.
    /// </summary>
    public IReadOnlyList<ScreenText> ScreenTexts { get; private set; } = [];

    /// <summary>Measures a string in a charset (<c>get-string-width</c>); without, it's 0.</summary>
    public Func<int, string, int> StringWidth { get; set; }

    /// <summary>The last line said or printed, with its tag (for a log).</summary>
    public event Action<string, string, int> Said;

    /// <summary>A system message (<c>print-system</c>: "insert disc 2"…), which the game shows in a box.</summary>
    public event Action<string> SystemMessage;

    /// <summary>
    /// Shows a system message and waits for the key that answers it (a click answers 0). The host can stop the
    /// engine in the middle of the instruction, as for a video; without one the message doesn't wait.
    /// </summary>
    public Func<string, int> AskSystem { get; set; }

    /// <summary>Speech for lines with a tag; without, lines show for their talk delay only.</summary>
    public ISpeech Speech { get; set; }

    /// <summary>The string slot <paramref name="m"/>: 0 talk, 1 text, 2 debug, 3 system, 4 blast text.</summary>
    public StringSlot Strings(int m) => _strings[m];

    /// <summary>How long the line on screen stays before it may go (jiffies).</summary>
    public int TalkDelay => _talkDelay;

    private int TalkingActor
    {
        get => State[EngineVars.TalkActor];
        set => State[EngineVars.TalkActor] = value;
    }

    /// <summary>The frame's time passes for the line on screen (ScummVM scummLoop's <c>_talkDelay -= delta</c>).</summary>
    public void TalkTime(int jiffies) => _talkDelay = Math.Max(0, _talkDelay - jiffies);

    private bool ExecText(ScriptSlot s, ScriptInstruction ins)
    {
        switch (ins.Opcode)
        {
            case 0x8f: ParseString(s, ins, 0, takesActor: true); return true; // printActor
            case 0x90: Push(State[EngineVars.Ego]); ParseString(s, ins, 0, takesActor: true); return true; // printEgo
            case 0x91: TalkActor(s, ins, Pop()); return true;
            case 0x92: TalkActor(s, ins, State[EngineVars.Ego]); return true;
            case 0x93: _actorToPrintFor = 0xFF; ParseString(s, ins, 0, takesActor: false); return true; // printLine
            case 0x94: ParseString(s, ins, 1, takesActor: false); return true; // printText
            case 0x95: ParseString(s, ins, 2, takesActor: false); return true; // printDebug
            case 0x96: ParseString(s, ins, 3, takesActor: false); return true; // printSystem
            case 0x97: ParseString(s, ins, 4, takesActor: false); return true; // blastText (the engine's slot 2)
            case 0xf6: // getStringWidth
            {
                int charset = Pop();
                Push(StringWidth?.Invoke(charset, ConvertMessage(TextOf(s, ins), out _)) ?? 0);
                return true;
            }
            case 0xd8: return KernelGet(s, ins);
        }
        return false;
    }

    /// <summary>
    /// ScummVM <c>o8_kernelGetFunctions</c>, the parts COMI asks for so far: an actor's talk animation, the
    /// registry's settings (text on, full volumes, object names), the volumes and the speech's lip sync.
    /// Others count as not carried out.
    /// </summary>
    private bool KernelGet(ScriptSlot s, ScriptInstruction ins)
    {
        var args = State.PopList(30);
        int a1 = args.Length > 1 ? args[1] : 0;
        int? value = (args.Length > 0 ? args[0] : 0) switch
        {
            0xDA => LipSync().Width,
            0xDB => LipSync().Height,
            0xDC => Actors[a1].TalkStartFrame,
            0xD8 => FindBlastObject(a1, args.Length > 2 ? args[2] : 0),
            0xDD or 0xDE or 0xDF => 127, // the effects', voices' and music's volume (255 / 2)
            0xD3 => KeyState?.Invoke(a1) == true ? 1 : 0, // getKeyState: the cannon's and the ship's Tab, the ship's arrows
            0xE0 => RegistryValue(a1),
            _ => null,
        };
        if (value is int v)
        {
            Push(v);
            return true;
        }
        _unhandledCounts[ins.Name] = _unhandledCounts.GetValueOrDefault(ins.Name) + 1;
        if (Strict) throw new ScriptException($"{ins.Name} isn't carried out yet");
        Push(0);
        Unhandled?.Invoke(new UnhandledCall(s.ToString(), ins, [args]));
        return true;
    }

    /// <summary>
    /// ScummVM <c>getSpeechLipSyncInfo</c>: the speech's mouth width and height 50 ms plus the <c>sync</c>
    /// variable ahead of where it plays. The talk scripts pick the mouth frame from them every frame.
    /// </summary>
    private (int Width, int Height) LipSync() => Speech?.LipSync(State[EngineVars.Sync] + 50) ?? (0, 0);

    /// <summary>
    /// The game's registry settings by name: what the host kept (<see cref="ReadRegistry"/>), or else the
    /// defaults ScummVM answers (text on, full volumes, object names) and −1 for the rest, as for a setting the
    /// game never wrote. The F1 menu's page is one of the rest: −1 starts it on page 0, the first.
    /// </summary>
    private int RegistryValue(int stringArray)
    {
        string name = StringOf(stringArray);
        if (ReadRegistry?.Invoke(name) is int kept) return kept;
        return name switch
        {
            "SFX Volume" or "Voice Volume" or "Music Volume" => 127,
            "Text Status" or "Object Names" => 1,
            _ => -1,
        };
    }

    /// <summary>A string array's text, by the array's id ("" when there's none).</summary>
    private string StringOf(int stringArray) =>
        State.ArrayById(stringArray) is { Type: ArrayType.String } array ? array.Text : "";

    /// <summary>ScummVM <c>o6_talkActor</c>: the line in the default talk style.</summary>
    private void TalkActor(ScriptSlot s, ScriptInstruction ins, int actor)
    {
        _actorToPrintFor = actor;
        _strings[0].CopyFrom(_stringDefaults[0]);
        ActorTalk(TextOf(s, ins));
    }

    /// <summary>ScummVM <c>ScummEngine_v8::decodeParseString</c>: one part of a print instruction.</summary>
    private void ParseString(ScriptSlot s, ScriptInstruction ins, int m, bool takesActor)
    {
        var str = _strings[m];
        switch (ins.SubOp)
        {
            case 0xC8: str.CopyFrom(_stringDefaults[m]); if (takesActor) _actorToPrintFor = Pop(); break;
            case 0xC9: _stringDefaults[m].CopyFrom(str); break;
            case 0xCA: str.Y = Pop(); str.X = Pop(); str.Overhead = false; break;
            case 0xCB: str.Color = Pop(); break;
            case 0xCC: str.Center = true; str.Overhead = false; break;
            case 0xCD: str.Charset = Pop(); break;
            case 0xCE: str.Wrapping = false; str.Overhead = false; break;
            case 0xCF: str.Overhead = true; str.NoTalkAnim = false; break;
            case 0xD0: str.NoTalkAnim = true; break;
            case 0xD1: PrintString(m, TextOf(s, ins)); break;
            case 0xD2: str.Wrapping = true; str.Overhead = false; break;
            default: throw new ScriptException($"print sub-op 0x{ins.SubOp:x2}");
        }
    }

    private static byte[] TextOf(ScriptSlot s, ScriptInstruction ins) =>
        ins.TextStart >= 0 ? s.Code.Code.Span.Slice(ins.TextStart, ins.TextLength).ToArray() : [];

    /// <summary>ScummVM <c>printString</c>: talk (0), screen text (1), debug (2), a system message (3).</summary>
    private void PrintString(int m, byte[] message)
    {
        switch (m)
        {
            case 0: ActorTalk(message); break;
            case 3:
            {
                // ScummVM showMessageDialog: the message waits for a key, which goes in keypress (the F1 menu's
                // "overwrite this game? (Y/N)" reads it on the next line).
                string text = ConvertMessage(message, out _);
                SystemMessage?.Invoke(text);
                if (AskSystem != null) State[EngineVars.Keypress] = AskSystem(text);
                break;
            }
            case 4: // ScummVM ScummEngine_v8::printString's blast text: drawn this frame
            {
                var str = _strings[4];
                string text = ConvertMessage(message, out _);
                if (text.Length > 0 && text != " ")
                    _blastTexts.Add(new ScreenText(text, str.X, str.Y, str.Color, str.Charset, str.Center, str.Wrapping));
                break;
            }
            // 1 draws a string straight to the screen and 2 is a debug message: neither shows in COMI.
        }
    }

    /// <summary>
    /// ScummVM <c>ScummEngine_v7::actorTalk</c>: the line's speech starts, the line on screen goes, the actor
    /// starts talking, and the new line goes up (<see cref="Charset"/>).
    /// </summary>
    private void ActorTalk(byte[] message)
    {
        _charsetBuffer = ConvertMessage(message, out _);
        string tag = _lastStringTag; // the last string converted decides: "/M1GT001/%v" speaks as the verb's name's tag
        if (State[EngineVars.HaveMessage] != 0) StopTalk();
        if (tag.Length > 0 && Speech is { } speech)
        {
            speech.Stop();
            speech.Start(tag, _actorToPrintFor);
            speech.Muted = State[EngineVars.VoiceMode] == 2;
            if (_actorToPrintFor != 0xFF && Actors.Find(_actorToPrintFor) is { } talker) speech.Frequency = talker.TalkFrequency;
        }
        _lineSpoken = Speech is { IsPlaying: true };

        if (_actorToPrintFor == 0xFF)
        {
            TalkingActor = 0xFF;
            _charsetColor = _strings[0].Color;
        }
        else
        {
            var a = Actors[_actorToPrintFor];
            TalkingActor = a.Number;
            if (!_strings[0].NoTalkAnim) RunActorTalkScript(a, a.TalkStartFrame);
            _charsetColor = a.TalkColor;
        }
        Said?.Invoke(_charsetBuffer, tag, _actorToPrintFor);

        _charsetBufPos = 0;
        _talkDelay = 0;
        _haveMsg = 1;
        bool wrap = _strings[0].Wrapping;
        _strings[0].Wrapping = true;
        Charset();
        State[EngineVars.HaveMessage] = _strings[0].NoTalkAnim ? 2 : 1;
        _strings[0].Wrapping = wrap;
    }

    /// <summary>
    /// ScummVM <c>ScummEngine_v7::CHARSET_1</c>, once a frame and when a line starts: an overhead line goes
    /// over the talking actor's head; while its delay runs nothing changes; then, once its speech is over,
    /// the line goes; a new one is queued with its delay.
    /// </summary>
    public void Charset()
    {
        if (_haveMsg == 0) return;
        var a = TalkingActor != 0xFF ? Actors.Find(TalkingActor) : null;
        var str = _strings[0];
        var saved = str.Copy();
        if (a != null && str.Overhead)
        {
            int left = CameraX - ScreenWidth / 2, top = CameraY - ScreenHeight / 2;
            int s = a.ScaleX * a.TalkX / 255;
            str.X = a.X - left + (a.TalkX - s) / 2 + s;
            s = a.ScaleY * a.TalkY / 255;
            str.Y = a.Y - a.Elevation - top + (a.TalkY - s) / 2 + s;
        }
        // As the engine does, the overhead place stays in the slot when it returns early.
        if (_talkDelay != 0) return;
        if (State[EngineVars.HaveMessage] != 0)
        {
            if (Speech is not { IsPlaying: true }) StopTalk();
            return;
        }
        if (a != null && !str.NoTalkAnim) RunActorTalkScript(a, a.TalkStartFrame);
        if (!_keepText) _subtitles.Clear();

        string line = _charsetBuffer[_charsetBufPos..];
        _talkDelay = State[EngineVars.DefaultTalkDelay] + line.Length * State[EngineVars.CharInc];
        if (line.Length > 0 && line != " ")
            _subtitles.Add(new ScreenText(line, str.X, str.Y, _charsetColor, str.Charset, str.Center, str.Wrapping, a != null && str.Overhead));
        _charsetBufPos = _charsetBuffer.Length;
        _haveMsg = State[EngineVars.HaveMessage] = str.NoTalkAnim ? 2 : 1;
        _keepText = false;
        str.CopyFrom(saved);
    }

    /// <summary>
    /// The frame is drawn: its blasted objects and texts and the talk lines; the blasts are gone next frame. With voice
    /// mode 0 a spoken line's text isn't shown (ScummVM <c>processSubtitleQueue</c>), so Ctrl+T shows or hides the line up.
    /// </summary>
    public void EndFrameTexts()
    {
        bool hide = _lineSpoken && State[EngineVars.VoiceMode] == 0;
        ScreenTexts = hide ? [.. _blastTexts] : [.. _blastTexts, .. _subtitles];
        _blastTexts.Clear();
        EndFrameBlasts();
    }

    /// <summary>
    /// ScummVM <c>processSfxQueues</c>, once a frame: while a line's speech plays it follows its actor's
    /// <c>frequency</c>, so a script changing it frame by frame (the helium balloon's squeaky voice coming
    /// down) bends the line as it plays.
    /// </summary>
    public void ProcessSpeech()
    {
        int act = TalkingActor;
        if (Speech is not { IsPlaying: true } speech) return;
        speech.Muted = State[EngineVars.VoiceMode] == 2; // subtitles only: the speech plays on unheard, so the line lasts as long
        if (act is > 0 and < 0x80 && Actors.Find(act) is { } a) speech.Frequency = a.TalkFrequency;
    }

    /// <summary>
    /// The talk-stop key (ScummVM <c>processKeyboard</c>): the line's delay is over, and a line with speech stops now; one
    /// without goes at the next frame's <see cref="Charset"/>.
    /// </summary>
    public void SkipLine()
    {
        _talkDelay = 0;
        if (Speech is { IsPlaying: true }) StopTalk();
    }

    /// <summary>ScummVM <c>stopTalk</c> (v8): the speech stops, the actor stops talking, the lines go.</summary>
    public void StopTalk()
    {
        Speech?.Stop();
        _haveMsg = 0;
        _talkDelay = 0;
        int act = TalkingActor;
        if (act != 0 && act < 0x80 && Actors.Find(act) is { } a && !_strings[0].NoTalkAnim)
            RunActorTalkScript(a, a.TalkStopFrame);
        TalkingActor = 0;
        State[EngineVars.HaveMessage] = 0;
        _keepText = false;
        _subtitles.Clear();
    }

    /// <summary>ScummVM <c>runActorTalkScript</c>: the actor's talk script with (actor, animation), or the animation itself.</summary>
    private void RunActorTalkScript(Actor a, int frame)
    {
        if (State[EngineVars.HaveMessage] == 2) return;
        if (TalkingActor == 0 || a.Room != CurrentRoom || a.Frame == frame) return;
        if (a.TalkScript != 0) RunScript(a.TalkScript, freezeResistant: true, args: [a.Number, frame]);
        else Actors.StartAnim(a, frame);
    }

    /// <summary>
    /// ScummVM <c>getObjOrActorName</c>: an actor's name, or an object's from its room (OBNA): the room's own
    /// objects and floating ones by their home room, carried ones by theirs.
    /// </summary>
    public byte[] NameOf(int obj)
    {
        if (obj < ActorTable.Count) return Actors.Find(obj)?.Name is { Length: > 0 } n ? Encoding.Latin1.GetBytes(n) : null;
        if (!State.Objects.IsObject(obj)) return null;
        return Scripts.ObjectName(State.Objects.Room(obj), obj) ?? Scripts.ObjectName(CurrentRoom, obj);
    }

    /// <summary>
    /// The tag of the last string converted (ScummVM <c>_lastStringTag</c>, set by translateText on every
    /// conversion): a verb, object or string variable inside a message is converted after the message's own
    /// tag is read, so its tag (or its lack of one) is what's left.
    /// </summary>
    private string _lastStringTag = "";

    /// <summary>
    /// ScummVM <c>convertMessageToString</c> for v8, with <c>translateText</c>'s tag: "/BCCB301/Hello" says
    /// "Hello" with speech BCCB301. Escapes (0xFF, a code, a 32-bit operand): 1 a new line, 4 a variable's
    /// number, 6 the name of the object or actor a variable holds, 7 a string variable (converted in turn); the rest (keep text,
    /// wait, verb names, sounds, colours) show nothing.
    /// </summary>
    public string ConvertMessage(ReadOnlySpan<byte> message, out string tag)
    {
        tag = "";
        int i = 0;
        if (message.Length > 0 && message[0] == '/')
        {
            int end = message[1..].IndexOf((byte)'/');
            if (end >= 0)
            {
                tag = Encoding.Latin1.GetString(message.Slice(1, Math.Min(end, 12))).ToUpperInvariant();
                i = end + 2;
            }
        }
        _lastStringTag = tag;
        var text = new StringBuilder();
        while (i < message.Length && message[i] != 0)
        {
            byte c = message[i++];
            if (c != 0xFF || i >= message.Length)
            {
                text.Append((char)c);
                continue;
            }
            byte code = message[i++];
            if (code is 1 or 2 or 3 or 8)
            {
                if (code == 1) text.Append('\n');
                continue;
            }
            int val = i + 4 <= message.Length ? BitConverter.ToInt32(message.Slice(i, 4)) : 0;
            i += 4;
            switch (code)
            {
                case 4: text.Append(State.Read(val)); break;
                case 5: if (VerbName(State.Read(val)) is { } verbName) text.Append(ConvertMessage(verbName, out _)); break;
                case 6: if (State.Read(val) is int named and not 0 && NameOf(named) is { } name) text.Append(ConvertMessage(name, out _)); break;
                case 7:
                    // The string goes through the conversion too (ScummVM convertStringMessage), so a verb
                    // name such as "/PU_M018/examine" loses its tag.
                    if (State.ArrayById(State.Read(val)) is { Type: ArrayType.String } array)
                    {
                        var bytes = array.Bytes;
                        int n = bytes.IndexOf((byte)0);
                        text.Append(ConvertMessage(n < 0 ? bytes : bytes[..n], out _));
                    }
                    break;
            }
        }
        return text.ToString();
    }
}
