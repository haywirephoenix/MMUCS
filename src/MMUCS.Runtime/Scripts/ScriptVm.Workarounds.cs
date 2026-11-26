namespace MMUCS.Runtime.Scripts;

/// <summary>
/// Fixes for bugs in COMI's own scripts: ScummVM's enhancements for the game (the ones it has for COMI, all of
/// them; ScummVM keeps them behind its Enhancements options, here they're always on), each where ScummVM makes it
/// and on the same conditions. ScummVM's script offsets count the script's 8-byte header; these don't.
/// </summary>
public sealed partial class ScriptVm
{
    private const byte JumpOpcode = 0x66;

    /// <summary>
    /// ScummVM <c>o6_startScript</c> (audio): in the theater, after greasing the cannonballs, the juggling show cuts
    /// from backstage (room 18) to the stage (19) with global 168, and script 29, which sets each room's music, doesn't
    /// run: the muffled backstage music plays on under the unmuffled drum fill (seqDropBalls, 2225). When global 168 starts 118
    /// on the stage, the stage's music (stateJuggling, 1278) is set as script 29 would.
    /// </summary>
    private void OnStartScript(ScriptSlot s, int script)
    {
        if (CurrentRoom == 19 && s.Kind == ScriptKind.Global && s.Number == 168 && script == 118)
            SoundKludge?.Invoke([ComiMusicSetState, 1278]);
    }

    /// <summary>
    /// ScummVM <c>o6_beginOverride</c> (audio): Esc before "Any last words, Threepwood?" on the Sea Cucumber skips
    /// the seqAttack (2277) that sends the monkeys' music (2275) to its end, so it plays on for ever and the plank's
    /// music never comes. When the plank's cutscene (global 251 in room 37) begins its override (ScummVM's 0x1A) with
    /// 2275 still playing, seqAttack is played as the dialogue would have.
    /// </summary>
    private void OnBeginOverride(ScriptSlot s)
    {
        if (s.Kind == ScriptKind.Global && s.Number == 251 && CurrentRoom == 37 && s.Pc == 0x12 && MusicRunning?.Invoke(2275) == true)
            SoundKludge?.Invoke([ComiMusicSetSequence, 2277]);
    }

    /// <summary>
    /// ScummVM <c>o6_soundKludge</c> (game-breaking): room 11's local 2016 waits for a sound in a loop with no
    /// break-here, so the game stops until the sound has played out. A sound-kludge there followed straight by a
    /// jump (the loop's) breaks, so the loop waits a frame at a time.
    /// </summary>
    private void OnSoundKludge(ScriptSlot s)
    {
        if (CurrentRoom == 11 && s.Kind == ScriptKind.Local && s.Number == 2016 && s.Code is { } code && s.Pc < code.Code.Length && code.Code.Span[s.Pc] == JumpOpcode)
            BreakHere();
    }

    private const int ComiMusicSetState = 0x1000, ComiMusicSetSequence = 0x1001;
}
