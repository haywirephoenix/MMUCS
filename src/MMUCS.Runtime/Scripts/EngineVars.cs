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
    /// <summary>The cursor's and userput's levels (on when above 0), copied after every cursor command.</summary>
    public const int CursorState = 7, UserPut = 8;
    public const int TalkActor = 14, HaveMessage = 15;
    /// <summary>Set for the frame a button went down in; held while it stays down.</summary>
    public const int LeftButtonDown = 16, RightButtonDown = 17, LeftButtonHold = 18, RightButtonHold = 19;
    /// <summary>1 after <c>begin-override</c>'s cutscene was skipped, 0 otherwise.</summary>
    public const int Override = 30;
    /// <summary>The clock as <c>get-date-time</c> last read it: years since 1900, months from 0, then day, hour, minute, second.</summary>
    public const int TimedateYear = 24, TimedateMonth = 25, TimedateDay = 26, TimedateHour = 27, TimedateMinute = 28, TimedateSecond = 29;
    public const int Room = 31, NewRoom = 32;
    /// <summary>The object a room was entered through (<c>come-out-door</c>), while its entry code runs.</summary>
    public const int WalkToObject = 33;
    /// <summary>The camera's position (its centre) and where it's heading, in room coordinates.</summary>
    public const int CameraPosX = 9, CameraPosY = 10, CameraDestX = 11, CameraDestY = 12;
    /// <summary>The actor the camera follows, 0 for none.</summary>
    public const int CameraFollowedActor = 13;
    /// <summary>The jiffies the last frame took.</summary>
    public const int Timer = 34;
    /// <summary>How lines are given: 0 speech only, 1 speech and subtitles, 2 subtitles only (the F1 menu's and Ctrl+T's).</summary>
    public const int VoiceMode = 39;
    public const int GameLoaded = 40, Language = 41, CurrentDisk = 42;
    /// <summary>The script run whenever the camera moves.</summary>
    public const int ScrollScript = 50;
    public const int EntryScript = 51, EntryScript2 = 52, ExitScript = 53, ExitScript2 = 54;
    /// <summary>The script clicks and keys go to (ScummVM <c>runInputScript</c>): locals click area, key or verb, button.</summary>
    public const int VerbScript = 55;
    public const int SentenceScript = 56;
    /// <summary>The script run whenever an object changes hands, with the object as its first local.</summary>
    public const int InventoryScript = 57;
    public const int CutsceneStartScript = 58, CutsceneEndScript = 59;
    /// <summary>When not 0, Esc skips cutscenes.</summary>
    public const int CutsceneExitKey = 62;
    /// <summary>The key that pauses the game (COMI: space, 32; 0 where it mustn't, as under water) and the one that ends the line up ('.', 46).</summary>
    public const int PauseKey = 64, TalkStopKey = 67;
    /// <summary>How many jiffies the next frame waits (4 when 0 or less is never used: the engine waits at least 1).</summary>
    public const int TimerNext = 112;
    public const int Tmr1 = 113, Tmr2 = 114, Tmr3 = 115;
    /// <summary>How far the camera's centre may go: half a screen in from the room's edges.</summary>
    public const int CameraMinX = 116, CameraMaxX = 117, CameraMinY = 118, CameraMaxY = 119;
    /// <summary>The camera's speed (pixels a frame, at most 8) and how much it speeds up each frame (hundredths).</summary>
    public const int CameraSpeedX = 120, CameraSpeedY = 121, CameraAccelX = 122, CameraAccelY = 123;
    /// <summary>How far the followed actor may get from the camera's centre before it moves.</summary>
    public const int CameraThresholdX = 124, CameraThresholdY = 125;
    public const int Ego = 126;
    public const int DefaultTalkDelay = 128;
    /// <summary>Jiffies per character a line stays up.</summary>
    public const int CharInc = 129;
    public const int DebugMode = 130;
    /// <summary>The key that answered the last system message (ScummVM <c>VAR_KEYPRESS</c>, v8).</summary>
    public const int Keypress = 132;
    /// <summary>Milliseconds the lip sync reads ahead of the speech (beyond the engine's own 50).</summary>
    public const int Sync = 134;

    /// <summary>
    /// What the engine sets before the boot script runs (ScummVM <c>resetScummVars</c> for v8): a 640×480
    /// screen, disk 1 (boot goes to room 87, the difficulty choice, on disk 1, and to the options room otherwise), English,
    /// no debug mode, 4 jiffies a character and 60 a line by default, and the v7+ camera ScummVM's
    /// <c>resetScumm</c> sets up (no script sets it): thresholds of 100 and 70, accel 100 a frame, without which
    /// a <c>pan-camera-to</c> never moves and its <c>wait-for-camera</c> never ends. The music and voice bundle
    /// flags stay 0, as ScummVM leaves them.
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
        ResetCamera(state);
    }

    /// <summary>
    /// The camera's thresholds and accel as ScummVM's <c>resetScumm</c> sets them (v7+). A save from before the
    /// engine set them has them all 0 and gets them on load, as ScummVM upgrades its own old saves.
    /// </summary>
    public static void ResetCamera(VmState state)
    {
        state[CameraThresholdX] = 100;
        state[CameraThresholdY] = 70;
        state[CameraAccelX] = 100;
        state[CameraAccelY] = 100;
    }
}
