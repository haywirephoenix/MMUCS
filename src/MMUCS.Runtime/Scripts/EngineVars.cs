namespace MMUCS.Runtime.Scripts;

/// <summary>
/// The global variables the v8 engine itself reads or writes (ScummVM <c>ScummEngine_v8::setupScummVars</c>).
/// The scripts know them by the names in Core's VariableNames; these are the ones the interpreter touches.
/// </summary>
public static class EngineVars
{
    public const int RoomWidth = 1, RoomHeight = 2;
    /// <summary>The mouse on the screen, and in room coordinates (the screen plus where its top left is in the room).</summary>
    public const int MouseX = 3, MouseY = 4, VirtMouseX = 5, VirtMouseY = 6;
    public const int TalkActor = 14;
    /// <summary>1 after <c>begin-override</c>'s cutscene was skipped, 0 otherwise.</summary>
    public const int Override = 30;
    public const int Room = 31, NewRoom = 32;
    /// <summary>The object a room was entered through (<c>come-out-door</c>), while its entry code runs.</summary>
    public const int WalkToObject = 33;
    /// <summary>The camera's position (its centre) and where it's heading, in room coordinates.</summary>
    public const int CameraPosX = 9, CameraPosY = 10, CameraDestX = 11, CameraDestY = 12;
    /// <summary>The jiffies the last frame took.</summary>
    public const int Timer = 34;
    public const int GameLoaded = 40, Language = 41, CurrentDisk = 42;
    public const int EntryScript = 51, EntryScript2 = 52, ExitScript = 53, ExitScript2 = 54;
    public const int SentenceScript = 56;
    /// <summary>The script run whenever an object changes hands, with the object as its first local.</summary>
    public const int InventoryScript = 57;
    public const int CutsceneStartScript = 58, CutsceneEndScript = 59;
    /// <summary>How many jiffies the next frame waits (4 when 0 or less is never used: the engine waits at least 1).</summary>
    public const int TimerNext = 112;
    public const int Tmr1 = 113, Tmr2 = 114, Tmr3 = 115;
    /// <summary>How far the camera's centre may go: half a screen in from the room's edges.</summary>
    public const int CameraMinX = 116, CameraMaxX = 117, CameraMinY = 118, CameraMaxY = 119;
    public const int Ego = 126;
    public const int DefaultTalkDelay = 128;
    /// <summary>Jiffies per character a line stays up.</summary>
    public const int CharInc = 129;
    public const int DebugMode = 130;

    /// <summary>
    /// What the engine sets before the boot script runs (ScummVM <c>resetScummVars</c> for v8): a 640×480
    /// screen, disk 1 (boot goes to room 87, the difficulty choice, on disk 1, and to the options room otherwise), English,
    /// no debug mode, 4 jiffies a character and 60 a line by default. The music and voice bundle flags stay
    /// 0, as ScummVM leaves them.
    /// </summary>
    public static void Reset(VmState state, int language = 0)
    {
        state[RoomWidth] = 640;
        state[RoomHeight] = 480;
        state[DebugMode] = 0;
        state[CharInc] = 4;
        state[TalkActor] = 0;
        state[DefaultTalkDelay] = 60;
        state[CurrentDisk] = 1;
        state[Language] = language;
    }
}
