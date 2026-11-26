using System.Text;
using MMUCS.Core.Audio;
using MMUCS.Core.Media;
using MMUCS.Core.Scumm;
using MMUCS.Runtime.Scripts;
using MMUCS.Runtime.Sound;
using Xunit;

namespace MMUCS.Runtime.Tests;

/// <summary>
/// COMI's music as Digital iMUSE plays it for the scripts (ScummVM dimuse_scripts.cpp): states and sequences
/// from the tables in comi.exe, each row's transition deciding how its track takes over, sequences giving way
/// to the state's music at their end. The game tests (MMUCS_GAME) read the real tables and hear the menu theme.
/// </summary>
public class MusicTests
{
    private static string Game => Environment.GetEnvironmentVariable("MMUCS_GAME") is { } g && File.Exists(g) ? g : null;

    /// <summary>What the music asked its output to do.</summary>
    private sealed class Recorder : IMusicOutput
    {
        public readonly List<string> Calls = [];
        public int Playing { get; set; }
        public bool End;
        public int Armed;
        public void Start(MusicCue cue, int hook) { Playing = cue.SoundId; Calls.Add($"start {cue.SoundId} hook {hook}"); }
        public void Switch(MusicCue cue, int fadeMs, bool samePlace, int hook) { Playing = cue.SoundId; Calls.Add($"switch {cue.SoundId} {fadeMs}ms{(samePlace ? " in place" : "")} hook {hook}"); }
        public void SwitchAtExit(MusicCue cue, int fadeMs, int hook) => Calls.Add($"at exit {cue.SoundId}");
        public void FadeOut(int ms) { Playing = 0; Calls.Add($"fade {ms}ms"); }
        public void SetHook(int hook) => Calls.Add($"hook {hook}");
        public void ArmEnd(int soundId) { Armed = soundId; Calls.Add($"arm {soundId}"); }
        public bool TakeEnd() { bool e = End; End = false; return e; }
        public MusicPlace? Place { get; set; }
        public void Resume(MusicPlace place) { Playing = place.Cue.SoundId; Calls.Add($"resume {place.Cue.SoundId} at {place.Frame} hook {place.Hook}"); }
    }

    /// <summary>A program holding the two tables in the game's 76-byte rows.</summary>
    private static MusicTable Table(params (int Transition, int Id, string Name, int Attrib, int Hook, string File)[] rows)
    {
        var bytes = new List<byte>(new byte[100]); // something before the tables
        void Row((int Transition, int Id, string Name, int Attrib, int Hook, string File) r)
        {
            var row = new byte[76];
            Encoding.Latin1.GetBytes(r.File).CopyTo(row, 0);
            BitConverter.GetBytes(r.Transition).CopyTo(row, 32);
            BitConverter.GetBytes(r.Id).CopyTo(row, 36);
            Encoding.Latin1.GetBytes(r.Name).CopyTo(row, 40);
            BitConverter.GetBytes(r.Attrib).CopyTo(row, 64);
            BitConverter.GetBytes(r.Hook).CopyTo(row, 68);
            bytes.AddRange(row);
        }
        Row((0, 1000, "STATE_NULL", 0, 0, ""));
        foreach (var r in rows.Where(r => r.Id < 2000)) Row(r);
        bytes.AddRange(new byte[4]); // the gap between the tables
        Row((0, 2000, "SEQ_NULL", 0, 0, ""));
        foreach (var r in rows.Where(r => r.Id >= 2000)) Row(r);
        bytes.AddRange(new byte[76]);
        return MusicTable.Read(bytes.ToArray());
    }

    private static readonly MusicTable Rows = Table(
        (3, 1099, "stateMenu", 0, 0, "1099-Menu"),
        (3, 1100, "stateHold1", 4, 0, "1100-Hold1"),
        (3, 1101, "stateWaterline1", 4, 0, "1101-Waterline1"),
        (3, 1102, "stateHold2", 6, 1, "1102-Hold2"),
        (3, 2105, "seqInterlude1", 0, 0, "2105-Interlude1"),
        (4, 2232, "seqArriveBarber", 0, 0, "2232-ArriveBarber"),
        (8, 2110, "seqLastBoat", 0, 1, ""));

    [Fact]
    public void The_tables_are_read_from_the_program_s_rows()
    {
        Assert.Equal(5, Rows.States.Count);
        Assert.Equal(4, Rows.Sequences.Count);
        var hold2 = Rows.States[4];
        Assert.Equal((3, 1102, "stateHold2", 6, 1, "1102-Hold2", 1102), (hold2.Transition, hold2.SoundId, hold2.Name, hold2.AttribPos, hold2.HookId, hold2.File, hold2.FileNumber));
    }

    [Fact]
    public void A_state_starts_its_track_and_a_new_state_crossfades_from_it()
    {
        var output = new Recorder();
        var music = new ComiMusic(Rows, output);
        Assert.True(music.Command([ComiMusic.SetStateCommand, 1099]));
        music.SetState(1099); // the same state again: nothing
        Assert.True(music.Command([ComiMusic.SetStateCommand, 1102]));
        Assert.True(music.Command([ComiMusic.SetStateCommand, 0])); // 0 is the null state: silence
        Assert.False(music.Command([12, 262, 1792, 50])); // a sound parameter, not music
        Assert.Equal(["start 1099 hook 0", "switch 1102 1000ms hook 0", "fade 2000ms"], output.Calls);
    }

    [Fact]
    public void States_with_the_same_attribute_take_over_in_place_and_hooked_ones_alternate_their_loops()
    {
        var output = new Recorder();
        var music = new ComiMusic(Rows, output);
        music.SetState(1100);
        music.SetState(1101); // hold and waterline share attribute 4: the waterline mix picks up where the hold was
        Assert.Equal("switch 1101 1000ms in place hook 0", output.Calls[^1]);

        // stateHold2 has hook 1: the first time it plays hook 0, after that hook 1 (its other loop).
        music.SetState(1102);
        Assert.Equal("switch 1102 1000ms hook 0", output.Calls[^1]);
        music.SetState(1099);
        music.SetState(1102);
        Assert.Equal("switch 1102 1000ms hook 1", output.Calls[^1]);
    }

    [Fact]
    public void A_sequence_plays_over_the_state_which_comes_back_when_its_track_ends()
    {
        var output = new Recorder();
        var music = new ComiMusic(Rows, output);
        music.SetSequence(2105);
        music.SetState(1100); // waits: the sequence plays on
        Assert.Equal(["start 2105 hook 0"], output.Calls);
        Assert.Equal("seqInterlude1", music.Sequence.Name);

        music.Refresh(); // still playing: nothing
        output.Playing = 0; // the interlude's track ran out
        music.Refresh();
        Assert.Equal(["start 2105 hook 0", "start 1100 hook 0"], output.Calls);
        Assert.Equal(("SEQ_NULL", "stateHold1"), (music.Sequence.Name, music.State.Name));
    }

    [Fact]
    public void A_sequence_that_ends_at_its_end_marker_makes_the_next_wait_and_hooks_set_the_playing_track()
    {
        var output = new Recorder();
        var music = new ComiMusic(Rows, output);
        music.SetState(1099);
        music.SetSequence(2232); // transition 4: plays to its "_end"
        Assert.Equal(2232, output.Armed);
        music.SetSequence(2105); // waits for the barber's "_end"
        Assert.Equal("switch 2232 1000ms hook 0", output.Calls[^1]);

        output.End = true; // the barber music passed "_end": the waiting sequence plays
        music.Refresh();
        Assert.Equal("switch 2105 1000ms hook 0", output.Calls[^1]);
        Assert.Equal("seqInterlude1", music.Sequence.Name);

        music.SetSequence(2110); // transition 8: only the playing track's hook
        Assert.Equal("hook 1", output.Calls[^1]);
    }

    [Fact]
    public void Sound_kludge_goes_to_the_music_and_other_commands_count_as_not_carried_out()
    {
        var code = new Asm().List(ComiMusic.SetStateCommand, 1099).Op(0xb2).List(-1).Op(0xb2).List(12, 262, 1792, 50).Op(0xb2).End().Global(10);
        var vm = new ScriptVm(ScriptStore.Of(code), new VmState(GameLimits.Comi));
        var output = new Recorder();
        var music = new ComiMusic(Rows, output);
        vm.SoundKludge = list => music.Command(list);
        vm.RunScript(10);
        Assert.Equal(["start 1099 hook 0"], output.Calls);
        Assert.Equal(1, vm.UnhandledCounts["soundKludge.12"]);
        Assert.False(vm.UnhandledCounts.ContainsKey("soundKludge.-1"));
    }

    [Fact]
    public void The_plank_s_cutscene_ends_the_monkeys_music_that_a_skipped_dialogue_left_playing()
    {
        // Global 251's override is at 0x12 in its code (ScummVM's 0x1A, counting the header): 17 bytes, then it.
        static ScriptCode Plank() => new Asm().Push(0).Push(0).Op(0x0c).Push(0).Op(0x0c)
            .Op(0x84).Jump(0x66, "skip").Op(0x85).Label("skip").End().Global(251);
        foreach (var (room, playing, expected) in new[] { (37, 2275, true), (37, 1247, false), (36, 2275, false) })
        {
            var vm = new ScriptVm(ScriptStore.Of(Plank()), new VmState(GameLimits.Comi)) { CurrentRoom = room };
            var sent = new List<string>();
            vm.SoundKludge = list => { sent.Add(string.Join(",", list)); return true; };
            vm.MusicRunning = id => id == playing;
            vm.RunScript(251);
            Assert.Equal(expected ? [$"{ComiMusic.SetSequenceCommand},2277"] : [], sent);
        }
    }

    [Fact]
    public void The_juggling_show_s_cut_to_the_stage_sets_the_stage_s_music()
    {
        var show = new Asm().Push(0).Push(118).List().Op(0x79).End().Global(168);
        var juggle = new Asm().End().Global(118);
        foreach (var (room, expected) in new[] { (19, true), (18, false) })
        {
            var vm = new ScriptVm(ScriptStore.Of(show, juggle), new VmState(GameLimits.Comi)) { CurrentRoom = room };
            var sent = new List<string>();
            vm.SoundKludge = list => { sent.Add(string.Join(",", list)); return true; };
            vm.RunScript(168);
            Assert.Equal(expected ? [$"{ComiMusic.SetStateCommand},1278"] : [], sent);
        }
    }

    [Fact]
    public void Room_11_s_wait_for_a_sound_waits_a_frame_at_a_time()
    {
        // local 2016's loop: sound-kludge, then straight back (no break-here).
        var wait = new Asm().Label("loop").List(12, 5, 1792, 127).Op(0xb2).Jump(0x66, "loop").End().Local(11, 2016);
        var vm = new ScriptVm(ScriptStore.Of(wait), new VmState(GameLimits.Comi)) { CurrentRoom = 11 };
        int sent = 0;
        vm.SoundKludge = _ => { sent++; return true; };
        Assert.True(Task.Run(() => vm.RunScript(2016)).Wait(5000), "the loop never gave the frame back");
        Assert.Equal(1, sent);
    }

    [Fact]
    public void COMI_s_tables_come_from_its_program()
    {
        if (Game == null || MusicTable.Load(Game) is not { } table) return;
        Assert.Equal((93, 89), (table.States.Count, table.Sequences.Count));
        var cannon = table.States.Single(s => s.SoundId == 1104);
        Assert.Equal(("stateCannon", "1104-Cannon", 3), (cannon.Name, cannon.File, cannon.Transition));
        Assert.Equal(2560, table.Sequences.Single(s => s.Name == "seqZap4c").FileNumber); // 2562 plays 2560's track
    }

    [Fact]
    public async Task The_difficulty_room_plays_the_menu_theme_through_the_mixer()
    {
        if (Game == null || MusicTable.FindProgram(Game) == null) return;
        var root = ScummProject.Load(ScummProject.FindRelatedFiles(Game), null);
        using var game = new ScriptedGame();
        var mixer = new Mixer();
        game.SetAudio(mixer, new NullAudioOutput(mixer));
        game.Load(root, Game);
        game.Play();
        Assert.Equal("stateMenu", game.Music.State.Name);
        Assert.Equal("1099-Menu", game.MusicOutput.Current.File);

        // The track loads in the background and starts at a frame after it's loaded.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (mixer.VoiceCount == 0 && DateTime.UtcNow < deadline)
        {
            game.Tick();
            await Task.Delay(5);
        }
        Assert.Equal(1, mixer.VoiceCount);
        var block = new float[4096];
        bool heard = false;
        for (int i = 0; i < 40 && !heard; i++)
        {
            mixer.Read(block);
            heard = block.Any(x => x != 0);
        }
        Assert.True(heard);
        Assert.Contains("music 1099-Menu", game.Status);
    }

    [Fact]
    public void A_load_puts_back_the_track_that_was_playing_where_it_was()
    {
        var output = new Recorder();
        var music = new ComiMusic(Rows, output);
        var cue = Rows.States[1];
        output.Playing = cue.SoundId;
        output.Place = new MusicPlace(cue, 2, 123456);
        var saved = music.Save();

        output.FadeOut(0); // the F1 menu's stop-all
        output.Calls.Clear();
        music.Restore(saved);
        Assert.Equal([$"resume {cue.SoundId} at 123456 hook 2"], output.Calls);

        // Nothing was playing: a load leaves silence.
        output.Place = null;
        var silent = music.Save();
        output.Playing = cue.SoundId;
        output.Calls.Clear();
        music.Restore(silent);
        Assert.Equal(["fade 0ms"], output.Calls);
    }

    [Fact]
    public void A_resumed_track_starts_from_the_frame_it_was_saved_at()
    {
        if (Game == null || MusicTable.Load(Game) is not { } table) return;
        var output = new MixerMusic(MixerMusic.FromBundles(Game)); // no mixer: the track doesn't move on by itself
        var cue = table.States.First(c => c.File.Length > 0 && c.SoundId != 0);
        output.Resume(new MusicPlace(cue, 0, 44100));
        for (int i = 0; i < 500 && !output.IsLoaded; i++) { output.Update(); Thread.Sleep(10); }
        Assert.True(output.IsLoaded);
        Assert.Equal(new MusicPlace(cue, 0, 44100), output.Place);
    }
}
