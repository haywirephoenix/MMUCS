using System;

namespace MMUCS.Runtime.Scripts;

/// <summary>
/// Turns jiffies into the engine's frames (ScummVM <c>ScummEngine::go</c> and <c>scummLoop</c>). Before each
/// wait the engine reads <c>timer-next</c> (at least 1) and waits that many jiffies; then a frame runs:
/// the timer variables learn how long it was, paused scripts count down by it (at most 15), the mouse
/// variables are set, and every running script gets its turn. COMI sets <c>timer-next</c> to 5, so its
/// scripts run 12 frames a second.
/// <para>
/// Hosts call <see cref="Tick"/> once per jiffy (1/60 s), as <see cref="GameState.Tick"/> does; tests can
/// call <see cref="RunFrame"/> directly. Parts of the frame that other layers will own (input, sentences,
/// actors, drawing) hook in through <see cref="BeforeScripts"/> and <see cref="AfterScripts"/>.
/// </para>
/// </summary>
public sealed class FrameLoop
{
    /// <summary>The most a frame counts paused scripts down by, however long it took (ScummVM caps delta at 15).</summary>
    public const int MaxDelayStep = 15;

    private int _elapsed;

    public FrameLoop(ScriptVm vm)
    {
        Vm = vm ?? throw new ArgumentNullException(nameof(vm));
    }

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

    /// <summary>The mouse on the 640×480 screen, written to the mouse variables each frame.</summary>
    public (int X, int Y) Mouse { get; set; }

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

        BeforeScripts?.Invoke(step);
        state[EngineVars.MouseX] = Mouse.X;
        state[EngineVars.MouseY] = Mouse.Y;
        state[EngineVars.VirtMouseX] = Mouse.X + ScreenOrigin.X;
        state[EngineVars.VirtMouseY] = Mouse.Y + ScreenOrigin.Y;

        Vm.RunAllScripts();
        Frames++;
        AfterScripts?.Invoke(step);
    }
}
