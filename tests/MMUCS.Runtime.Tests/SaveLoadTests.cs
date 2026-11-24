using MMUCS.Core.Scumm;
using MMUCS.Runtime.Scripts;
using Xunit;

namespace MMUCS.Runtime.Tests;

/// <summary>
/// The scripts' temporary save (ScummVM <c>room save-game</c> / <c>room load-game</c>, handled by
/// <c>scummLoop_handleSaveLoad</c>): the frame loop saves the whole engine and puts it back in place, with
/// <c>game-loaded</c> telling the script that saved it which way it came back; quitting stops the game. The
/// game test (MMUCS_GAME) goes through COMI's F1 menu: Return to Game and Quit.
/// </summary>
public class SaveLoadTests
{
    private static string Game => Environment.GetEnvironmentVariable("MMUCS_GAME") is { } g && File.Exists(g) ? g : null;

    [Fact]
    public void A_temporary_save_puts_everything_back_in_the_live_objects_and_the_saving_script_sees_game_loaded()
    {
        // Global 10 saves, then writes game-loaded to var100 when it carries on; global 11 loads.
        var save = new Asm().Sub(0xab, 0x5D).Break().PushVar(EngineVars.GameLoaded).Write(100).Push(7).Write(101).End().Global(10);
        var load = new Asm().Push(0).Sub(0xab, 0x5E).Break().End().Global(11);
        var store = ScriptStore.Of([save, load], [], new Dictionary<int, (int, int)> { [5] = (640, 480) });
        var vm = new ScriptVm(store, new VmState(GameLimits.Comi));
        vm.Faulted += f => throw new Xunit.Sdk.XunitException(f.ToString());
        vm.State[EngineVars.TimerNext] = 1;
        vm.StartScene(5);
        var loop = new FrameLoop(vm);
        int saved = 0, loaded = 0;
        loop.StateSaved += () => saved++;
        loop.StateLoaded += () => loaded++;
        var actor = vm.Actors[3];
        actor.X = 100;
        vm.RunScript(10);

        loop.RunFrame(1); // saved at the frame's start, then global 10 carries on into the game as it was
        Assert.Equal((1, 0, 0, 7), (saved, loaded, vm.State[100], vm.State[101]));
        Assert.NotNull(vm.TemporaryState);

        vm.State[101] = 0;
        vm.State[102] = 9;
        actor.X = 300;
        vm.RunScript(11);
        loop.RunFrame(1); // loaded: global 10 is back where it saved, and finds game-loaded set
        Assert.Equal(1, loaded);
        Assert.Equal((1, 7, 0), (vm.State[100], vm.State[101], vm.State[102]));
        Assert.Same(actor, vm.Actors[3]); // the live objects were restored, not replaced
        Assert.Equal(100, actor.X);
        Assert.DoesNotContain(vm.State.Slots, s => s.IsAlive);

        // The save is left as it was: loading it again goes back once more.
        actor.X = 5;
        vm.LoadState(vm.TemporaryState);
        Assert.Equal(100, actor.X);
        Assert.Equal(10, vm.State.Slots.Single(s => s.IsAlive).Number);

        // A save leaves out what isn't state ([NonSerialized]): it doesn't hold the save before it.
        Assert.Null(vm.SaveState().TemporaryState);
    }

    [Fact]
    public void Quit_and_query_quit_ask_the_host_to_quit()
    {
        var quit = new Asm().Sub(0xb3, 0x29).End().Global(10);
        var query = new Asm().List(34).Op(0xba).End().Global(11);
        var vm = new ScriptVm(ScriptStore.Of([quit, query], []), new VmState(GameLimits.Comi));
        vm.Unhandled += c => throw new Xunit.Sdk.XunitException(c.ToString());
        int asked = 0;
        vm.QuitRequested += () => asked++;
        vm.RunScript(10);
        vm.RunScript(11);
        Assert.Equal(2, asked);
    }

    [Fact]
    public void The_F1_menu_returns_to_the_game_as_it_was_and_its_quit_stops_and_clears_the_game()
    {
        if (Game == null) return;
        var root = ScummProject.Load(ScummProject.FindRelatedFiles(Game), null);
        using var game = new ScriptedGame { BootParam = 9 };
        game.Load(root, Game);
        game.Play();
        void Seconds(double s) { for (int i = 0; i < s * 60; i++) game.Tick(); }
        void Click(int x, int y)
        {
            game.Loop.Mouse = (x, y);
            game.Loop.Press(Scripts.MouseButton.Left);
            Seconds(0.1);
            game.Loop.Release(Scripts.MouseButton.Left);
        }
        Seconds(3);
        Assert.Equal(9, game.CurrentRoom);
        var running = game.Vm.State.Slots.Where(s => s.IsAlive).Select(s => (s.Number, s.Pc)).ToList();
        int musicState = game.Music?.State.SoundId ?? 0;

        game.PressKey(FrameLoop.F1Key);
        Seconds(2);
        Assert.Equal(92, game.CurrentRoom);
        Assert.NotNull(game.Vm.TemporaryState);

        Click(472, 252); // Return to Game
        Seconds(0.2);
        Assert.Equal(9, game.CurrentRoom);
        foreach (var (number, _) in running) Assert.Contains(game.Vm.State.Slots, s => s.IsAlive && s.Number == number);
        Assert.DoesNotContain(game.Vm.State.Slots, s => s.IsAlive && s.Number == 448);
        if (game.Music != null) Assert.Equal(musicState, game.Music.State.SoundId);
        Assert.Equal(0, game.Faults);

        int quits = 0;
        game.Quit += () => quits++;
        game.PressKey(FrameLoop.F1Key);
        Seconds(2);
        Assert.Equal(92, game.CurrentRoom);
        Click(472, 292); // Quit
        Seconds(0.2);
        Assert.Equal(1, quits);
        Assert.False(game.IsPlaying);
        Assert.Equal(0, game.CurrentRoom);
        Assert.Null(game.Vm.TemporaryState);
        Assert.DoesNotContain(game.Vm.State.Slots, s => s.IsAlive);

        game.Play(); // boots afresh
        Seconds(1);
        Assert.Equal(9, game.CurrentRoom);
    }
}
