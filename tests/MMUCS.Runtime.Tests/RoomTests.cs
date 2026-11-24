using MMUCS.Core.Scumm;
using MMUCS.Runtime.Scripts;
using Xunit;

namespace MMUCS.Runtime.Tests;

/// <summary>
/// Entering a room runs as ScummVM's startScene: the exit scripts (exit-script, the room's exit code,
/// exit-script2), then the old room's scripts end, then the new room's size and camera limits are set and
/// its entry scripts run (entry-script, its entry code, entry-script2). The game test (MMUCS_GAME) boots
/// COMI into its first room.
/// </summary>
public class RoomTests
{
    private static string Game => Environment.GetEnvironmentVariable("MMUCS_GAME") is { } g && File.Exists(g) ? g : null;

    private const int Local = (int)VmState.LocalFlag;
    private const int Log = 200; // var200[] records the order things ran in; var201 counts them
    private const int VerbScript = 55;

    /// <summary>Code that appends <paramref name="mark"/> to the log.</summary>
    private static Asm Mark(int mark) => new Asm().PushVar(201).Push(mark).Op(0x71, Log).Op(0x6e, 201);

    private static ScriptVm Machine(params ScriptCode[] scripts)
    {
        var store = ScriptStore.Of(scripts, [], new Dictionary<int, (int, int)> { [5] = (1280, 480) });
        var vm = new ScriptVm(store, new VmState(GameLimits.Comi));
        vm.Faulted += f => throw new Xunit.Sdk.XunitException(f.ToString());
        vm.State.DefineArray(Log, ArrayType.Int, 0, 19);
        return vm;
    }

    private static int[] Logged(ScriptVm vm) => vm.State.Array(Log).Ints[..vm.State[201]].ToArray();

    [Fact]
    public void Exit_scripts_then_entry_scripts_run_in_the_engine_s_order()
    {
        var vm = Machine(
            Mark(1).End().Global(10),                 // exit-script
            Mark(2).Op(0x02, (int)EngineVars.NewRoom).Write(210).End().Exit(4), // room 4's exit code sees new-room
            Mark(3).End().Global(11),                 // exit-script2
            Mark(4).PushVar(EngineVars.RoomWidth).Write(211).End().Global(12), // entry-script sees the new size
            Mark(5).End().Entry(5),
            Mark(6).End().Global(13));                // entry-script2
        var s = vm.State;
        (s[EngineVars.ExitScript], s[EngineVars.ExitScript2], s[EngineVars.EntryScript], s[EngineVars.EntryScript2]) = (10, 11, 12, 13);
        var entered = new List<int>();
        vm.RoomEntered += entered.Add;
        vm.CurrentRoom = 4;

        vm.StartScene(5);
        Assert.Equal([1, 2, 3, 4, 5, 6], Logged(vm));
        Assert.Equal([5], entered);
        Assert.Equal((5, 5, 5), (vm.CurrentRoom, s[EngineVars.Room], s[210]));
        Assert.Equal((1280, 480, 1280), (s[EngineVars.RoomWidth], s[EngineVars.RoomHeight], s[211]));
        Assert.Equal((320, 960, 240, 240), (s[EngineVars.CameraMinX], s[EngineVars.CameraMaxX], s[EngineVars.CameraMinY], s[EngineVars.CameraMaxY]));
        Assert.Equal((320, 240), (s[EngineVars.CameraPosX], s[EngineVars.CameraPosY]));

        // Room 0 is no room: nothing is entered, though the exit code still runs.
        vm.StartScene(0);
        Assert.Equal([1, 2, 3, 4, 5, 6, 1, 3], Logged(vm));
        Assert.Equal(0, vm.CurrentRoom);
    }

    [Fact]
    public void Leaving_ends_the_room_s_scripts_but_not_global_ones()
    {
        // In room 4: a looping local script and a looping global one, each counting a variable up.
        var loop = (int v) => new Asm().Label("top").Op(0x6e, v).Break().Jump(0x66, "top");
        var vm = Machine(loop(100).Local(4, 2000), loop(101).Global(20), new Asm().Mark(0).End().Entry(5));
        vm.CurrentRoom = 4;
        vm.RunScript(2000);
        vm.RunScript(20);
        Assert.Equal(2, vm.State.Slots.Count(sl => sl.IsAlive));

        vm.StartScene(5);
        var alive = Assert.Single(vm.State.Slots, sl => sl.IsAlive);
        Assert.Equal(20, alive.Number);
        // The entry code ran (as 10002, in the slot the local script left) and ended.
        Assert.Equal([0], Logged(vm));
    }

    [Fact]
    public void A_local_script_that_changes_room_stops_there_and_a_global_one_carries_on()
    {
        var vm = Machine(
            new Asm().Push(5).Op(0x9d).Push(1).Write(100).End().Local(4, 2000),  // current-room 5, then var100 = 1
            new Asm().Push(5).Op(0x9d).Push(1).Write(101).End().Global(30));
        vm.CurrentRoom = 4;
        vm.RunScript(2000);
        Assert.Equal((5, 0), (vm.CurrentRoom, vm.State[100]));

        vm.CurrentRoom = 4;
        vm.RunScript(30);
        Assert.Equal((5, 1), (vm.CurrentRoom, vm.State[101]));
        Assert.DoesNotContain(vm.State.Slots, sl => sl.IsAlive);
    }

    [Fact]
    public void Come_out_door_enters_the_object_s_room_with_the_ego_at_it_and_walk_to_object_set()
    {
        // Object 7's home room is 5, where its hotspot is 30,40 from its place at 100,200 and actors face
        // south-east (3 × 45°) at it; room 5's entry code records walk-to-object and where the ego is.
        var limits = GameLimits.Comi with { IndexObjects = [.. Enumerable.Range(0, 10).Select(i => ((byte)0, (byte)(i == 7 ? 5 : 0), 0u))] };
        var door = new ObjectPlace(1, 7, 0, 0, 100, 200, 50, 80) { ActorDir = 3, Hotspots = [(30, 40)] };
        var store = ScriptStore.Of([
            new Asm().PushVar(EngineVars.WalkToObject).Write(100).Push(1).Op(0xeb).Write(101).Push(1).Op(0xec).Write(102).End().Entry(5),
            new Asm().Push(7).Push(-1).Push(-1).Op(0x9e).End().Global(1),
            new Asm().Push(7).Push(300).Push(250).Op(0x9e).End().Global(2)], [],
            places: new Dictionary<int, IReadOnlyList<ObjectPlace>> { [5] = [door] });
        var vm = new ScriptVm(store, new VmState(limits));
        vm.State[EngineVars.Ego] = 1;
        var calls = new List<UnhandledCall>();
        vm.Unhandled += calls.Add;
        vm.RunScript(1);
        Assert.Equal((5, 7, 0), (vm.CurrentRoom, vm.State[100], vm.State[EngineVars.WalkToObject]));
        var ego = vm.Actors[1];
        Assert.Equal((130, 240, 5), (vm.State[101], vm.State[102], ego.Room)); // put there before the entry code ran
        Assert.Equal((315, true), (ego.Facing, ego.Visible)); // away from the door: 135 + 180
        Assert.Empty(calls);

        // With somewhere to walk to, the walk is what's left (walking isn't here yet).
        vm.RunScript(2);
        Assert.Equal("loadRoomWithEgo", Assert.Single(calls).Instruction.Name);
    }

    [Fact]
    public void Boot_enters_the_difficulty_room_and_waits_for_a_click()
    {
        if (Game == null) return;
        var root = ScummProject.Load(ScummProject.FindRelatedFiles(Game), null);
        var vm = new ScriptVm(new ScriptStore(root), new VmState(GameLimits.Read(root)));
        var faults = new List<ScriptFault>();
        var entered = new List<int>();
        vm.Faulted += faults.Add;
        vm.RoomEntered += entered.Add;
        vm.Boot();
        var loop = new FrameLoop(vm);
        for (int i = 0; i < 600; i++) loop.Tick();

        Assert.Empty(faults);
        Assert.Equal([0, 87], entered);
        // Room 87's entry code (local 2002) set the sentence and verb scripts, stopped global 39 and runs the
        // cursor loop 2007; local 2005 starts the game once object 1364 or 1365 is clicked.
        Assert.Equal((2004, 2005), (vm.State[EngineVars.SentenceScript], vm.State[VerbScript]));
        Assert.Equal([9, 41, 2007, 28, 111, 65], vm.State.Slots.Where(s => s.IsAlive).Select(s => s.Number));
        Assert.Equal((640, 480), (vm.State[EngineVars.RoomWidth], vm.State[EngineVars.RoomHeight]));
        Assert.NotNull(vm.ObjectCodeOf(1364));
        Assert.NotNull(vm.ObjectCodeOf(1365));
        Assert.Empty(vm.State.Stack);
    }
}

internal static class AsmRoomExtensions
{
    /// <summary>Appends <paramref name="mark"/> to var200[] (var201 counts).</summary>
    public static Asm Mark(this Asm asm, int mark) => asm.PushVar(201).Push(mark).Op(0x71, 200).Op(0x6e, 201);
}
