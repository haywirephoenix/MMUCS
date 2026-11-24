using MMUCS.Core.Scumm;
using MMUCS.Runtime.Scripts;
using Xunit;

namespace MMUCS.Runtime.Tests;

/// <summary>
/// Talking and printing as ScummVM's v7+ engine does them: a line goes up with its speech and the actor's
/// talk animation, stays for the talk delay and while its speech plays, then goes; have-message says a line
/// is up; print-line's parts style it; blasted text lasts a frame. The game test (MMUCS_GAME) hears the
/// first scene's first line.
/// </summary>
public class TextTests
{
    private static string Game => Environment.GetEnvironmentVariable("MMUCS_GAME") is { } g && File.Exists(g) ? g : null;

    private sealed class FakeSpeech : ISpeech
    {
        public readonly List<string> Started = [];
        public bool IsPlaying { get; set; }
        public void Start(string tag, int actor) { Started.Add($"{tag} by {actor}"); IsPlaying = true; }
        public void Stop() => IsPlaying = false;
        public int AskedAhead = -1;
        public (int, int) LipSync(int aheadMs) { AskedAhead = aheadMs; return IsPlaying ? (0x40, 0x27) : (0, 0); }
    }

    private static (ScriptVm Vm, FrameLoop Loop, FakeSpeech Speech) Machine(params ScriptCode[] scripts)
    {
        var store = ScriptStore.Of(scripts, [], new Dictionary<int, (int, int)> { [5] = (640, 480) });
        var vm = new ScriptVm(store, new VmState(GameLimits.Comi));
        vm.Faulted += f => throw new Xunit.Sdk.XunitException(f.ToString());
        var speech = new FakeSpeech();
        vm.Speech = speech;
        vm.StartScene(5);
        vm.State[EngineVars.DefaultTalkDelay] = 60;
        vm.State[EngineVars.CharInc] = 4;
        return (vm, new FrameLoop(vm), speech);
    }

    [Fact]
    public void Leaving_the_room_ends_the_line_and_its_speech_at_once()
    {
        // As ScummVM's startScene does first: the F1 menu taking the screen cuts the line being said.
        var code = new Asm()
            .Push(3).Push(100).Push(300).Push(5).Op(0xa1)
            .Push(3).Op(0x91).Text("/cngt001/Hello there")
            .End().Global(10);
        var (vm, _, speech) = Machine(code);
        vm.RunScript(10);
        Assert.True(speech.IsPlaying);
        Assert.Single(vm.Subtitles);

        vm.StartScene(5);
        Assert.False(speech.IsPlaying);
        Assert.Empty(vm.Subtitles);
        Assert.Equal((0, 0, 0), (vm.State[EngineVars.TalkActor], vm.State[EngineVars.HaveMessage], vm.TalkDelay));
    }

    [Fact]
    public void A_line_goes_up_with_its_speech_and_talk_animation_and_goes_when_both_are_over()
    {
        // Actor 3 talks in colour 7, its talk script (20) records the animations it's asked for; then the
        // script waits for the message and marks that it went on.
        var talkScript = new Asm().PushVar(201).PushVar((int)VmState.LocalFlag + 1).Op(0x71, 200).Op(0x6e, 201).End().Global(20);
        var code = new Asm()
            .Push(3).Push(100).Push(300).Push(5).Op(0xa1)
            .Push(3).Sub(0xac, 0x7a).Push(7).Sub(0xac, 0x70).Push(20).Sub(0xac, 0x84)
            .Push(3).Op(0x91).Text("/cngt001/Hello there")
            .Sub(0x69, 0x1F) // wait-for-message: tries again each frame while a line is up
            .Push(1).Write(210).End().Global(10);
        var (vm, loop, speech) = Machine(code, talkScript);
        vm.State.DefineArray(200, ArrayType.Int, 0, 9);
        vm.RunScript(10);

        Assert.Equal(["CNGT001 by 3"], speech.Started);
        var line = Assert.Single(vm.Subtitles);
        Assert.Equal(("Hello there", 7), (line.Text, line.Color));
        Assert.False(line.OverActor); // the scripts didn't ask for talk lines overhead: it's placed on the screen
        Assert.Equal((3, 1), (vm.State[EngineVars.TalkActor], vm.State[EngineVars.HaveMessage]));
        Assert.Equal(60 + 11 * 4, vm.TalkDelay); // 60 + 4 per letter
        // The talk-start animation through the talk script: from actorTalk and again as the line goes up (the
        // engine asks twice; an actor already in it would skip the second).
        Assert.Equal([4, 4], vm.State.Array(200).Ints[..2].ToArray());

        // The delay runs out, but the speech goes on: the line stays.
        for (int i = 0; i < 25; i++) loop.RunFrame(5);
        Assert.Equal(0, vm.TalkDelay);
        Assert.Equal(1, vm.State[EngineVars.HaveMessage]);
        Assert.Equal(0, vm.State[210]);

        speech.IsPlaying = false; // the speech is over: the line goes, the actor stops talking, the script goes on
        loop.RunFrame(5);
        loop.RunFrame(5);
        Assert.Empty(vm.Subtitles);
        Assert.Equal((0, 0), (vm.State[EngineVars.TalkActor], vm.State[EngineVars.HaveMessage]));
        Assert.Equal(5, vm.State.Array(200).Ints[2]); // talk-stop
        Assert.Equal(1, vm.State[210]);
    }

    [Fact]
    public void Print_line_s_parts_style_the_line_and_blasted_text_lasts_a_frame()
    {
        var code = new Asm()
            .Sub(0x93, 0xC8)                                   // begin
            .Push(320).Push(400).Sub(0x93, 0xCA)               // at 320,400
            .Push(12).Sub(0x93, 0xCB)                          // color 12
            .Sub(0x93, 0xCC)                                   // center
            .Push(1).Sub(0x93, 0xCD)                           // charset 1
            .Sub(0x93, 0xD1).Text("Part One")
            .Sub(0x97, 0xC8).Push(10).Push(20).Sub(0x97, 0xCA).Sub(0x97, 0xD1).Text("label")
            .Label("again").Break().Jump(0x66, "again").Global(10);
        var (vm, loop, _) = Machine(code);
        vm.RunScript(10);
        Assert.Equal(new ScreenText("Part One", 320, 400, 12, 1, true, true), Assert.Single(vm.Subtitles)); // talk lines wrap
        Assert.Equal(0xFF, vm.State[EngineVars.TalkActor]); // a printed line has no actor

        vm.EndFrameTexts();
        Assert.Equal(["label", "Part One"], vm.ScreenTexts.Select(t => t.Text));
        loop.RunFrame(5); // the script only breaks now: no blast this frame
        Assert.Equal(["Part One"], vm.ScreenTexts.Select(t => t.Text));
    }

    [Fact]
    public void Messages_lose_their_tag_and_take_variables_and_new_lines()
    {
        var (vm, _, _) = Machine();
        vm.State[300] = 42;
        byte[] message = [.. "/ab12/Got "u8, 0xFF, 4, .. BitConverter.GetBytes(300), .. " coins"u8, 0xFF, 1, .. "Next"u8, 0];
        Assert.Equal("Got 42 coins\nNext", vm.ConvertMessage(message, out string tag));
        Assert.Equal("AB12", tag);
        Assert.Equal("No tag", vm.ConvertMessage("No tag"u8, out tag));
        Assert.Equal("", tag);

        // A string variable's text is converted too: a verb name loses its tag (the verb coin's labels).
        var verb = vm.State.DefineArray(301, ArrayType.String, 0, 32);
        "/PU_M018/examine"u8.CopyTo(verb.Bytes);
        byte[] label = [0xFF, 7, .. BitConverter.GetBytes(301), .. " cannon"u8, 0];
        Assert.Equal("examine cannon", vm.ConvertMessage(label, out _));
    }

    [Fact]
    public void Labels_are_placed_from_the_room_objects_images()
    {
        // The F1 menu puts each label by its button's image: image-x/y/width/height of the room's slot.
        var code = new Asm()
            .Push(1386).Op(0xf0).Write(300).Push(1386).Op(0xf1).Write(301)
            .Push(1386).Op(0xf2).Write(302).Push(1386).Op(0xf3).Write(303).End().Global(10);
        var store = ScriptStore.Of([code], [], new Dictionary<int, (int, int)> { [92] = (640, 480) },
            new Dictionary<int, IReadOnlyList<ObjectPlace>> { [92] = [new ObjectPlace(1, 1386, 0, 0, 400, 140, 144, 32)] });
        var vm = new ScriptVm(store, new VmState(GameLimits.Comi));
        var faults = new List<ScriptFault>();
        vm.Faulted += faults.Add;
        vm.StartScene(92);
        vm.RunScript(10);
        Assert.Empty(faults);
        Assert.Equal((400, 140, 144, 32), (vm.State[300], vm.State[301], vm.State[302], vm.State[303]));

        // One that isn't in the room stops the script, as the engine stops.
        vm.StartScene(0);
        vm.RunScript(10);
        Assert.Contains("not in room", Assert.Single(faults).Message);
    }

    [Fact]
    public void Kernel_get_gives_the_speech_s_lip_sync_width_and_height()
    {
        var code = new Asm()
            .List(218).Op(0xd8).Write(300).List(219).Op(0xd8).Write(301) // no line: a closed mouth
            .Push(3).Push(100).Push(300).Push(5).Op(0xa1)
            .Push(3).Op(0x91).Text("/cngt001/Hello there")
            .List(218).Op(0xd8).Write(302).List(219).Op(0xd8).Write(303)
            .End().Global(10);
        var (vm, _, speech) = Machine(code);
        vm.State[EngineVars.Sync] = 30;
        vm.RunScript(10);
        Assert.Equal((0, 0, 0x40, 0x27), (vm.State[300], vm.State[301], vm.State[302], vm.State[303]));
        Assert.Equal(80, speech.AskedAhead); // the sync variable plus the engine's 50 ms
    }

    [Fact]
    public void String_width_measures_in_the_charset_given()
    {
        var code = new Asm().Push(2).Op(0xf6).Text("abcd").Write(300).End().Global(10);
        var (vm, _, _) = Machine(code);
        vm.StringWidth = (charset, text) => charset * 100 + text.Length;
        vm.RunScript(10);
        Assert.Equal(204, vm.State[300]);
    }

    [Fact]
    public void Wally_s_mouth_follows_his_speech_through_the_talk_script()
    {
        if (Game == null) return;
        var root = ScummProject.Load(ScummProject.FindRelatedFiles(Game), null);
        var vm = new ScriptVm(new ScriptStore(root), new VmState(GameLimits.Read(root)));
        var faults = new List<ScriptFault>();
        vm.Faulted += faults.Add;
        var voice = new MMUCS.Runtime.Sound.VoicePlayer(MMUCS.Runtime.Sound.VoicePlayer.FromBundles(Game));
        vm.Speech = voice;
        var loop = new FrameLoop(vm);
        loop.AfterScripts += voice.Update;
        vm.Boot(9); // straight to the cannon room, where Wally shouts at the pirates
        for (int i = 0; i < 60 * 30 && voice.Playing == null; i++) loop.Tick();
        Assert.NotNull(voice.Playing);

        // Global 28 starts his talk animation and steps his head limb to the mouth the lip sync picks, every frame.
        var wally = vm.Actors[2];
        var mouths = new List<int>();
        while (voice.IsPlaying && vm.State[EngineVars.TalkActor] == 2)
        {
            loop.Tick();
            if (mouths.Count == 0 || mouths[^1] != wally.CurPos[1]) mouths.Add(wally.CurPos[1]);
        }
        Assert.True(mouths.Distinct().Count() >= 6, string.Join(",", mouths));
        Assert.True(mouths.Count >= 15, string.Join(",", mouths)); // it keeps changing until the line is over
        Assert.Empty(faults);
    }

    [Fact]
    public void COMI_s_first_scene_says_its_first_line_with_its_speech()
    {
        if (Game == null) return;
        var root = ScummProject.Load(ScummProject.FindRelatedFiles(Game), null);
        using var game = new ScriptedGame();
        game.Load(root, Game);
        game.Play();
        game.SetMouseCaptured(true);
        game.MoveCursor(GameState.PageToGame(new GamePoint(320, 224)));
        for (int i = 0; i < 60; i++) game.Tick();
        game.Press(MouseButton.Left);
        for (int i = 0; i < 6; i++) game.Tick();
        game.Release(MouseButton.Left);
        for (int i = 0; i < 60 * 120 && (game.IsVideoPlaying || game.CurrentRoom != 9); i++)
        {
            if (game.IsVideoPlaying) game.SkipVideo();
            game.Tick();
        }
        for (int i = 0; i < 60 * 20 && game.Vm.Subtitles.Count == 0; i++) game.Tick();

        var line = Assert.Single(game.Vm.Subtitles);
        Assert.Equal("I've got to get out of here and help Elaine!", line.Text);
        Assert.Equal("CNGT001", game.Voice.Playing);
        Assert.InRange(line.X, 0, 640); // over Guybrush's head, on the page
        Assert.InRange(line.Y, 0, 480);

        // His mouth follows the speech: the lip sync changes as the line plays.
        var mouths = new HashSet<(int, int)>();
        for (int i = 0; i < 60 * 2 && game.Voice.IsPlaying; i++)
        {
            mouths.Add(game.Voice.LipSync(50));
            game.Tick();
        }
        Assert.True(mouths.Count >= 3, $"mouths seen: {string.Join(", ", mouths)}");
        Assert.Equal(0, game.Faults);
    }
}
