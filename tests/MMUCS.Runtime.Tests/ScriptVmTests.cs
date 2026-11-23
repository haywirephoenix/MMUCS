using MMUCS.Core.Scumm;
using MMUCS.Runtime.Scripts;
using Xunit;
using Xunit.Abstractions;

namespace MMUCS.Runtime.Tests;

/// <summary>
/// The interpreter runs scripts the way ScummVM's v8 engine does: expressions, jumps, nested starts and
/// calls that hand back a value on the shared stack, breaks, delays, cutscene overrides and freezing. Small
/// hand-assembled scripts check each rule; the boot test (MMUCS_GAME) runs COMI's own boot script.
/// </summary>
public class ScriptVmTests(ITestOutputHelper output)
{
    private static string Game => Environment.GetEnvironmentVariable("MMUCS_GAME") is { } g && File.Exists(g) ? g : null;

    private const int Local = (int)VmState.LocalFlag;

    /// <summary>Writes v8 bytecode: an opcode byte, then 32-bit little-endian operands; jumps count from after their offset.</summary>
    private sealed class Asm
    {
        private readonly List<byte> _code = [];
        private readonly Dictionary<string, int> _labels = [];
        private readonly List<(int At, string Label)> _fixups = [];

        public int Here => _code.Count;

        public Asm Op(byte opcode) { _code.Add(opcode); return this; }
        public Asm Word(int v) { _code.AddRange(BitConverter.GetBytes(v)); return this; }
        public Asm Op(byte opcode, int operand) => Op(opcode).Word(operand);
        public Asm Sub(byte opcode, byte sub, int? operand = null) { Op(opcode); _code.Add(sub); return operand is int o ? Word(o) : this; }
        public Asm Push(int v) => Op(0x01, v);
        public Asm PushVar(int v) => Op(0x02, v);
        public Asm Write(int v) => Op(0x6d, v);
        public Asm List(params int[] items) { foreach (int i in items) Push(i); return Push(items.Length); }
        public Asm Label(string name) { _labels[name] = Here; return this; }
        public Asm Jump(byte opcode, string label) { Op(opcode); _fixups.Add((Here, label)); return Word(0); }
        public Asm Break() => Op(0x67);
        public Asm End() => Op(0x7b);

        public ScriptCode Global(int number) => new() { Kind = ScriptKind.Global, Number = number, Room = 0, Code = Build(), Entry = 0 };

        private byte[] Build()
        {
            var bytes = _code.ToArray();
            foreach (var (at, label) in _fixups)
                BitConverter.TryWriteBytes(bytes.AsSpan(at, 4), _labels[label] - (at + 4));
            return bytes;
        }
    }

    private static (ScriptVm Vm, List<UnhandledCall> Calls, List<ScriptFault> Faults) Machine(params ScriptCode[] scripts)
    {
        var vm = new ScriptVm(ScriptStore.Of(scripts), new VmState(GameLimits.Comi));
        var calls = new List<UnhandledCall>();
        var faults = new List<ScriptFault>();
        vm.Unhandled += calls.Add;
        vm.Faulted += faults.Add;
        return (vm, calls, faults);
    }

    [Fact]
    public void Expressions_follow_the_stack_order()
    {
        var code = new Asm()
            .Push(7).Push(5).Op(0x0f).Write(100)              // 7 − 5
            .Push(7).Push(5).Op(0x0a).Write(101)              // 7 > 5
            .Push(-7).Push(2).Op(0x16).Write(102)             // −7 mod 2 (C: −1)
            .Push(-7).Push(2).Op(0x11).Write(103)             // −7 / 2 (C: −3)
            .Push(3).Op(0x05).Op(0x10).Write(104)             // dup: 3 × 3
            .Push(0).Push(9).Op(0x13).Write(105)              // 0 || 9 → 1
            .Push(6).Push(3).Op(0x14).Write(106)              // 6 & 3
            .Push(-4).Op(0xd6).Write(107)                     // abs
            .Push(5).List(1, 5, 9).Op(0xcd).Write(108)        // 5 in [1, 5, 9]
            .Push(1).List(10, 20, 30).Push(-1).Op(0xcb).Write(109) // pick-one-of(1, [10, 20, 30], default −1)
            .Push(3).List(10, 20, 30).Push(-1).Op(0xcb).Write(110)
            .End().Global(1);
        var (vm, _, faults) = Machine(code);
        vm.RunScript(1);
        Assert.Empty(faults);
        var s = vm.State;
        Assert.Equal((2, 1, -1, -3, 9, 1, 2, 4, 1, 20, -1), (s[100], s[101], s[102], s[103], s[104], s[105], s[106], s[107], s[108], s[109], s[110]));
        Assert.Empty(s.Stack);
    }

    [Fact]
    public void Jumps_loops_and_arrays()
    {
        // for local0 = 0 to 4: var100[local0] = local0 × 10, then sum them into var101.
        var code = new Asm()
            .Push(4).Sub(0x70, 0x0A, 100)                     // dim int var100[4]
            .Push(0).Write(Local | 0)
            .Label("fill")
            .PushVar(Local | 0).PushVar(Local | 0).Push(10).Op(0x10).Op(0x71, 100)
            .Op(0x6e, Local | 0)
            .PushVar(Local | 0).Push(5).Op(0x0b).Jump(0x64, "fill")
            .Push(0).Write(101)
            .Push(0).Write(Local | 0)
            .Label("sum")
            .PushVar(Local | 0).Push(5).Op(0x0d).Jump(0x64, "done")
            .PushVar(101).PushVar(Local | 0).Op(0x03, 100).Op(0x0e).Write(101)
            .Op(0x6e, Local | 0)
            .Jump(0x66, "sum")
            .Label("done")
            .Push(1).Op(0x72, 100)                            // var100[1]++
            .End().Global(1);
        var (vm, _, faults) = Machine(code);
        vm.RunScript(1);
        Assert.Empty(faults);
        Assert.Equal(100, vm.State[101]);
        Assert.Equal([0, 11, 20, 30, 40], vm.State.Array(100).Ints.ToArray());
    }

    [Fact]
    public void A_called_script_hands_its_result_back_on_the_shared_stack()
    {
        // global 2: return local0 + 1. global 1: var100 = call 2(41).
        var callee = new Asm().PushVar(Local | 0).Push(1).Op(0x0e).Op(0x7e).End().Global(2);
        var caller = new Asm().Push(2).List(41).Op(0xc8).Write(100).End().Global(1);
        var (vm, _, faults) = Machine(caller, callee);
        vm.RunScript(1);
        Assert.Empty(faults);
        Assert.Equal(42, vm.State[100]);
        Assert.Empty(vm.State.Stack);
        Assert.DoesNotContain(vm.State.Slots, s => s.IsAlive);
    }

    [Fact]
    public void A_started_script_runs_at_once_until_it_breaks_then_each_frame_in_slot_order()
    {
        // global 2 counts var100 up once a frame; global 1 starts it, then writes var101 = var100.
        var counter = new Asm().Label("top").Op(0x6e, 100).Break().Jump(0x66, "top").Global(2);
        var starter = new Asm().Push(0).Push(2).List().Op(0x79).PushVar(100).Write(101).Break().PushVar(100).Write(102).End().Global(1);
        var (vm, _, faults) = Machine(starter, counter);
        vm.RunScript(1);
        Assert.Equal((1, 1), (vm.State[100], vm.State[101])); // 2 ran to its break before 1 went on
        Assert.True(vm.IsScriptRunning(1) && vm.IsScriptRunning(2));

        vm.RunAllScripts(); // slot 1 (global 1) reads var100 before slot 2 counts it up
        Assert.Equal((2, 1), (vm.State[100], vm.State[102]));
        Assert.False(vm.IsScriptRunning(1));
        vm.RunAllScripts();
        Assert.Equal(3, vm.State[100]);

        // Starting it again (not recursive) replaces the running copy.
        vm.RunScript(2);
        Assert.Single(vm.State.Slots, s => s.IsAlive && s.Number == 2);
        Assert.Empty(faults);
    }

    [Fact]
    public void Stopping_the_caller_from_a_started_script_ends_it_too()
    {
        var stopper = new Asm().Push(1).Op(0x7c).End().Global(2);
        var caller = new Asm().Push(2).List().Op(0x7a).Push(1).Write(100).End().Global(1);
        var (vm, _, faults) = Machine(caller, stopper);
        vm.RunScript(1);
        Assert.Empty(faults);
        Assert.Equal(0, vm.State[100]); // the caller never resumed
        Assert.False(vm.IsScriptRunning(1));
    }

    [Fact]
    public void Delays_count_jiffies_and_delay_frames_counts_frames()
    {
        // Sleeping 10 jiffies wakes once the count goes below 0: three decrements of 5.
        var sleeper = new Asm().Push(10).Op(0x6a).Push(1).Write(100).End().Global(1);
        var (vm, _, _) = Machine(sleeper);
        vm.RunScript(1);
        var slot = vm.State.Slots.First(s => s.Number == 1);
        Assert.Equal(SlotStatus.Paused, slot.Status);
        vm.DecreaseDelays(5);
        vm.DecreaseDelays(5);
        vm.RunAllScripts();
        Assert.Equal(0, vm.State[100]);
        vm.DecreaseDelays(5);
        vm.RunAllScripts();
        Assert.Equal(1, vm.State[100]);

        // delay-frames 3 re-runs itself until its count is used up: on the third frame it goes on.
        var frames = new Asm().Push(3).Op(0x68).Push(1).Write(100).End().Global(1);
        (vm, _, _) = Machine(frames);
        vm.RunScript(1);
        vm.RunAllScripts();
        vm.RunAllScripts();
        Assert.Equal(0, vm.State[100]);
        vm.RunAllScripts();
        Assert.Equal(1, vm.State[100]);
        Assert.Empty(vm.State.Stack);
    }

    [Fact]
    public void Skipping_a_cutscene_resumes_at_its_override_with_the_override_variable_set()
    {
        // cutscene; begin-override; jump skipped; { var100 = 1; break; var100 = 2 } skipped: var101 = override;
        // end-override (COMI reads the override variable at the jump's target, before end-override clears it)
        var code = new Asm()
            .List().Op(0x81)
            .Op(0x84).Jump(0x66, "skipped")
            .Push(1).Write(100).Break().Push(2).Write(100)
            .Label("skipped")
            .PushVar(EngineVars.Override).Write(101)
            .Op(0x85)
            .Op(0x82)
            .End().Global(1);
        var (vm, _, faults) = Machine(code);
        vm.RunScript(1);
        Assert.Equal(1, vm.State[100]); // the jump after begin-override was stepped over
        Assert.Equal(1, vm.CutsceneDepth);
        Assert.True(vm.AbortCutscene());
        vm.RunAllScripts();
        Assert.Equal(1, vm.State[100]);
        Assert.Equal(1, vm.State[101]);
        Assert.Equal(0, vm.State[EngineVars.Override]); // end-override cleared it again
        Assert.Equal(0, vm.CutsceneDepth);
        Assert.Empty(faults);
    }

    [Fact]
    public void Frozen_scripts_wait_until_unfrozen()
    {
        var counter = new Asm().Label("top").Op(0x6e, 100).Break().Jump(0x66, "top").Global(2);
        var freezer = new Asm().Push(0).Push(2).List().Op(0x79).Push(1).Op(0x83).Break().Push(0).Op(0x83).End().Global(1);
        var (vm, _, _) = Machine(freezer, counter);
        vm.RunScript(1); // 2 counts once, then 1 freezes it
        var two = vm.State.Slots.First(s => s.Number == 2);
        Assert.True(two.Frozen);
        vm.RunAllScripts(); // 1 unfreezes 2 before 2's turn in slot order
        Assert.False(two.Frozen);
        Assert.Equal(2, vm.State[100]);
    }

    [Fact]
    public void Instructions_not_carried_out_yet_take_their_arguments_and_return_0()
    {
        var code = new Asm()
            .Push(87).Op(0x9d)                    // current-room 87
            .Push(5).Push(1).Op(0xe2).Op(0x0e).Write(100) // 5 + actor-room(1)
            .Push(0).List(3, 4).Op(0xd8).Write(101) // kernel-get [3, 4] → 0
            .End().Global(1);
        var (vm, calls, faults) = Machine(code);
        vm.RunScript(1);
        Assert.Empty(faults);
        Assert.Equal(["loadRoom(87)", "getActorRoom(1)", "kernelGetFunctions([3, 4])"],
            calls.Select(c => c.ToString()[(c.ToString().IndexOf(": ") + 2)..]));
        Assert.Equal((5, 0), (vm.State[100], vm.State[101]));
        Assert.Equal([0], vm.State.Stack); // the 0 pushed under the list was never used
        Assert.Equal(1, vm.UnhandledCounts["loadRoom"]);

        vm.Strict = true;
        Assert.Throws<ScriptException>(() => vm.RunScript(1));
    }

    [Fact]
    public void Fatal_errors_end_the_script()
    {
        var code = new Asm().Push(1).Push(0).Op(0x11).Push(1).Write(100).End().Global(1);
        var (vm, _, faults) = Machine(code);
        vm.RunScript(1);
        var fault = Assert.Single(faults);
        Assert.Contains("division by zero", fault.Message);
        Assert.Equal(0, vm.State[100]);
        Assert.False(vm.IsScriptRunning(1));
    }

    [Fact]
    public void Boot_sets_up_the_game_and_asks_for_the_opening_room()
    {
        if (Game == null) return;
        var root = ScummProject.Load(ScummProject.FindRelatedFiles(Game), null);
        var state = new VmState(GameLimits.Read(root));
        var vm = new ScriptVm(new ScriptStore(root), state);
        var rooms = new List<int>();
        var faults = new List<ScriptFault>();
        vm.Unhandled += c => { if (c.Instruction.Name == "loadRoom") rooms.Add(c.Args[0][0]); };
        vm.Faulted += faults.Add;

        vm.Boot();
        foreach (var (name, count) in vm.UnhandledCounts.OrderByDescending(kv => kv.Value).Take(12))
            output.WriteLine($"{name} ×{count}");

        Assert.Empty(faults);
        Assert.Equal([0, 87], rooms); // the empty room, then the opening (disk 1)
        Assert.Equal((5, 8, 13), (state[EngineVars.TimerNext], state[303], state[304])); // a frame per 5 jiffies; the hold waits
        Assert.Empty(state.Stack);
        Assert.Equal([9, 41, 39, 28, 111, 21], state.Slots.Where(s => s.IsAlive).Select(s => s.Number));

        // A few seconds of frames: the background scripts keep running without faults and leave the stack clean.
        for (int frame = 0; frame < 600; frame++)
        {
            vm.DecreaseDelays(state[EngineVars.TimerNext]);
            vm.RunAllScripts();
        }
        Assert.Empty(faults);
        Assert.Empty(state.Stack);
    }
}
