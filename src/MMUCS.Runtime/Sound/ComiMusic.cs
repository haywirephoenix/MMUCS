using System;
using MMUCS.Core.Audio;

namespace MMUCS.Runtime.Sound;

/// <summary>
/// Where <see cref="ComiMusic"/> plays: one music stream at a time, named by its cue's sound id, which a new
/// one takes over from with a crossfade (ScummVM's iMUSE streams, switchStream and hooks).
/// </summary>
public interface IMusicOutput
{
    /// <summary>The sound id of the music stream playing (or loading), 0 when none is.</summary>
    int Playing { get; }

    /// <summary>Starts <paramref name="cue"/>'s track with nothing playing.</summary>
    void Start(MusicCue cue, int hook);

    /// <summary>
    /// The playing stream fades out over <paramref name="fadeMs"/> while <paramref name="cue"/>'s starts at
    /// full volume, from the start or, with <paramref name="samePlace"/>, from where the old one was.
    /// </summary>
    void Switch(MusicCue cue, int fadeMs, bool samePlace, int hook);

    /// <summary>Switches to <paramref name="cue"/> (from its start) when the playing track passes its "exit" marker.</summary>
    void SwitchAtExit(MusicCue cue, int fadeMs, int hook);

    /// <summary>The playing stream fades out and ends.</summary>
    void FadeOut(int ms);

    /// <summary>The playing stream's hook: which of its jumps it takes.</summary>
    void SetHook(int hook);

    /// <summary>Report (<see cref="TakeEnd"/>) when sound <paramref name="soundId"/> passes a marker starting with "_" (its "_end").</summary>
    void ArmEnd(int soundId);

    /// <summary>Whether an armed "_end" was passed since the last call.</summary>
    bool TakeEnd();
}

/// <summary>
/// COMI's music as its scripts drive it through Digital iMUSE (ScummVM dimuse_scripts.cpp): <c>sound-kludge</c>
/// 0x1000 sets the music state (where the player is), 0x1001 a sequence (an event's music, played instead of
/// the state's until it ends, then the state's comes back). Each is a row of <see cref="MusicTable"/>, whose
/// transition says how its track takes over. <see cref="Refresh"/> runs once a frame.
/// </summary>
public sealed class ComiMusic
{
    /// <summary>The sound-kludge commands for music (ScummVM parseScriptCmds).</summary>
    public const int SetStateCommand = 0x1000, SetSequenceCommand = 0x1001, SetCuePointCommand = 0x1002, SetAttributeCommand = 0x1003;

    /// <summary>Where the attributes for the states' attribute positions start (ScummVM COMI_STATE_OFFSET).</summary>
    private const int StateOffset = 3;

    private readonly IMusicOutput _output;
    private readonly int[] _attributes = new int[1024];
    private int _curState, _curSeq, _nextSeq;
    private bool _stopSequence;

    public ComiMusic(MusicTable table, IMusicOutput output)
    {
        Table = table ?? throw new ArgumentNullException(nameof(table));
        _output = output ?? throw new ArgumentNullException(nameof(output));
    }

    public MusicTable Table { get; }

    /// <summary>The state and sequence in force (rows of the tables), 0 for their null entries.</summary>
    public MusicCue State => Table.States[_curState];
    public MusicCue Sequence => Table.Sequences[_curSeq];

    /// <summary>What changed, for a log ("state 1099 stateMenu: 1099-Menu, start").</summary>
    public event Action<string> Changed;

    /// <summary>A <c>sound-kludge</c> list; true when it was a music command (the rest are sound effects').</summary>
    public bool Command(ReadOnlySpan<int> list)
    {
        if (list.Length == 0) return false;
        int arg = list.Length > 1 ? list[1] : 0;
        switch (list[0])
        {
            case SetStateCommand: SetState(arg); return true;
            case SetSequenceCommand: SetSequence(arg); return true;
            case SetCuePointCommand: return true; // FT's (early iMUSE) only
            case SetAttributeCommand: return true; // The Dig's only: COMI's attributes come from its tables
            case 0x2000 or 0x2001 or 0x2002: return true; // the group volumes from the player's settings
            default: return false;
        }
    }

    /// <summary>ScummVM <c>setComiMusicState</c>: 0 is the null state; a new state's music plays unless a sequence is on.</summary>
    public void SetState(int stateId)
    {
        if (stateId == 0) stateId = 1000;
        int num = IndexOf(Table.States, stateId);
        if (num < 0 || num == _curState) return;
        if (_curSeq == 0) Play(num == 0 ? null : Table.States[num], num, sequence: false, $"state {stateId}");
        else Changed?.Invoke($"state {stateId} {Table.States[num].Name}: waits for {Table.Sequences[_curSeq].Name} to end");
        _curState = num;
    }

    /// <summary>
    /// ScummVM <c>setComiMusicSequence</c>: a sequence plays over the state's music (or waits while one that
    /// ends at its "_end" plays); 0 ends it, for the one waiting or the state's music.
    /// </summary>
    public void SetSequence(int seqId)
    {
        if (seqId == 0) seqId = 2000;
        int num = IndexOf(Table.Sequences, seqId);
        if (num < 0 || num == _curSeq) return;
        if (num != 0)
        {
            if (_curSeq != 0 && Table.Sequences[_curSeq].Transition is 4 or 6)
            {
                _nextSeq = num;
                return;
            }
            Play(Table.Sequences[num], 0, sequence: true, $"sequence {seqId}");
            _nextSeq = 0;
        }
        else if (_nextSeq != 0)
        {
            Play(Table.Sequences[_nextSeq], 0, sequence: true, $"sequence {Table.Sequences[_nextSeq].SoundId} (waiting)");
            num = _nextSeq;
            _nextSeq = 0;
        }
        else
        {
            Play(_curState != 0 ? Table.States[_curState] : null, _curState, sequence: true, "sequence over, back to the state");
            num = 0;
        }
        _curSeq = num;
    }

    /// <summary>
    /// ScummVM <c>scriptRefresh</c>, once a frame: a sequence whose "_end" was passed ends, and so does one
    /// whose track ran out, bringing the state's music back.
    /// </summary>
    public void Refresh()
    {
        if (_output.TakeEnd()) _stopSequence = true;
        if (_stopSequence)
        {
            SetSequence(0);
            _stopSequence = false;
        }
        if (_output.Playing != 0) return;
        if (_curSeq != 0) SetSequence(0);
    }

    /// <summary>The music's part of a save: the state and sequence in force, the one waiting, and the attributes (the hooks taken).</summary>
    public sealed record Saved(int State, int Sequence, int NextSequence, bool StopSequence, int[] Attributes);

    public Saved Save() => new(_curState, _curSeq, _nextSeq, _stopSequence, (int[])_attributes.Clone());

    /// <summary>
    /// Puts the music back as <paramref name="saved"/> had it (ScummVM's iMUSE restores its tracks on a load):
    /// the sequence's track, else the state's, crossfades in from its start unless it's what plays already.
    /// </summary>
    public void Restore(Saved saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        (_curState, _curSeq, _nextSeq, _stopSequence) = (saved.State, saved.Sequence, saved.NextSequence, saved.StopSequence);
        Array.Copy(saved.Attributes, _attributes, Math.Min(saved.Attributes.Length, _attributes.Length));
        var cue = _curSeq != 0 ? Table.Sequences[_curSeq] : _curState != 0 ? Table.States[_curState] : null;
        if (cue == null || cue.File.Length == 0 || cue.SoundId == 0)
        {
            if (_output.Playing != 0) _output.FadeOut(1000);
            Changed?.Invoke("loaded: silence");
            return;
        }
        if (_output.Playing == cue.SoundId) return;
        if (_output.Playing == 0) _output.Start(cue, 0);
        else _output.Switch(cue, 1000, samePlace: false, 0);
        Changed?.Invoke($"loaded: {cue.Name}: {cue.File}");
    }

    private static int IndexOf(System.Collections.Generic.IReadOnlyList<MusicCue> rows, int soundId)
    {
        for (int i = 0; i < rows.Count; i++)
            if (rows[i].SoundId == soundId) return i;
        return -1;
    }

    /// <summary>ScummVM <c>playComiMusic</c>: the hook from the attributes, then the cue's transition from what plays now.</summary>
    private void Play(MusicCue cue, int attribPos, bool sequence, string why)
    {
        int hookId = 0;
        if (cue != null && attribPos != 0)
        {
            if (cue.AttribPos != 0) attribPos = cue.AttribPos;
            int at = StateOffset + attribPos;
            hookId = _attributes[at];
            if (cue.HookId != 0)
            {
                if (hookId != 0 && cue.HookId > 1) _attributes[at] = 2;
                else
                {
                    _attributes[at] = hookId + 1;
                    if (cue.HookId < hookId + 1) _attributes[at] = 1;
                }
            }
        }

        int old = _output.Playing;
        if (cue == null)
        {
            if (old != 0) _output.FadeOut(2000);
            Changed?.Invoke($"{why}: silence");
            return;
        }

        string what = $"{why} {cue.Name}: {(cue.File.Length > 0 ? cue.File : "no track")}";
        switch (cue.Transition)
        {
            case 2:
            case 3:
            case 4:
            case 12:
                if (cue.File.Length == 0 || cue.SoundId == 0)
                {
                    if (old != 0) _output.FadeOut(1000);
                    Changed?.Invoke($"{what}, fade out");
                    break;
                }
                if (cue.Transition == 4)
                {
                    _stopSequence = false;
                    _output.ArmEnd(cue.SoundId);
                }
                if (old == 0)
                {
                    _output.Start(cue, hookId);
                    Changed?.Invoke($"{what}, start (hook {hookId})");
                    break;
                }
                // The table's delay is in 60ths of a second; none means a second (ScummVM).
                int fade = cue.FadeOutDelay == 0 ? 1000 : cue.FadeOutDelay * 100 / 6;
                if (cue.Transition == 2)
                {
                    _output.Switch(cue, fade, samePlace: false, cue.HookId);
                    Changed?.Invoke($"{what}, crossfade");
                }
                else if (old != cue.SoundId)
                {
                    if (!sequence && cue.AttribPos != 0 && cue.AttribPos == Table.States[_curState].AttribPos)
                    {
                        _output.Switch(cue, fade, samePlace: true, 0);
                        Changed?.Invoke($"{what}, crossfade in place (same attribute)");
                    }
                    else if (cue.Transition == 12)
                    {
                        _output.SetHook(cue.HookId);
                        _output.SwitchAtExit(cue, fade, hookId);
                        Changed?.Invoke($"{what}, at the playing track's exit");
                    }
                    else
                    {
                        _output.Switch(cue, fade, samePlace: false, hookId);
                        Changed?.Invoke($"{what}, crossfade (hook {hookId})");
                    }
                }
                break;
            case 8:
                if (old != 0) _output.SetHook(cue.HookId);
                Changed?.Invoke($"{what}, hook {cue.HookId}");
                break;
            case 9:
                if (old != 0)
                {
                    _output.SetHook(cue.HookId);
                    _stopSequence = false;
                    _output.ArmEnd(old);
                }
                Changed?.Invoke($"{what}, hook {cue.HookId} until its end");
                break;
            default:
                Changed?.Invoke($"{what}, nothing to do (transition {cue.Transition})");
                break;
        }
    }
}

/// <summary>
/// Music nobody hears, for running the scripts headless: a track plays from when it's started or switched
/// to until it's faded out, and never ends on its own (so a sequence lasts until the scripts end it).
/// </summary>
public sealed class SilentMusic : IMusicOutput
{
    private int _endArmed;

    public int Playing { get; private set; }
    public int Hook { get; private set; }

    public void Start(MusicCue cue, int hook) => (Playing, Hook) = (cue.SoundId, hook);
    public void Switch(MusicCue cue, int fadeMs, bool samePlace, int hook) => (Playing, Hook) = (cue.SoundId, hook);
    public void SwitchAtExit(MusicCue cue, int fadeMs, int hook) => (Playing, Hook) = (cue.SoundId, hook);
    public void FadeOut(int ms) => Playing = 0;
    public void SetHook(int hook) => Hook = hook;
    public void ArmEnd(int soundId) => _endArmed = soundId;
    public bool TakeEnd() => false;
}
