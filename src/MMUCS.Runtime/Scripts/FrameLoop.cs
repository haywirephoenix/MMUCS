using System;

namespace MMUCS.Runtime.Scripts;

public enum MouseButton
{
    Left,
    Right,
}

/// <summary>
/// Turns jiffies into the engine's frames (ScummVM <c>ScummEngine::go</c> and <c>scummLoop</c>). Before each
/// wait the engine reads <c>timer-next</c> (at least 1) and waits that many jiffies; then a frame runs:
/// the timer variables learn how long it was, paused scripts count down by it (at most 15), the mouse
/// variables are set, and every running script gets its turn. COMI sets <c>timer-next</c> to 5, so its
/// scripts run 12 frames a second.
/// <para>
/// Input works as ScummVM's <c>processInput</c> and <c>checkExecVerbs</c>: a button pressed since the last
/// frame counts as a click (both at once as Esc), the button variables say what was clicked and what is
/// held, and after the scripts a click or key goes to the verb script while userput is on.
/// </para>
/// <para>
/// Hosts call <see cref="Tick"/> once per jiffy (1/60 s), as <see cref="GameState.Tick"/> does; tests can
/// call <see cref="RunFrame"/> directly. Parts of the frame that other layers will own (input, sentences,
/// actors, drawing) hook in through <see cref="BeforeScripts"/> and <see cref="AfterScripts"/>.
/// </para>
/// </summary>
public sealed class FrameLoop
{
    public FrameLoop(ScriptVm vm)
    {
        Vm = vm ?? throw new ArgumentNullException(nameof(vm));
        Vm.KeyState = KeyState;
        // cursor put: the engine's mouse is where the script put it from now on (the host moves the system pointer to match).
        Vm.MouseWarped += (x, y) => Mouse = (x, y);
    }

    /// <summary>The most a frame counts paused scripts down by, however long it took (ScummVM caps delta at 15).</summary>
    public const int MaxDelayStep = 15;

    private int _elapsed;
    private bool _leftDown, _rightDown, _leftClicked, _rightClicked;

    /// <summary>
    /// Frames a click still counts as held after it's taken, however soon the button came up. The game's
    /// release watcher (global 41) runs the verb script again once the hold ends; ending it in the frame after
    /// the click would cut short the click's own run of the verb script, waiting at a <c>break-here</c> (the F1
    /// menu's slots and page corners), so a quick click was lost. Two frames lets it finish first.
    /// </summary>
    private const int MinHoldFrames = 2;

    private int _leftKeep, _rightKeep;
    private int _key;

    /// <summary>MBS_LEFT_CLICK / MBS_RIGHT_CLICK; below 0x200 the value is a key.</summary>
    private const int LeftClick = 0x8000, RightClick = 0x4000, MaxKey = 0x200;

    /// <summary>The key codes the engine knows besides ASCII: Esc, and F1–F9 as 315–323.</summary>
    public const int EscapeKey = 27, F1Key = 315;

    public ScriptVm Vm { get; }

    /// <summary>Jiffies ticked so far.</summary>
    public long Jiffies { get; private set; }

    /// <summary>Frames run so far.</summary>
    public long Frames { get; private set; }

    /// <summary>
    /// How many jiffies the frame being waited for takes: <c>timer-next</c> as it was when the wait began
    /// (after the last frame, or at the first tick), or 0 between frames before the next tick reads it.
    /// </summary>
    public int Wait { get; private set; }

    /// <summary>How far through the wait for the next frame the jiffies are, from 0 (a frame just ran) towards 1.</summary>
    public double Progress => Wait > 0 ? (double)_elapsed / Wait : 0;

    /// <summary>The mouse on the 640×480 screen, written to the mouse variables each frame (kept within <see cref="MouseRangeX"/> and the screen's height).</summary>
    public (int X, int Y) Mouse { get; set; }

    /// <summary>
    /// How far left and right the mouse goes on the screen (0–639, the engine's screen); wider when more of the
    /// room is shown beside the screen, so the room there can be pointed at.
    /// </summary>
    public (int Min, int Max) MouseRangeX { get; set; } = (0, 639);

    /// <summary>A button went down: it counts as a click in the next frame, and as held until <see cref="Release"/>.</summary>
    public void Press(MouseButton button)
    {
        if (button == MouseButton.Left) _leftDown = _leftClicked = true;
        else _rightDown = _rightClicked = true;
    }

    public void Release(MouseButton button)
    {
        if (button == MouseButton.Left) _leftDown = false;
        else _rightDown = false;
    }

    private bool _videoSkipped;

    /// <summary>
    /// A video was skipped: the next frame skips the cutscene it was in and tells the verb script Esc
    /// (ScummVM v7's <c>processInput</c> after <c>_skipVideo</c>).
    /// </summary>
    public void VideoSkipped() => _videoSkipped = true;

    /// <summary>
    /// A key went down (ASCII, or <see cref="F1Key"/>…): the next frame handles the last one pressed, and it counts
    /// as held (kernel-get 211) until <see cref="ReleaseKey"/>.
    /// </summary>
    public void PressKey(int code)
    {
        _key = code;
        if (code <= 0) return;
        _held.Add(code);
        _fresh.Add(code);
        _letGo.Remove(code);
    }

    /// <summary>
    /// A key came up. One that went down since the last frame stays held through the next one, so the scripts
    /// still see a quick tap held (the cannon's and the ship's Tab fire on it). A letter lets go of both cases, as
    /// Shift may have changed in between.
    /// </summary>
    public void ReleaseKey(int code)
    {
        foreach (int k in code is >= 'a' and <= 'z' or >= 'A' and <= 'Z' ? [code | 0x20, code & ~0x20] : (int[])[code])
        {
            if (_fresh.Contains(k)) _letGo.Add(k);
            else _held.Remove(k);
        }
    }

    /// <summary>Every key lets go at once (the host lost the keyboard).</summary>
    public void ReleaseKeys()
    {
        _held.Clear();
        _fresh.Clear();
        _letGo.Clear();
    }

    private readonly System.Collections.Generic.HashSet<int> _held = [], _fresh = [], _letGo = [];

    /// <summary>The DOS scan code plus 256 of the keypad's arrows and page keys, as the scripts ask for them.</summary>
    public const int HomeKey = 327, UpKey = 328, PageUpKey = 329, LeftKey = 331, RightKey = 333, EndKey = 335, DownKey = 336, PageDownKey = 337;

    /// <summary>
    /// kernel-get 211 (ScummVM <c>getKeyState</c>): whether a key is held. The keypad's keys count as theirs with the
    /// keypad's digit too (Num Lock on), so 331 is Left or 4; any other key is asked for by its own code (9 is Tab).
    /// </summary>
    public bool KeyState(int key) => _held.Contains(key) || key switch
    {
        HomeKey => _held.Contains('7'),
        UpKey => _held.Contains('8'),
        PageUpKey => _held.Contains('9'),
        LeftKey => _held.Contains('4'),
        RightKey => _held.Contains('6'),
        EndKey => _held.Contains('1'),
        DownKey => _held.Contains('2'),
        PageDownKey => _held.Contains('3'),
        _ => false,
    };

    /// <summary>Where the screen's top left is in the room; the virtual mouse is the mouse plus this (the camera moves it).</summary>
    public (int X, int Y) ScreenOrigin { get; set; }

    /// <summary>Runs in each frame before the scripts (ScummVM's input handling goes here), with the frame's jiffies.</summary>
    public event Action<int> BeforeScripts;

    /// <summary>Runs in each frame after the scripts (sentences, actors, the camera and drawing go here).</summary>
    public event Action<int> AfterScripts;

    /// <summary>One jiffy. Runs a frame when the wait is over, and says whether it did.</summary>
    public bool Tick()
    {
        if (_elapsed == 0) Wait = Math.Max(1, Vm.State[EngineVars.TimerNext]);
        Jiffies++;
        if (++_elapsed < Wait) return false;
        _elapsed = 0;
        RunFrame(Wait);
        Wait = 0;
        return true;
    }

    /// <summary>One frame that took <paramref name="jiffies"/> (ScummVM <c>scummLoop(delta)</c>, the script part).</summary>
    public void RunFrame(int jiffies)
    {
        var state = Vm.State;
        state[EngineVars.Timer] = jiffies;
        state[EngineVars.Tmr1] += jiffies;
        state[EngineVars.Tmr2] += jiffies;
        state[EngineVars.Tmr3] += jiffies;

        int step = Math.Min(jiffies, MaxDelayStep);
        Vm.DecreaseDelays(step);
        Vm.TalkTime(step);

        int input = ProcessInput();
        Vm.UpdateCameraVars();
        BeforeScripts?.Invoke(step);

        state[EngineVars.GameLoaded] = 0;
        while (true)
        {
            HandleSaveLoad();
            int clears = Vm.ClickClears;
            Vm.RunAllScripts();
            if (Vm.ClickClears != clears) input = 0; // stop-sentence dropped the click
            CheckExecVerbs(input);
            Vm.CheckAndRunSentenceScript();
            // A load asked for this frame happens at once and the scripts run again from it, so the room left
            // isn't drawn (ScummVM's goto load_game); the loaded game starts with no click.
            if (Vm.PendingSaveLoad is not (SaveLoadRequest.Load or SaveLoadRequest.LoadSlot)) break;
            input = 0;
        }
        if (Vm.CurrentRoom != 0)
        {
            Vm.WalkActors();
            Vm.MoveCamera();
        }
        Vm.UpdateObjectStates();
        Vm.Charset();
        if (Vm.CurrentRoom != 0) Vm.RedrawVerbs(Mouse.X, Mouse.Y); // v7+ redraws the verbs every frame
        Vm.EndFrameTexts();
        if (Vm.CurrentRoom != 0) Vm.ProcessActors();
        Vm.EndFramePalette();
        Vm.ProcessSpeech();
        Frames++;
        AfterScripts?.Invoke(step);
        // The quick taps the frame saw held are let go now.
        foreach (int k in _letGo) _held.Remove(k);
        _letGo.Clear();
    }

    /// <summary>The scripts' save went into <see cref="ScriptVm.TemporaryState"/> (the host saves what it keeps itself, such as the music).</summary>
    public event Action StateSaved;

    /// <summary>The engine was put back as a save had it; the host follows (its sounds, the camera it shows).</summary>
    public event Action StateLoaded;

    /// <summary>
    /// Puts the engine back as a host's own save has it (the quick save), between frames, with the clicks and key
    /// waiting dropped as after the scripts' loads. <see cref="StateLoaded"/> isn't raised: that one follows the
    /// scripts' temporary save, and the host puts its own sounds back.
    /// </summary>
    public void LoadState(ScriptVm saved)
    {
        Vm.LoadState(saved);
        _leftClicked = _rightClicked = false;
        _key = 0;
        ReleaseKeys();
    }

    /// <summary>
    /// The host loads a save slot into the engine (with its sounds); false when it can't, and then the F1 menu's
    /// snapshot is loaded instead, so the menu's Load still goes back to the game.
    /// </summary>
    public Func<int, bool> LoadSlot { get; set; }

    private void HandleSaveLoad()
    {
        switch (Vm.HandleSaveLoad())
        {
            case SaveLoadRequest.Save: StateSaved?.Invoke(); break;
            case SaveLoadRequest.LoadSlot:
                _leftClicked = _rightClicked = false;
                _key = 0;
                if (LoadSlot?.Invoke(Vm.PendingLoadSlot) == true)
                {
                    Vm.State[EngineVars.GameLoaded] = 1; // the save is the F1 snapshot, where the key script waits for it
                    break;
                }
                Vm.Warn($"save-game-read {Vm.PendingLoadSlot}: couldn't load it, so back to the game as it was");
                if (Vm.TemporaryState == null) break;
                Vm.LoadState(Vm.TemporaryState);
                Vm.State[EngineVars.GameLoaded] = 1;
                StateLoaded?.Invoke();
                break;
            case SaveLoadRequest.Load:
                _leftClicked = _rightClicked = false; // ScummVM clearClickedStatus
                _key = 0;
                StateLoaded?.Invoke();
                break;
        }
    }

    /// <summary>ScummVM <c>processInput</c>: the mouse and button variables, then the key. Returns the click or key for the verb script.</summary>
    private int ProcessInput()
    {
        var state = Vm.State;
        Mouse = (Math.Clamp(Mouse.X, MouseRangeX.Min, MouseRangeX.Max), Math.Clamp(Mouse.Y, 0, 479));
        state[EngineVars.MouseX] = Mouse.X;
        state[EngineVars.MouseY] = Mouse.Y;
        state[EngineVars.VirtMouseX] = Mouse.X + ScreenOrigin.X;
        state[EngineVars.VirtMouseY] = Mouse.Y + ScreenOrigin.Y;
        state[EngineVars.DebugMode] = Vm.DebugMode ? 1 : 0;

        int input = 0, key = _key;
        _key = 0;
        _fresh.Clear();
        if (_leftClicked && _rightClicked) key = EscapeKey; // both buttons skip a cutscene (v4+)
        else if (_leftClicked) input = LeftClick;
        else if (_rightClicked) input = RightClick;

        if (_leftClicked) _leftKeep = MinHoldFrames;
        if (_rightClicked) _rightKeep = MinHoldFrames;
        state[EngineVars.LeftButtonHold] = _leftDown || _leftKeep > 0 ? 1 : 0;
        state[EngineVars.RightButtonHold] = _rightDown || _rightKeep > 0 ? 1 : 0;
        if (_leftKeep > 0) _leftKeep--;
        if (_rightKeep > 0) _rightKeep--;
        state[EngineVars.LeftButtonDown] = _leftClicked ? 1 : 0;
        state[EngineVars.RightButtonDown] = _rightClicked ? 1 : 0;
        _leftClicked = _rightClicked = false;

        if (_videoSkipped)
        {
            _videoSkipped = false;
            Vm.AbortCutscene();
            return EscapeKey;
        }

        if (key == 0) return input;
        // v8: the key script's key runs it instead (COMI's F1 menu); v7: Esc skips the cutscene.
        if (Vm.KeyScript != 0 && key == Vm.KeyScriptKey)
        {
            Vm.RunScript(Vm.KeyScript);
            return input;
        }
        if (key == EscapeKey && state[EngineVars.CutsceneExitKey] != 0) Vm.AbortCutscene();
        // The talk-stop key (COMI sets it to '.', and to 0 in the F1 menu) ends the line up now instead of reaching the scripts.
        else if (key == state[EngineVars.TalkStopKey])
        {
            Vm.SkipLine();
            return input;
        }
        return key;
    }

    /// <summary>
    /// ScummVM <c>checkExecVerbs</c>, after the scripts: while userput is on, a key or a click starts the verb
    /// script: a verb's key or a click on a verb with that verb, otherwise as a key or a scene click.
    /// </summary>
    private void CheckExecVerbs(int input)
    {
        if (Vm.UserPutLevel <= 0 || input == 0) return;
        if (input < MaxKey)
        {
            if (Vm.VerbWithKey(input) is int keyed and not 0) Vm.RunInputScript(ScriptVm.VerbClickArea, keyed, 1);
            else Vm.RunInputScript(ScriptVm.KeyClickArea, input, 1);
            return;
        }
        int code = (input & LeftClick) != 0 ? 1 : 2;
        if (Vm.FindVerbAt(Mouse.X, Mouse.Y) is int over and not 0) Vm.RunInputScript(ScriptVm.VerbClickArea, Vm.VerbIdOf(over), code);
        else Vm.RunInputScript(ScriptVm.SceneClickArea, 0, code);
    }
}
