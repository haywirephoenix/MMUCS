using MMUCS.Core.Scumm;
using MMUCS.Runtime.Scripts;
using Xunit;

namespace MMUCS.Runtime.Tests;

/// <summary>
/// Walking as ScummVM's actor.cpp and boxes.cpp do it for v7+: a walk is kept in the boxes, goes box to box by
/// the box matrix (the room's BOXM, or worked out from the boxes that touch), a step at a time at the walk
/// speed scaled by the actor's scale, turning to each leg and standing at the end. A walk to a box the matrix
/// doesn't reach doesn't set off. Clicks reach the walk through the sentence queue.
/// The game tests (MMUCS_GAME) walk Guybrush across COMI's barbershop, growing as he comes forward, and
/// follow him across the town with the smooth camera.
/// </summary>
public class WalkTests
{
    private static string Game => Environment.GetEnvironmentVariable("MMUCS_GAME") is { } g && File.Exists(g) ? g : null;

    private static Walkbox Box(int index, int left, int top, int right, int bottom, uint flags = 0) =>
        new(index, new(left, top), new(right, top), new(right, bottom), new(left, bottom), 0, flags, 0, 255);

    /// <summary>Boxes 1 and 2 share the edge x = 100; box 3 stands apart.</summary>
    private static readonly Walkbox[] Boxes =
    [
        new(0, new(-32000, -32000), new(-32000, -32000), new(-32000, -32000), new(-32000, -32000), 0, 0, 0, 255),
        Box(1, 0, 0, 100, 100),
        Box(2, 100, 0, 200, 100),
        Box(3, 300, 0, 400, 100),
    ];

    private static ScriptVm Machine(params ScriptCode[] scripts)
    {
        var store = ScriptStore.Of(scripts, [], new Dictionary<int, (int, int)> { [5] = (640, 480) }, boxes: new Dictionary<int, IReadOnlyList<Walkbox>> { [5] = Boxes });
        var vm = new ScriptVm(store, new VmState(GameLimits.Comi));
        vm.Faulted += f => throw new Xunit.Sdk.XunitException(f.ToString());
        vm.StartScene(5);
        return vm;
    }

    /// <summary>Frames of walking until the actor stops (at most <paramref name="max"/>); the places it was at.</summary>
    private static List<(int X, int Y)> WalkOut(ScriptVm vm, Actor a, int max = 200)
    {
        var path = new List<(int, int)>();
        for (int i = 0; i < max && a.IsMoving; i++)
        {
            vm.WalkActors();
            path.Add((a.X, a.Y));
        }
        return path;
    }

    [Fact]
    public void The_box_matrix_is_worked_out_from_the_boxes_that_touch()
    {
        var vm = Machine();
        Assert.Equal((2, 1, -1, 2), (vm.Actors.NextBox(1, 2), vm.Actors.NextBox(2, 1), vm.Actors.NextBox(1, 3), vm.Actors.NextBox(2, 2)));
        Assert.Equal(-1, Walkboxes.NextBox([0xFF, 0xFF, 2, 3, 2, 0xFF], 1, 4)); // box 1 reaches 2 and 3, by 2, not 4
        Assert.Equal(2, Walkboxes.NextBox([0xFF, 0xFF, 2, 3, 2, 0xFF], 1, 3));
    }

    [Fact]
    public void An_actor_walks_box_to_box_at_its_speed_scaled_and_stands_facing_its_way_at_the_end()
    {
        var code = new Asm().Push(1).Push(150).Push(50).Op(0xa0).End().Global(10); // walk-actor 1 to 150,50
        var vm = Machine(code);
        var a = vm.Actors[1];
        vm.Actors.Put(a, 50, 50, 5);
        Assert.Equal(1, a.Walkbox);

        vm.RunScript(10);
        Assert.Equal((150, 50, 2, -1), (a.WalkDestX, a.WalkDestY, a.WalkDestBox, a.WalkDestDir));
        Assert.Equal(Actor.NewLeg, a.Moving);
        var path = WalkOut(vm, a);
        Assert.Equal((150, 50), path[^1]);
        Assert.Equal(new[] { 57, 65, 73 }, path.Take(3).Select(p => p.X)); // a shade under 8 a frame: full size is 255, the fraction carries
        Assert.All(path, p => Assert.Equal(50, p.Y));
        Assert.Equal((2, 90, 0), (a.Walkbox, a.Facing, a.Moving)); // (standing is checked in the game test: without a costume no animation starts)

        // Half size, half the step; a walk to where it is only turns it.
        a.ScaleX = a.ScaleY = 128;
        vm.Actors.StartWalk(a, 50, 50, -1);
        vm.WalkActors();
        Assert.Equal(146, a.X);
        WalkOut(vm, a);
        vm.Actors.StartWalk(a, 50, 50, 180);
        Assert.Equal(Actor.Turning, a.Moving);
    }

    [Fact]
    public void A_walk_to_a_box_out_of_reach_does_not_set_off()
    {
        var vm = Machine();
        var a = vm.Actors[1];
        vm.Actors.Put(a, 50, 50, 5);
        vm.Actors.StartWalk(a, 350, 50, -1);
        Assert.Equal(3, a.WalkDestBox);
        WalkOut(vm, a);
        Assert.Equal((50, 50, 1, 0), (a.X, a.Y, a.Walkbox, a.Moving));

        // The scripts can make box 2 invisible, and set-box-path works the ways out again.
        vm.Actors.SetBoxFlags(2, 0x80);
        vm.Actors.CreateBoxMatrix();
        Assert.Equal(-1, vm.Actors.NextBox(1, 2));
    }

    [Fact]
    public void Scale_slots_the_scripts_set_scale_actors_by_where_they_stand()
    {
        var code = new Asm()
            .List(21, 1, 0, 0, 100, 0, 100, 200).Op(0xba)  // scale slot 1: 100 at y 0, 200 at y 100
            .List(21, 2, 0, 0, 100, 200, 100, 200).Op(0xba) // slot 2 by x and y: the two averaged
            .List(20, 1, 1).Op(0xba)                        // box 1 scales by slot 1
            .End().Global(10);
        var vm = Machine(code);
        var a = vm.Actors[1];
        vm.Actors.Put(a, 50, 50, 5);
        Assert.Equal(255, a.ScaleX); // the box's own scale until then
        vm.RunScript(10);
        vm.Actors.Put(a, 50, 50, 5);
        Assert.Equal((150, 150), (a.ScaleX, a.ScaleY));
        vm.Actors.Put(a, 50, 90, 5);
        Assert.Equal(190, a.ScaleX);

        vm.Actors.SetBoxScaleSlot(1, 2);
        vm.Actors.Put(a, 50, 0, 5); // by x 125, by y 100
        Assert.Equal(112, a.ScaleX);
    }

    [Fact]
    public void A_click_s_sentence_runs_the_sentence_script_once_a_frame_and_stop_sentence_clears_the_queue()
    {
        const int SentenceScript = 40;
        var vm = Machine(
            new Asm().PushVar((int)VmState.LocalFlag).Write(300).PushVar((int)VmState.LocalFlag + 1).Write(301).PushVar((int)VmState.LocalFlag + 2).Write(302)
                .PushVar(303).Push(1).Op(0x0e).Write(303).End().Global(SentenceScript), // records verb, objects and counts its runs
            new Asm().Push(10).Push(500).Push(450).Op(0xa5)                                  // do-sentence 10 500 with 450
                .Push(10).Push(500).Push(450).Op(0xa5)                                       // the same again: ignored
                .Push(9).Push(7).Push(7).Op(0xa5)                                            // both objects the same: ignored
                .Push(11).Push(600).Push(0).Op(0xa5)
                .End().Global(10),
            new Asm().Op(0x86).End().Global(11));                                            // stop-sentence
        vm.State[EngineVars.SentenceScript] = SentenceScript;
        vm.RunScript(10);
        Assert.Equal([(10, 500, 450), (11, 600, 0)], vm.Sentences);

        vm.CheckAndRunSentenceScript(); // the newest first
        Assert.Equal((11, 600, 0, 1), (vm.State[300], vm.State[301], vm.State[302], vm.State[303]));
        vm.CheckAndRunSentenceScript();
        Assert.Equal((10, 500, 450, 2), (vm.State[300], vm.State[301], vm.State[302], vm.State[303]));

        vm.RunScript(10);
        vm.RunScript(11);
        Assert.Empty(vm.Sentences);
    }

    [Fact]
    public void The_smooth_camera_trails_the_engine_s_and_settles_on_it()
    {
        if (Game == null) return;
        var root = ScummProject.Load(ScummProject.FindRelatedFiles(Game), null);
        using var game = new ScriptedGame { BootParam = 15, SmoothCamera = true };
        game.Load(root, Game);
        game.Play();
        for (int i = 0; i < 150; i++) game.Tick();
        Assert.Equal(game.CameraTarget, game.Camera); // entering the room snapped it

        game.Loop.Mouse = (630, 300);
        game.Loop.Press(MouseButton.Left);
        game.Tick();
        game.Loop.Release(MouseButton.Left);
        bool trailed = false;
        for (int i = 0; i < 900; i++)
        {
            game.Tick();
            var (view, engine) = (game.Camera, game.CameraTarget);
            if (view.X != engine.X) trailed = true;
            Assert.InRange(Math.Abs(view.X - engine.X), 0, ScriptedGame.SmoothCameraSnap); // close behind it
        }
        Assert.True(trailed);
        Assert.True(game.CameraTarget.X > GameState.CanvasWidth / 2); // it moved off the town's left edge
        for (int i = 0; i < 600 && game.Camera != game.CameraTarget; i++) game.Tick();
        Assert.Equal(game.CameraTarget, game.Camera);
    }

    [Fact]
    public void The_camera_shown_stays_inside_the_room_when_the_scripts_let_the_engine_s_go_past_it()
    {
        if (Game == null) return;
        var root = ScummProject.Load(ScummProject.FindRelatedFiles(Game), null);
        foreach (bool smooth in new[] { false, true })
        {
            using var game = new ScriptedGame { BootParam = 15, SmoothCamera = smooth };
            game.Load(root, Game);
            game.Play();
            for (int i = 0; i < 150; i++) game.Tick();
            var vm = game.Vm;
            Assert.Equal(2096, vm.State[EngineVars.RoomWidth]);
            vm.State[EngineVars.CameraMaxX] = 5000;
            vm.State[EngineVars.CameraMinX] = -1000;
            vm.CameraAt(4000, 240); // as camera-at does: no longer following Guybrush
            for (int i = 0; i < 300; i++) game.Tick();
            Assert.Equal((4000, 2096 - GameState.CanvasWidth / 2), ((int)game.EngineCamera.X, game.Camera.X)); // the town's right edge less half the canvas
            vm.CameraAt(-500, 240);
            for (int i = 0; i < 300; i++) game.Tick();
            Assert.Equal((-500, GameState.CanvasWidth / 2), ((int)game.EngineCamera.X, game.Camera.X));
        }
    }

    [Fact]
    public void Guybrush_walks_across_the_barbershop_through_the_box_between()
    {
        if (Game == null) return;
        var root = ScummProject.Load(ScummProject.FindRelatedFiles(Game), null);
        using var game = new ScriptedGame { BootParam = 21 };
        game.Load(root, Game);
        game.Play();
        var vm = game.Vm;
        var guybrush = vm.Actors[1];
        for (int i = 0; i < 30; i++) game.Loop.RunFrame(5);
        Assert.Equal((21, 1), (vm.CurrentRoom, guybrush.Walkbox));

        game.Loop.Mouse = (350, 400);
        game.Loop.Press(MouseButton.Left);
        game.Loop.RunFrame(5);
        game.Loop.Release(MouseButton.Left);
        var boxes = new List<int>();
        var scalesInBox1 = new HashSet<int>();
        for (int i = 0; i < 120 && (boxes.Count == 0 || guybrush.IsMoving); i++)
        {
            game.Loop.RunFrame(5);
            if (guybrush.IsMoving && (boxes.Count == 0 || boxes[^1] != guybrush.Walkbox)) boxes.Add(guybrush.Walkbox);
            if (guybrush.Walkbox == 1) scalesInBox1.Add(guybrush.ScaleX);
        }
        Assert.False(guybrush.IsMoving);
        Assert.Equal([1, 2, 3], boxes);
        Assert.True(scalesInBox1.Count > 3, $"box 1 scales by y, so Guybrush grows as he comes forward: {string.Join(",", scalesInBox1)}");
        Assert.Equal(3, guybrush.Walkbox);
        Assert.Equal(guybrush.StandFrame, guybrush.Frame);
        Assert.Equal(0, game.Faults);
    }
}
