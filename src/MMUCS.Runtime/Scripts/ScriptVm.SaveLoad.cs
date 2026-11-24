using System;
using MMUCS.Core.Scumm;

namespace MMUCS.Runtime.Scripts;

/// <summary>A save or load the scripts asked for, carried out by the frame loop (ScummVM <c>_saveLoadFlag</c>).</summary>
public enum SaveLoadRequest
{
    None,
    Save,
    Load,
}

/// <summary>
/// Saving and loading (ScummVM saveload.cpp and <c>scummLoop_handleSaveLoad</c>), and quitting. COMI's F1 menu
/// works on a temporary save: its key script does <c>room save-game</c> before taking the screen over with room
/// 92, and Return to Game does <c>room load-game</c>, which puts everything back as it was; the key script
/// then finds <c>game-loaded</c> set and carries on into the game. The scripts only ask; the frame loop saves
/// and loads at the points of the frame ScummVM does (<see cref="FrameLoop"/>).
/// </summary>
public sealed partial class ScriptVm
{
    /// <summary>What the scripts asked for, until the frame loop does it.</summary>
    [field: NonSerialized]
    public SaveLoadRequest PendingSaveLoad { get; private set; }

    /// <summary>The temporary save (<c>room save-game</c>'s), or null before there is one.</summary>
    [field: NonSerialized]
    public ScriptVm TemporaryState { get; private set; }

    /// <summary>A script asked the game to quit (<c>quit</c>, or <c>query-quit</c> from the F1 menu): the host stops it.</summary>
    public event Action QuitRequested;

    /// <summary>A copy of the whole engine state as it is now.</summary>
    public ScriptVm SaveState() => StateGraph.Clone(this);

    /// <summary>
    /// Puts the engine back as <paramref name="saved"/> has it (which stays as it is, so it can be loaded again).
    /// The objects blasted over the room go, as after ScummVM v8's loads, and <see cref="RoomEntered"/> tells the
    /// host which room it now shows (no room code runs).
    /// </summary>
    public void LoadState(ScriptVm saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        StateGraph.Restore(saved, this);
        _blastObjects.Clear();
        BlastObjects = [];
        Guarded("drawing the loaded actors", Actors.Redraw);
        RoomEntered?.Invoke(CurrentRoom); // hosts show the room loaded
    }

    /// <summary>
    /// The frame loop's turn (ScummVM <c>scummLoop_handleSaveLoad</c>): the pending temporary save or load, with
    /// <c>game-loaded</c> 0 before it and 1 after a load. Returns what was done.
    /// </summary>
    public SaveLoadRequest HandleSaveLoad()
    {
        var request = PendingSaveLoad;
        if (request == SaveLoadRequest.None) return request;
        PendingSaveLoad = SaveLoadRequest.None;
        State[EngineVars.GameLoaded] = 0;
        if (request == SaveLoadRequest.Save)
        {
            TemporaryState = SaveState();
            return request;
        }
        if (TemporaryState == null)
        {
            Warned?.Invoke("load-game with nothing saved");
            return SaveLoadRequest.None;
        }
        LoadState(TemporaryState);
        State[EngineVars.GameLoaded] = 1;
        return request;
    }

    /// <summary><c>room save-game</c> (0x5D) and <c>room load-game</c> (0x5E, which takes a sound to keep, ignored here).</summary>
    private bool SaveLoadOps(ScriptInstruction ins)
    {
        switch (ins.SubOp)
        {
            case 0x5D:
                PendingSaveLoad = SaveLoadRequest.Save;
                return true;
            case 0x5E:
                Pop();
                if (PendingSaveLoad == SaveLoadRequest.None) PendingSaveLoad = SaveLoadRequest.Load;
                return true;
        }
        return false;
    }

    /// <summary><c>system-ops</c>: quit (0x29). Restart (0x28) isn't carried out yet.</summary>
    private bool SystemOps(ScriptInstruction ins)
    {
        if (ins.SubOp != 0x29) return false;
        QuitRequested?.Invoke();
        return true;
    }

    /// <summary>kernel set 34, <c>query-quit</c>: ScummVM asks first when confirm_exit is set; the F1 menu's Quit is enough here.</summary>
    private void QueryQuit() => QuitRequested?.Invoke();
}
