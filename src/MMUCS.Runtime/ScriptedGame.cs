using System;
using MMUCS.Core.Scumm;
using MMUCS.Runtime.Scripts;

namespace MMUCS.Runtime;

/// <summary>
/// The game run by its own scripts (MMUCS.Runtime.Scripts), for a host to show: it boots the game, turns
/// jiffies into the engine's frames, and takes the host's pointer, buttons and keys. Positions are in game
/// units on the 16:9 canvas, as <see cref="GameState"/> has them; the scripts see the 640×480 page in its
/// middle. <see cref="Changed"/> says when to look again (after a frame, input, or when the game loads).
/// </summary>
public sealed class ScriptedGame
{
    private bool _mouseInside, _booted;

    /// <summary>Something that's shown changed.</summary>
    public event Action Changed;

    public ScummBlock Root { get; private set; }
    public ScriptStore Scripts { get; private set; }
    public ScriptVm Vm { get; private set; }
    public FrameLoop Loop { get; private set; }

    public bool IsLoaded => Vm != null;
    public bool IsPlaying { get; private set; }

    /// <summary>Whether the host has the mouse for the game (its cursor shows and clicks count).</summary>
    public bool IsMouseCaptured { get; private set; }

    /// <summary>Scripts that faulted, most recent last (the game carries on without them).</summary>
    public int Faults { get; private set; }
    public string LastFault { get; private set; }

    /// <summary>The cursor in game units on the canvas.</summary>
    public GamePoint CursorPosition { get; private set; } = new(GameState.CanvasWidth / 2, GameState.CanvasHeight / 2);

    /// <summary>
    /// Where the camera looks in the room (its centre, room pixels), from the camera variables; the page's top
    /// left is half a screen up and left of it.
    /// </summary>
    public GamePoint Camera => Vm == null ? new(320, 240) : new(Vm.State[EngineVars.CameraPosX], Vm.State[EngineVars.CameraPosY]);

    public int CurrentRoom => Vm?.CurrentRoom ?? 0;

    /// <summary>What's happening, for a status line.</summary>
    public string Status => Vm == null ? "Open the game first."
        : !IsPlaying ? "Press Play to boot the game."
        : $"room {CurrentRoom} {Scripts.RoomName(CurrentRoom)} · {Loop.Frames} frames · {Running} scripts" +
          (Faults > 0 ? $" · {Faults} faults (last: {LastFault})" : "");

    private int Running
    {
        get
        {
            int n = 0;
            foreach (var s in Vm.State.Slots) if (s.IsAlive) n++;
            return n;
        }
    }

    /// <summary>Gets the game's scripts and tables ready; <see cref="Play"/> boots it.</summary>
    public void Load(ScummBlock root)
    {
        Root = root ?? throw new ArgumentNullException(nameof(root));
        Scripts = new ScriptStore(root);
        Vm = new ScriptVm(Scripts, new VmState(GameLimits.Read(root) ?? GameLimits.Comi));
        Vm.Faulted += f =>
        {
            Faults++;
            LastFault = f.ToString();
        };
        Loop = new FrameLoop(Vm);
        _booted = false;
        // The camera decides where the page's top left is in the room, for the virtual mouse.
        Loop.BeforeScripts += _ => Loop.ScreenOrigin = (Vm.State[EngineVars.CameraPosX] - 320, Vm.State[EngineVars.CameraPosY] - 240);
        Changed?.Invoke();
    }

    /// <summary>Boots the game (once; Stop leaves it where it is, Play carries on).</summary>
    public void Play()
    {
        if (Vm == null || IsPlaying) return;
        IsPlaying = true;
        if (!_booted)
        {
            _booted = true;
            Vm.Boot();
        }
        Changed?.Invoke();
    }

    public void Stop()
    {
        if (!IsPlaying) return;
        IsPlaying = false;
        SetMouseCaptured(false);
        Changed?.Invoke();
    }

    public void SetMouseCaptured(bool captured)
    {
        captured &= IsPlaying;
        if (captured == IsMouseCaptured) return;
        IsMouseCaptured = captured;
        if (!captured)
        {
            Loop?.Release(MouseButton.Left);
            Loop?.Release(MouseButton.Right);
        }
        Changed?.Invoke();
    }

    /// <summary>Moves the cursor (clamped to the canvas); the scripts see it on the page.</summary>
    public void MoveCursor(GamePoint position)
    {
        CursorPosition = new GamePoint(Math.Clamp(position.X, 0, GameState.CanvasWidth), Math.Clamp(position.Y, 0, GameState.CanvasHeight));
        var page = GameState.PageOrigin;
        _mouseInside = CursorPosition.X >= page.X && CursorPosition.X < page.X + GameState.PageSize.Width;
        if (Loop != null) Loop.Mouse = ((int)Math.Floor(CursorPosition.X - page.X), (int)Math.Floor(CursorPosition.Y - page.Y));
        Changed?.Invoke();
    }

    public void Press(MouseButton button)
    {
        if (IsMouseCaptured) Loop?.Press(button);
    }

    public void Release(MouseButton button) => Loop?.Release(button);

    /// <summary>A key, as the engine numbers it (ASCII; F1–F9 are 315–323).</summary>
    public void PressKey(int code)
    {
        if (IsPlaying) Loop?.PressKey(code);
    }

    /// <summary>One jiffy (1/60 s) while playing; a frame runs every timer-next jiffies.</summary>
    public void Tick()
    {
        if (!IsPlaying || Loop == null) return;
        if (Loop.Tick()) Changed?.Invoke();
    }

    /// <summary>Whether the cursor is over the page (the scripts' screen) rather than the canvas's sides.</summary>
    public bool IsCursorOnPage => _mouseInside;
}
