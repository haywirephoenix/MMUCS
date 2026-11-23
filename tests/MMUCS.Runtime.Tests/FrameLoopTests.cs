using MMUCS.Core.Scumm;
using MMUCS.Runtime.Scripts;
using Xunit;

namespace MMUCS.Runtime.Tests;

/// <summary>
/// Jiffies become frames as in ScummVM's main loop: a frame each <c>timer-next</c> jiffies (read when the
/// wait begins, at least 1), the timer variables count real jiffies while delays count down at most 15
/// per frame, and the mouse variables are set before the scripts run. The boot test needs MMUCS_GAME.
/// </summary>
public class FrameLoopTests
{
    private static string Game => Environment.GetEnvironmentVariable("MMUCS_GAME") is { } g && File.Exists(g) ? g : null;

    private static FrameLoop Loop(params ScriptCode[] scripts)
    {
        return new FrameLoop(new ScriptVm(ScriptStore.Of(scripts), new VmState(GameLimits.Comi)));
    }

    /// <summary>Ticks <paramref name="jiffies"/> and returns at which of them (1-based) a frame ran.</summary>
    private static List<int> Tick(FrameLoop loop, int jiffies)
    {
        var frames = new List<int>();
        for (int i = 1; i <= jiffies; i++)
            if (loop.Tick()) frames.Add(i);
        return frames;
    }

    [Fact]
    public void A_frame_runs_every_timer_next_jiffies()
    {
        var loop = Loop();
        var state = loop.Vm.State;
        state[EngineVars.TimerNext] = 5;
        Assert.Equal([5, 10, 15], Tick(loop, 17));
        Assert.Equal((17L, 3L), (loop.Jiffies, loop.Frames));
        Assert.Equal(5, state[EngineVars.Timer]);
        Assert.Equal((15, 15, 15), (state[EngineVars.Tmr1], state[EngineVars.Tmr2], state[EngineVars.Tmr3]));

        // A change counts from the next wait: the one under way (2 of 5 jiffies in) keeps its length.
        state[EngineVars.TimerNext] = 2;
        Assert.Equal([3, 5, 7], Tick(loop, 7));
        Assert.Equal(2, state[EngineVars.Timer]);

        // 0 or less still waits a jiffy.
        state[EngineVars.TimerNext] = 0;
        Assert.Equal([1, 2, 3], Tick(loop, 3));
    }

    [Fact]
    public void Delays_count_down_at_most_15_a_frame_while_the_timers_count_every_jiffy()
    {
        // sleep-for 20 jiffies, then var100 = tmr1
        var code = new Asm().Push(20).Op(0x6a).PushVar(EngineVars.Tmr1).Write(100).End().Global(1);
        var loop = Loop(code);
        var state = loop.Vm.State;
        loop.Vm.RunScript(1);
        state[EngineVars.TimerNext] = 30;

        Tick(loop, 30); // 20 − 15 = 5 left
        Assert.Equal(0, state[100]);
        Tick(loop, 30); // 5 − 15 < 0: runs, 60 jiffies in
        Assert.Equal(60, state[100]);
    }

    [Fact]
    public void The_mouse_variables_are_set_before_the_scripts_run()
    {
        // A slot waiting to run (not started through RunScript, which would run it at once).
        var code = new Asm().PushVar(EngineVars.MouseX).Write(100).PushVar(EngineVars.VirtMouseX).Write(101).PushVar(EngineVars.VirtMouseY).Write(102).End().Global(1);
        var loop = Loop(code);
        var state = loop.Vm.State;
        state.FreeSlot().Start(code);
        var order = new List<string>();
        loop.BeforeScripts += _ => order.Add($"before {state[100]}");
        loop.AfterScripts += _ => order.Add($"after {state[100]}");

        loop.Mouse = (100, 200);
        loop.ScreenOrigin = (320, 0);
        Assert.True(loop.Tick());
        Assert.Equal((100, 420, 200), (state[100], state[101], state[102]));
        Assert.Equal(["before 0", "after 100"], order);
    }

    [Fact]
    public void The_game_runs_ten_seconds_after_boot_at_12_frames_a_second()
    {
        if (Game == null) return;
        var root = ScummProject.Load(ScummProject.FindRelatedFiles(Game), null);
        var vm = new ScriptVm(new ScriptStore(root), new VmState(GameLimits.Read(root)));
        var faults = new List<ScriptFault>();
        vm.Faulted += faults.Add;
        vm.Boot();
        var loop = new FrameLoop(vm);

        Tick(loop, 600);
        Assert.Equal(120, loop.Frames);
        Assert.Empty(faults);
        Assert.Empty(vm.State.Stack);
        Assert.Equal(600, vm.State[EngineVars.Tmr1]);
        Assert.Equal([9, 41, 2007, 28, 111, 65], vm.State.Slots.Where(s => s.IsAlive).Select(s => s.Number));
    }
}
