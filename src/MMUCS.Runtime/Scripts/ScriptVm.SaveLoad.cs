using System;
using MMUCS.Core.Scumm;

namespace MMUCS.Runtime.Scripts;

/// <summary>A save or load the scripts asked for, carried out by the frame loop (ScummVM <c>_saveLoadFlag</c>).</summary>
public enum SaveLoadRequest
{
    None,
    Save,
    Load,
    /// <summary>A save slot's load (<c>save-game-read</c>, kernel set 27), which the host carries out from its saves.</summary>
    LoadSlot,
}

/// <summary>
/// A save's thumbnail the F1 menu put on the screen (kernel set 28, <c>save-game-stamp-screenshot</c>): slot 0 is
/// the game about to be saved (the F1 snapshot's), the others the slots'. <paramref name="Brightness"/> is of 255
/// (the menu dims the slots the mouse isn't on).
/// </summary>
public readonly record struct SaveStamp(int Slot, int X, int Y, int Width, int Height, int Brightness);

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

    /// <summary>The slot <see cref="SaveLoadRequest.LoadSlot"/> loads.</summary>
    [field: NonSerialized]
    public int PendingLoadSlot { get; private set; }

    /// <summary>The host's saves: a slot's name, or null when the slot is empty (kernel set 25, <c>save-game-read-name</c>).</summary>
    public Func<int, string> SaveGameName { get; set; }

    /// <summary>
    /// The host writes <see cref="TemporaryState"/> (the F1 menu's snapshot, not the menu) to a slot with a name
    /// (kernel set 26, <c>save-game-write</c>). Returns whether it was written.
    /// </summary>
    public Func<int, string, bool> SaveGameWrite { get; set; }

    /// <summary>The game's registry settings (<c>read-registry-value</c>): the value kept, or null when there's none yet.</summary>
    public Func<string, int?> ReadRegistry { get; set; }

    /// <summary>Keeps a registry setting (kernel set 32, <c>write-registry-value</c>): the menu's page, the volumes, the text speed…</summary>
    public Action<string, int> WriteRegistry { get; set; }

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
        if (State[EngineVars.CameraThresholdX] == 0 && State[EngineVars.CameraThresholdY] == 0
            && State[EngineVars.CameraAccelX] == 0 && State[EngineVars.CameraAccelY] == 0)
            EngineVars.ResetCamera(State);
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
        if (request == SaveLoadRequest.LoadSlot) return request; // the frame loop has the host load it
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

    private readonly System.Collections.Generic.List<SaveStamp> _saveStamps = [];

    /// <summary>
    /// The thumbnails the F1 menu has on the screen, in the order stamped, for the host to draw over the room.
    /// The engine draws them once and they stay until drawn over; here one stays until another is stamped in its
    /// place, the menu reads a page's first slot name again (the page is shown afresh) or the room changes.
    /// </summary>
    public System.Collections.Generic.IReadOnlyList<SaveStamp> SaveStamps => _saveStamps;

    /// <summary>
    /// An object's state was set, which redraws it (ScummVM <c>o6_setState</c> marks its rect dirty): the
    /// thumbnails under it are drawn over. The F1 menu turns its pages so, with a 640×480 object.
    /// </summary>
    private void DrawnOver(int obj)
    {
        if (_saveStamps.Count == 0) return;
        foreach (var o in RoomObjects)
        {
            if (o.Id != obj || o.Width <= 0 || o.Height <= 0) continue;
            _saveStamps.RemoveAll(s => s.X < o.X + o.Width && o.X < s.X + s.Width && s.Y < o.Y + o.Height && o.Y < s.Y + s.Height);
        }
    }

    private void StampSaveThumbnail(SaveStamp stamp)
    {
        _saveStamps.RemoveAll(s => s.X == stamp.X && s.Y == stamp.Y);
        _saveStamps.Add(stamp);
    }

    /// <summary>Kernel set 25: a slot's name into string array <paramref name="array"/>; an empty slot leaves it as it is.</summary>
    private void SaveGameReadName(int slot, int array)
    {
        // The menu reads a page's names in order, stamping each slot's thumbnail as it goes: the first is a page shown afresh.
        if ((slot - 1) % 6 == 0) _saveStamps.Clear();
        if (SaveGameName?.Invoke(slot) is not { } name) return;
        var bytes = System.Text.Encoding.Latin1.GetBytes(name);
        var text = new ScriptArray(ArrayType.String, 0, bytes.Length + 1);
        bytes.CopyTo(text.Bytes);
        State.ReplaceArray(array, text);
    }

    /// <summary>Kernel set 26: the F1 menu's snapshot saved to a slot under the name in string array <paramref name="array"/>.</summary>
    private void SaveGameWriteSlot(int slot, int array)
    {
        string name = State.ArrayById(array)?.Text ?? "";
        if (TemporaryState == null) Warned?.Invoke($"save-game-write {slot}: no snapshot to save");
        else if (SaveGameWrite == null) Warned?.Invoke($"save-game-write {slot} \"{name}\": the host keeps no saves");
        else if (!SaveGameWrite(slot, name)) Warned?.Invoke($"save-game-write {slot} \"{name}\" failed");
    }

    /// <summary>Kernel set 27: the slot is loaded at the frame loop's turn, before the temporary load 449 asks for (ScummVM's <c>_saveLoadFlag</c> 2).</summary>
    private void SaveGameReadSlot(int slot)
    {
        PendingLoadSlot = slot;
        PendingSaveLoad = SaveLoadRequest.LoadSlot;
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
