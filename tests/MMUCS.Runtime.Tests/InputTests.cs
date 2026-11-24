using MMUCS.Core.Scumm;
using MMUCS.Runtime.Scripts;
using Xunit;

namespace MMUCS.Runtime.Tests;

/// <summary>
/// Input as ScummVM's processInput and checkExecVerbs: clicks and keys reach the verb script after the
/// frame's scripts while userput is on, the button variables say what was clicked and what is held, both
/// buttons or Esc skip a cutscene, the key script's key runs it; and object-at finds room objects as
/// findObject does. The game test (MMUCS_GAME) clicks through COMI's difficulty choice.
/// </summary>
public class InputTests
{
    private static string Game => Environment.GetEnvironmentVariable("MMUCS_GAME") is { } g && File.Exists(g) ? g : null;

    private const int Local = (int)VmState.LocalFlag;

    /// <summary>
    /// A loop whose verb script (global 2) records its three locals in var100–102 and counts its runs in
    /// var103, with userput on; global 3 records the button variables each frame into var110–113.
    /// </summary>
    private static FrameLoop Loop(IReadOnlyDictionary<int, IReadOnlyList<ObjectPlace>> places = null, params ScriptCode[] more)
    {
        var verbScript = new Asm()
            .PushVar(Local | 0).Write(100).PushVar(Local | 1).Write(101).PushVar(Local | 2).Write(102).Op(0x6e, 103)
            .End().Global(2);
        var buttons = new Asm().Label("top")
            .PushVar(EngineVars.LeftButtonDown).Write(110).PushVar(EngineVars.LeftButtonHold).Write(111)
            .PushVar(EngineVars.RightButtonDown).Write(112).PushVar(EngineVars.RightButtonHold).Write(113)
            .Break().Jump(0x66, "top").Global(3);
        var userputOn = new Asm().Sub(0x9c, 0xE0).Sub(0x9c, 0xDC).End().Global(4);
        var store = ScriptStore.Of([verbScript, buttons, userputOn, .. more], [], null, places);
        var vm = new ScriptVm(store, new VmState(GameLimits.Comi)) { CurrentRoom = 0 };
        vm.Faulted += f => throw new Xunit.Sdk.XunitException(f.ToString());
        vm.State[EngineVars.VerbScript] = 2;
        vm.State[EngineVars.TimerNext] = 1;
        vm.RunScript(4);
        vm.RunScript(3);
        return new FrameLoop(vm);
    }

    [Fact]
    public void Clicks_and_keys_reach_the_verb_script_with_area_value_and_button()
    {
        var loop = Loop();
        var s = loop.Vm.State;
        Assert.Equal((1, 1), (s[EngineVars.UserPut], s[EngineVars.CursorState]));

        loop.Press(MouseButton.Left);
        loop.Tick();
        Assert.Equal((ScriptVm.SceneClickArea, 0, 1, 1), (s[100], s[101], s[102], s[103]));
        Assert.Equal((1, 1), (s[110], s[111])); // clicked this frame, held

        loop.Tick(); // still held, no new click
        Assert.Equal((0, 1, 1), (s[110], s[111], s[103]));
        loop.Release(MouseButton.Left);
        loop.Tick();
        Assert.Equal(0, s[111]);

        loop.Press(MouseButton.Right);
        loop.Release(MouseButton.Right); // a click shorter than a frame still counts
        loop.Tick();
        Assert.Equal((ScriptVm.SceneClickArea, 0, 2, 2), (s[100], s[101], s[102], s[103]));
        Assert.Equal((1, 0), (s[112], s[113]));

        loop.PressKey('i');
        loop.Tick();
        Assert.Equal((ScriptVm.KeyClickArea, 'i', 1, 3), (s[100], s[101], s[102], s[103]));
    }

    [Fact]
    public void Nothing_reaches_the_verb_script_while_userput_is_off()
    {
        var off = new Asm().Sub(0x9c, 0xE3).End().Global(5); // userput soft-off: 1 → 0
        var loop = Loop(null, off);
        loop.Vm.RunScript(5);
        Assert.Equal(0, loop.Vm.UserPutLevel);
        loop.Press(MouseButton.Left);
        loop.Tick();
        Assert.Equal(0, loop.Vm.State[103]);
        Assert.Equal(1, loop.Vm.State[110]); // the button variables still say so
    }

    [Fact]
    public void Both_buttons_or_esc_skip_the_cutscene_and_the_key_script_takes_its_key()
    {
        // global 6: cutscene with an override; global 7 is the key script for F1.
        var cutscene = new Asm()
            .List().Op(0x81).Op(0x84).Jump(0x66, "skipped")
            .Label("wait").Break().Jump(0x66, "wait")
            .Label("skipped").PushVar(EngineVars.Override).Write(120).Op(0x85).Op(0x82).End().Global(6);
        var keyScript = new Asm().Op(0x6e, 121).End().Global(7);
        var setKey = new Asm().List(29, FrameLoop.F1Key, 7).Op(0xba).End().Global(8);
        var loop = Loop(null, cutscene, keyScript, setKey);
        var s = loop.Vm.State;
        s[EngineVars.CutsceneExitKey] = FrameLoop.EscapeKey;
        loop.Vm.RunScript(8);
        Assert.Equal((FrameLoop.F1Key, 7), (loop.Vm.KeyScriptKey, loop.Vm.KeyScript));

        loop.Vm.RunScript(6);
        loop.Press(MouseButton.Left);
        loop.Press(MouseButton.Right);
        loop.Tick();
        Assert.Equal(1, s[120]); // skipped: the override variable was set when it resumed
        Assert.Equal((ScriptVm.KeyClickArea, FrameLoop.EscapeKey), (s[100], s[101])); // and Esc went on to the verb script

        loop.PressKey(FrameLoop.F1Key);
        loop.Tick();
        Assert.Equal(1, s[121]);
        Assert.Equal(1, s[103]); // F1 ran the key script, not the verb script
    }

    [Fact]
    public void Object_at_skips_untouchable_objects_and_ones_whose_parent_is_in_the_wrong_state()
    {
        // Slot 1: object 10 at 100,100 20×20. Slot 2: object 11, same place, shown only while 10 is in
        // state 2. Slot 3: object 12 elsewhere, untouchable.
        IReadOnlyList<ObjectPlace> room =
        [
            new(1, 10, 0, 0, 100, 100, 20, 20),
            new(2, 11, 1, 2, 100, 100, 20, 20),
            new(3, 12, 0, 0, 300, 300, 10, 10),
        ];
        var loop = Loop(new Dictionary<int, IReadOnlyList<ObjectPlace>> { [9] = room });
        var vm = loop.Vm;
        vm.StartScene(9);
        vm.State.Objects.SetClass(12, ObjectTables.Untouchable, true);

        Assert.Equal(10, vm.FindObject(100, 100));
        Assert.Equal(10, vm.FindObject(120, 120)); // COMI's edges count
        Assert.Equal(0, vm.FindObject(121, 120));
        Assert.Equal(0, vm.FindObject(305, 305));

        // With 10 untouchable, 11 counts once its parent's state is 2, as the room's slots see it: a frame later.
        vm.State.Objects.SetClass(10, ObjectTables.Untouchable, true);
        vm.State.Objects.SetState(10, 2);
        Assert.Equal(0, vm.FindObject(110, 110));
        loop.Tick();
        Assert.Equal(11, vm.FindObject(110, 110));
    }

    [Fact]
    public void Cursor_commands_set_the_levels_image_and_hotspot()
    {
        // cursor soft-off twice, soft-on once; image object 105 frame 2; hotspot 3,4
        var code = new Asm().Sub(0x9c, 0xDF).Sub(0x9c, 0xDF).Sub(0x9c, 0xDE)
            .Push(105).Push(2).Sub(0x9c, 0xE4).Push(3).Push(4).Sub(0x9c, 0xE5).End().Global(9);
        var loop = Loop(null, code);
        loop.Vm.RunScript(9);
        Assert.Equal((0, 0), (loop.Vm.CursorLevel, loop.Vm.State[EngineVars.CursorState]));
        Assert.Equal((105, 2), (loop.Vm.CursorImage.Object, loop.Vm.CursorImage.Image));
        Assert.Equal((3, 4), loop.Vm.CursorHotspot);
    }

    [Fact]
    public void Clicking_mega_monkey_on_the_difficulty_choice_starts_the_game()
    {
        if (Game == null) return;
        var root = ScummProject.Load(ScummProject.FindRelatedFiles(Game), null);
        var store = new ScriptStore(root);
        var vm = new ScriptVm(store, new VmState(GameLimits.Read(root)));
        var faults = new List<ScriptFault>();
        var entered = new List<int>();
        vm.Faulted += faults.Add;
        vm.RoomEntered += entered.Add;
        vm.Boot();
        var loop = new FrameLoop(vm);

        // Room 87's objects: the two buttons (hard 1364 below easy 1365), the choice text and a placard.
        Assert.Equal([1364, 1365, 1366, 1367], vm.RoomObjects.Where(o => !vm.IsFloating(o.Id)).Select(o => o.Id)); // its own (the interface brings in floating ones)
        var hard = vm.RoomObjects[0];
        Assert.Equal((64, 176, 512, 96), (hard.X, hard.Y, hard.Width, hard.Height));
        Assert.Equal((FrameLoop.F1Key, 448), (vm.KeyScriptKey, vm.KeyScript)); // boot's F1 menu

        for (int i = 0; i < 60; i++) loop.Tick();
        loop.Mouse = (hard.X + hard.Width / 2, hard.Y + hard.Height / 2);
        loop.Press(MouseButton.Left);
        for (int i = 0; i < 6; i++) loop.Tick();
        loop.Release(MouseButton.Left);
        for (int i = 0; i < 60 * 5 && vm.CurrentRoom == 87; i++) loop.Tick();

        Assert.Empty(faults);
        Assert.Equal([0, 87, 2], entered); // global 445, then 7 and 122 leave for room 2 (thx) about 3.5 s later
        Assert.Equal("thx", store.RoomName(2));
        Assert.Equal(0, vm.UserPutLevel);
        Assert.Empty(vm.State.Stack);
    }
}
