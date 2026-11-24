using MMUCS.Core.Scumm;
using MMUCS.Runtime.Scripts;
using Xunit;

namespace MMUCS.Runtime.Tests;

/// <summary>
/// Actors as ScummVM's actor.cpp and akos.cpp run them for v8: put-actor shows an actor in the current room
/// and hides it elsewhere, actorOps sets what the actor-* functions read back, facing is at once and turning
/// takes frames (wait-for-turn waits for it), and actor-at goes by where actors were drawn. The game test
/// (MMUCS_GAME) checks COMI's first scene: the actors its scripts put there, drawn, and their costumes
/// stepping on.
/// </summary>
public class ActorTests
{
    private static string Game => Environment.GetEnvironmentVariable("MMUCS_GAME") is { } g && File.Exists(g) ? g : null;

    private static ScriptVm Machine(params ScriptCode[] scripts)
    {
        var store = ScriptStore.Of(scripts, [], new Dictionary<int, (int, int)> { [5] = (640, 480), [6] = (640, 480) });
        var vm = new ScriptVm(store, new VmState(GameLimits.Comi));
        vm.Faulted += f => throw new Xunit.Sdk.XunitException(f.ToString());
        vm.StartScene(5);
        return vm;
    }

    private static Asm Current(Asm a, int actor) => a.Push(actor).Sub(0xac, 0x7a);

    [Fact]
    public void Put_actor_shows_it_in_the_current_room_and_the_scripts_read_back_what_they_set()
    {
        var code = Current(new Asm(), 3)
            .Push(4).Sub(0xac, 0x6e)                                  // elevation 4
            .Push(7).Push(77).Sub(0xac, 0x7b)                         // variable 7 is 77
            .Push(2).Sub(0xac, 0x7f)                                  // depth (layer) 2
            .Push(3).Sub(0xac, 0x75)                                  // always-zclip 3
            .Push(3).Push(100).Push(200).Push(5).Op(0xa1)             // put-actor 3 at 100,200 in-room 5
            .Push(3).Op(0xe2).Write(300)                              // actor-room
            .Push(3).Op(0xeb).Write(301)                              // x-of
            .Push(3).Op(0xec).Write(302)                              // y-of
            .Push(3).Op(0xe8).Write(303)                              // actor-elevation
            .Push(3).Push(7).Op(0xe1).Write(304)                      // actor-variable(3, 7)
            .Push(3).Op(0xe7).Write(305)                              // actor-layer
            .Push(0).Op(0xe2).Write(306)                              // actor-room(0): 0, as COMI's script 28 relies on
            .End().Global(10);
        var vm = Machine(code);
        vm.RunScript(10);
        var s = vm.State;
        Assert.Equal((5, 100, 200, 4, 77, 2, 0), (s[300], s[301], s[302], s[303], s[304], s[305], s[306]));
        var actor = vm.Actors[3];
        Assert.True(actor.Visible);
        Assert.Equal(3, actor.ForceClip);

        // always-zclip 255 means the walk box's plane, and so does −1: the engine keeps it in a byte.
        foreach (int clip in new[] { 255, -1 })
        {
            var zclip = Current(new Asm(), 3).Push(clip).Sub(0xac, 0x75).Push(3).Op(0xf7).Write(310).End().Global(11);
            var other = Machine(zclip);
            other.RunScript(11);
            Assert.Equal((Actor.BoxClip, 0), (other.Actors[3].ForceClip, other.State[310])); // no boxes here: box 0's plane, none
        }

        // Elsewhere it's out of sight; put back, it shows again.
        vm.Actors.Put(actor, 10, 10, 6);
        Assert.False(actor.Visible);
        vm.Actors.Put(actor, 10, 10, 5);
        Assert.True(actor.Visible);

        // Leaving the room hides every actor; the actors of the room entered come back into sight.
        vm.StartScene(6);
        Assert.False(actor.Visible);
        vm.StartScene(5);
        Assert.True(actor.Visible);
    }

    [Fact]
    public void Facing_is_at_once_and_turning_takes_frames_that_wait_for_turn_waits_out()
    {
        var code = new Asm()
            .Push(3).Push(100).Push(200).Push(5).Op(0xa1)
            .Push(3).Push(3100).Op(0xa4)                              // do-animation 3100: face 100°, the nearest eighth: 90
            .Push(3).Op(0xea).Write(300)
            .Push(3).Push(4180).Op(0xa4)                              // do-animation 4180: turn to 180
            .Label("wait").Push(3).SubJump(0x69, 0x23, "wait")         // wait-for-turn 3
            .Push(3).Op(0xea).Write(301)
            .End().Global(10);
        var vm = Machine(code);
        var loop = new FrameLoop(vm);
        vm.RunScript(10);
        var actor = vm.Actors[3];
        Assert.Equal(90, vm.State[300]);
        Assert.Equal(Actor.Turning, actor.Moving);
        Assert.True(vm.IsScriptRunning(10));

        // A frame to face the new way, one more to stop turning, then the script goes on.
        for (int i = 0; i < 4 && vm.IsScriptRunning(10); i++) loop.RunFrame(5);
        Assert.False(vm.IsScriptRunning(10));
        Assert.Equal((180, 180, 0), (actor.Facing, vm.State[301], actor.Moving));
    }

    [Fact]
    public void Actor_at_finds_the_lowest_numbered_actor_drawn_over_the_point_unless_it_s_untouchable()
    {
        var vm = Machine(new Asm().Push(20).Push(30).Op(0xdb).Write(300).End().Global(10));
        vm.Actors[6].Drawn = (0, 0, 100, 100);
        vm.Actors[4].Drawn = (10, 10, 50, 90);
        vm.RunScript(10);
        Assert.Equal(4, vm.State[300]);
        vm.State.Objects.SetClass(4, ObjectTables.Untouchable, true);
        Assert.Equal(6, vm.ActorAt(20, 30));
        Assert.Equal(0, vm.ActorAt(200, 30));
    }

    [Fact]
    public void Setting_an_actor_up_gives_it_object_0_s_classes_as_the_v7_engine_does()
    {
        // COMI makes object 0 untouchable before setting up overlay actors, so actor-at passes over them.
        var code = Current(new Asm(), 3).Sub(0xac, 0x6D).End().Global(10);   // actor 3 default
        var vm = Machine(code);
        vm.Actors[3].Drawn = (0, 0, 100, 100);
        Assert.Equal(3, vm.ActorAt(50, 50));
        vm.State.Objects.SetClass(0, ObjectTables.Untouchable, true);
        vm.RunScript(10);
        Assert.True(vm.State.Objects.HasClass(3, ObjectTables.Untouchable));
        Assert.Equal(0, vm.ActorAt(50, 50));
    }

    [Fact]
    public void An_actor_variable_past_the_table_is_a_fault()
    {
        var code = Current(new Asm(), 3).Push(27).Push(1).Sub(0xac, 0x7b).End().Global(10);
        var store = ScriptStore.Of(code);
        var vm = new ScriptVm(store, new VmState(GameLimits.Comi));
        var faults = new List<ScriptFault>();
        vm.Faulted += faults.Add;
        vm.RunScript(10);
        Assert.Contains("animation variable 27", Assert.Single(faults).Message);
    }

    [Fact]
    public void COMI_s_first_scene_has_its_actors_drawn_and_their_costumes_stepping_on()
    {
        if (Game == null) return;
        var root = ScummProject.Load(ScummProject.FindRelatedFiles(Game), null);
        using var game = new ScriptedGame();
        game.Load(root);
        game.Play();
        game.SetMouseCaptured(true);
        game.MoveCursor(GameState.PageToGame(new GamePoint(320, 224)));
        for (int i = 0; i < 60; i++) game.Tick();
        game.Press(MouseButton.Left);
        for (int i = 0; i < 6; i++) game.Tick();
        game.Release(MouseButton.Left);
        for (int i = 0; i < 60 * 60 && game.CurrentRoom != 9; i++) game.Tick();
        Assert.Equal(9, game.CurrentRoom); // cannon
        for (int i = 0; i < 60 * 5; i++) game.Tick();

        var actors = game.Vm.Actors;
        var ego = actors[1];
        Assert.True(ego.Visible);
        Assert.Equal((2, 9), (ego.Costume, ego.Room));
        Assert.NotEqual(0, ego.Walkbox); // it follows the boxes and stands in one
        var drawn = actors.Pictures.Select(p => p.Actor).ToList();
        Assert.Contains(1, drawn);
        Assert.All(actors.Pictures, p => Assert.False(p.Frame.IsEmpty));
        Assert.NotNull(ego.Drawn);
        // COMI says always-zclip −1 for Guybrush: he's cut by his box's plane (box 2: plane 1).
        Assert.Equal((Actor.BoxClip, 1), (ego.ForceClip, actors.Pictures.Single(p => p.Actor == 1).ZPlane));

        // Two seconds later some limb has moved on: the costumes animate.
        var before = actors.All.Where(a => a.Room == 9).ToDictionary(a => a.Number, a => a.CurPos.ToArray());
        for (int i = 0; i < 120; i++) game.Tick();
        Assert.Contains(actors.All.Where(a => a.Room == 9), a => before.TryGetValue(a.Number, out var was) && !was.SequenceEqual(a.CurPos));
        Assert.Equal(0, game.Faults);
    }
}
