using MMUCS.Core.Fonts;
using MMUCS.Core.Scumm;
using MMUCS.Runtime.Scripts;
using Xunit;

namespace MMUCS.Runtime.Tests;

/// <summary>
/// Verbs as ScummVM's v7+ verbs.cpp and o8_verbOps run them: the scripts name, place and colour a verb and turn
/// it on; every frame it's drawn as blast text, in its highlight colour under the mouse; a click on it runs
/// the verb script with its number, and a message's %v is its name. Save sets hide verbs and bring them back.
/// The game tests (MMUCS_GAME) check the game fonts' outline and shadow, and talk to Wally in COMI's cannon
/// room: the dialogue choices show, and the one clicked is said.
/// </summary>
public class VerbTests
{
    private static string Game => Environment.GetEnvironmentVariable("MMUCS_GAME") is { } g && File.Exists(g) ? g : null;

    private const int Local = (int)VmState.LocalFlag;

    /// <summary>Sets up verb <paramref name="verb"/> at x,y named <paramref name="name"/>, colour 14, highlight 9, on.</summary>
    private static Asm Verb(Asm a, int verb, int x, int y, string name) => a
        .Push(verb).Sub(0xae, 0x96).Sub(0xae, 0x97)
        .Push(x).Push(y).Sub(0xae, 0x9A)
        .Sub(0xae, 0x99).Text(name)
        .Push(14).Sub(0xae, 0x9D).Push(9).Sub(0xae, 0x9E)
        .Sub(0xae, 0x9B);

    /// <summary>A loop in room 5 whose verb script (global 2) records its locals in var100–102; text is 10 px a letter, 20 high.</summary>
    private static FrameLoop Loop(params ScriptCode[] scripts)
    {
        var verbScript = new Asm().PushVar(Local | 0).Write(100).PushVar(Local | 1).Write(101).PushVar(Local | 2).Write(102).End().Global(2);
        var userputOn = new Asm().Sub(0x9c, 0xE0).Sub(0x9c, 0xDC).End().Global(4);
        var store = ScriptStore.Of([verbScript, userputOn, .. scripts], [], new Dictionary<int, (int, int)> { [5] = (640, 480) });
        var vm = new ScriptVm(store, new VmState(GameLimits.Comi))
        {
            StringWidth = (_, text) => text.Length * 10,
            FontHeight = _ => 20,
        };
        vm.Faulted += f => throw new Xunit.Sdk.XunitException(f.ToString());
        vm.State[EngineVars.VerbScript] = 2;
        vm.State[EngineVars.TimerNext] = 1;
        vm.StartScene(5);
        vm.RunScript(4);
        return new FrameLoop(vm);
    }

    [Fact]
    public void Verbs_the_scripts_put_up_are_drawn_each_frame_highlighted_under_the_mouse_and_a_click_runs_the_verb_script()
    {
        var setup = Verb(Verb(new Asm(), 500, 5, 330, "Hello."), 501, 5, 354, "Aaargh!").End().Global(10);
        var loop = Loop(setup);
        var vm = loop.Vm;
        vm.RunScript(10);
        loop.Mouse = (600, 100);
        loop.Tick();
        Assert.Equal([("Hello.", 5, 330, 14), ("Aaargh!", 5, 354, 14)], vm.ScreenTexts.Select(t => (t.Text, t.X, t.Y, t.Color)));
        Assert.Equal([(500, "Hello.", (5, 330, 65, 350)), (501, "Aaargh!", (5, 354, 75, 374))], vm.VerbsShown);

        loop.Mouse = (30, 360); // over the second
        loop.Tick();
        Assert.Equal([14, 9], vm.ScreenTexts.Select(t => t.Color));
        Assert.Equal(501, vm.VerbIdOf(vm.FindVerbAt(30, 360)));

        loop.Press(MouseButton.Left);
        loop.Tick();
        loop.Release(MouseButton.Left);
        Assert.Equal((ScriptVm.VerbClickArea, 501, 1), (vm.State[100], vm.State[101], vm.State[102]));

        loop.Mouse = (300, 200); // nowhere near a verb: a scene click
        loop.Press(MouseButton.Left);
        loop.Tick();
        loop.Release(MouseButton.Left);
        Assert.Equal((ScriptVm.SceneClickArea, 0), (vm.State[100], vm.State[101]));
    }

    [Fact]
    public void Verb_at_off_dim_and_save_sets_follow_the_engine()
    {
        var put = Verb(new Asm(), 500, 5, 330, "Hello.").End().Global(13);
        var setup = new Asm()
            .Push(5).Push(335).Op(0xdd).Write(300)                          // verb-at: 500 (where the last frame drew it)
            .Push(500).Sub(0xae, 0x96).Sub(0xae, 0xA1)                       // dim
            .Push(500).Push(500).Push(7).Sub(0xb4, 0x8D)                     // save-verbs 500 to 500 set 7: hidden
            .Push(5).Push(335).Op(0xdd).Write(301)                          // verb-at: none
            .End().Global(10);
        var restore = new Asm().Push(500).Push(500).Push(7).Sub(0xb4, 0x8E).End().Global(11);
        var delete = new Asm().Push(500).Push(500).Push(0).Sub(0xb4, 0x8F).End().Global(12);
        var loop = Loop(setup, restore, delete, put);
        var vm = loop.Vm;
        vm.RunScript(13);
        loop.Tick();
        vm.RunScript(10);
        Assert.Equal((500, 0), (vm.State[300], vm.State[301]));
        loop.Tick();
        Assert.Empty(vm.ScreenTexts);

        vm.RunScript(11); // back, still dim: drawn in its dim colour (8 unless set)
        loop.Tick();
        Assert.Equal([("Hello.", 8)], vm.ScreenTexts.Select(t => (t.Text, t.Color)));
        Assert.Equal(0, vm.FindVerbAt(10, 335)); // a dim verb can't be clicked

        vm.RunScript(12);
        loop.Tick();
        Assert.Empty(vm.ScreenTexts);
        Assert.Empty(vm.VerbsShown);
    }

    [Fact]
    public void A_message_s_v_is_the_name_of_the_verb_in_the_variable_and_the_charset_command_sets_talk_s_charset()
    {
        var code = Verb(new Asm(), 501, 5, 354, "/S9SO318/I think I'd rather not.")
            .Push(501).Write(200)
            .Push(3).Sub(0x9c, 0xE7)                                                  // charset 3 for talk lines
            .Push(1).Op(0x91).Bytes([.. System.Text.Encoding.Latin1.GetBytes("/M1GT001/"), 0xFF, 5, .. BitConverter.GetBytes(200), 0]) // say-line 1 "/M1GT001/%v{var200}"
            .End().Global(10);
        var loop = Loop(code);
        var vm = loop.Vm;
        vm.Actors.Put(vm.Actors[1], 100, 300, 5);
        vm.RunScript(10);
        var line = Assert.Single(vm.Subtitles);
        Assert.Equal(("I think I'd rather not.", 3), (line.Text, line.Charset));
    }

    [Fact]
    public void The_game_fonts_say_which_have_an_outline_all_round_and_which_a_drop_shadow()
    {
        if (Game == null) return;
        var fonts = NutFont.LoadAll(Game);
        Assert.Equal((true, true), (fonts[1].OutlineAllRound, fonts[1].HasShadow)); // talk text: outline and shadow
        Assert.Equal((false, true), (fonts[4].OutlineAllRound, fonts[4].HasShadow)); // a shadow only
        Assert.Equal((false, false), (fonts[3].OutlineAllRound, fonts[3].HasShadow)); // no edge at all
    }

    [Fact]
    public void Talking_to_Wally_puts_up_the_dialogue_choices_and_the_one_clicked_is_said()
    {
        if (Game == null) return;
        var root = ScummProject.Load(ScummProject.FindRelatedFiles(Game), null);
        using var game = new ScriptedGame { BootParam = 9 };
        game.Load(root, Game);
        game.Play();
        var vm = game.Vm;
        var loop = game.Loop;
        void Seconds(double s) { for (int i = 0; i < s * 60; i++) game.Tick(); }
        Seconds(24.5);

        // Hold on Wally for the verb coin, and let go over its mouth.
        var wally = vm.Actors[2].Drawn!.Value;
        loop.Mouse = ((wally.Left + wally.Right) / 2, (wally.Top + wally.Bottom) / 2);
        loop.Press(MouseButton.Left);
        Seconds(0.6);
        loop.Mouse = (315, 285);
        Seconds(0.4);
        loop.Release(MouseButton.Left);
        for (int i = 0; i < 60 * 30 && !vm.VerbsShown.Any(); i++) game.Tick();
        var choices = vm.VerbsShown.ToList();
        Assert.Equal(6, choices.Count);
        Assert.Equal((500, "I'm Guybrush Threepwood, who are you?"), (choices[0].Verb, choices[0].Text));
        Assert.All(vm.ScreenTexts.Where(t => choices.Any(c => c.Text == t.Text)), t => Assert.Equal(1, t.Charset));

        var hello = choices.Single(c => c.Text.StartsWith("Hello."));
        loop.Mouse = (hello.Rect.Left + 20, hello.Rect.Top + 5);
        Seconds(0.2);
        loop.Press(MouseButton.Left);
        Seconds(0.1);
        loop.Release(MouseButton.Left);
        for (int i = 0; i < 60 * 5 && !vm.Subtitles.Any(t => t.Text.StartsWith("Hello.")); i++) game.Tick();
        Assert.Contains(vm.Subtitles, t => t.Text == "Hello.  Please don't kill me.");
        Assert.Equal("WGSO003", game.Voice.Playing); // spoken as the choice's own tag, not the say-line's /M1GT001/
        Assert.Equal(0, game.Faults);
    }
}
