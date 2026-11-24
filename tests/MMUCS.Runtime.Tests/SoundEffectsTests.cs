using MMUCS.Core.Media;
using MMUCS.Core.Scumm;
using MMUCS.Runtime.Scripts;
using MMUCS.Runtime.Sound;
using Xunit;

namespace MMUCS.Runtime.Tests;

/// <summary>
/// Sound effects as iMUSE plays them for the scripts: start-sound plays the sound resource (DSOU → SOUN →
/// iMUS) as a new track, stop-sound and sound-running go by its number, and set-param / fade-param change its
/// volume and pan. Needs the game (MMUCS_GAME).
/// </summary>
public class SoundEffectsTests
{
    private static string Game => Environment.GetEnvironmentVariable("MMUCS_GAME") is { } g && File.Exists(g) ? g : null;

    private static (SoundEffects Effects, Mixer Mixer) Effects(ScummBlock root, bool withMixer)
    {
        var mixer = withMixer ? new Mixer() : null;
        return (new SoundEffects(SoundEffects.FromResources(new ScriptStore(root), root)) { Mixer = mixer }, mixer);
    }

    [Fact]
    public void A_sound_plays_from_its_resource_until_it_ends_or_is_stopped()
    {
        if (Game == null) return;
        var root = ScummProject.Load(ScummProject.FindRelatedFiles(Game), null);
        var (effects, _) = Effects(root, withMixer: false);
        effects.Start(788); // the difficulty room's click: a short sound
        effects.Start(788);
        Assert.True(effects.IsRunning(788));
        Assert.Equal([788], effects.Playing);
        for (int i = 0; i < 60 * 10 && effects.IsRunning(788); i++) effects.Update(1);
        Assert.False(effects.IsRunning(788)); // read along with the game's time, it ran out

        effects.Start(246); // the THX sound: long, until the scripts stop it
        effects.Update(60);
        Assert.True(effects.IsRunning(246));
        effects.Stop(246);
        Assert.False(effects.IsRunning(246));
        effects.Start(0); // no such sound: nothing
        Assert.Empty(effects.Playing);
    }

    [Fact]
    public void Set_param_and_fade_param_reach_every_copy_and_a_fade_to_silence_ends_it()
    {
        if (Game == null) return;
        var root = ScummProject.Load(ScummProject.FindRelatedFiles(Game), null);
        var (effects, mixer) = Effects(root, withMixer: true);
        effects.Start(262);
        effects.Start(262);
        Assert.Equal(2, mixer.VoiceCount);
        Assert.True(effects.Command([SoundEffects.SetParamCommand, 262, SoundEffects.PanParam, 127])); // hard right
        Assert.True(effects.Command([SoundEffects.FadeParamCommand, 262, SoundEffects.VolumeParam, 0, 30])); // half a second
        Assert.False(effects.Command([0x1000, 1100])); // music's, not the effects'
        var block = new float[2 * 44100];
        mixer.Read(block); // a second: the fade is over
        mixer.Read(block);
        Assert.Equal(0, mixer.VoiceCount);
        effects.Update(1);
        Assert.False(effects.IsRunning(262));
    }

    [Fact]
    public void The_thx_room_s_sound_plays_until_the_scripts_leave_the_room()
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
        for (int i = 0; i < 60 * 10 && game.CurrentRoom != 2; i++) game.Tick();
        for (int i = 0; i < 120; i++) game.Tick();
        Assert.Equal(2, game.CurrentRoom);
        Assert.True(game.Effects.IsRunning(246));
        for (int i = 0; i < 60 * 40 && game.CurrentRoom == 2; i++) game.Tick();
        Assert.NotEqual(2, game.CurrentRoom);
        Assert.False(game.Effects.IsRunning(246));
        Assert.False(game.Vm.UnhandledCounts.ContainsKey("startSound"));
        Assert.Equal(0, game.Faults);
    }
}
