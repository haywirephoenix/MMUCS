using MMUCS.Core.Audio;
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

    /// <summary>An iMUS sound: a MAP with these events (big-endian fields), then DATA.</summary>
    private static byte[] Imus(int bits, int channels, byte[] data, params (string Tag, uint[] Fields)[] events)
    {
        static byte[] Chunk(string tag, byte[] body) => [.. System.Text.Encoding.ASCII.GetBytes(tag), .. BE((uint)body.Length), .. body];
        static byte[] BE(uint v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
        var map = new List<byte>(Chunk("FRMT", [.. BE(0), .. BE(0), .. BE((uint)bits), .. BE(22050), .. BE((uint)channels)]));
        foreach (var (tag, fields) in events) map.AddRange(Chunk(tag, [.. fields.SelectMany(BE)]));
        var body = new List<byte>(Chunk("MAP ", [.. map]));
        body.AddRange(Chunk("DATA", data));
        return Chunk("iMUS", [.. body]);
    }

    [Fact]
    public void Twelve_bit_samples_are_widened_to_sixteen_bit_with_the_map_s_positions()
    {
        // Two 3-byte groups: silence (0x800, 0x800), then the top (0xFFF) and just below the middle (0x7FF).
        byte[] data = [0x00, 0x88, 0x00, 0xFF, 0x7F, 0xFF];
        int dataAt = 8 + 8 + (8 + 20) + (8 + 8) + (8 + 16) + 8; // iMUS, MAP, FRMT, REGN, JUMP, DATA headers
        var raw = Imus(12, 1, data, ("REGN", [(uint)dataAt, 6]), ("JUMP", [(uint)dataAt + 6, (uint)dataAt + 3, 0, 0]));
        var sound = ImusSound.Parse(raw);
        Assert.Equal((16, dataAt, 8, 4L), (sound.Bits, sound.DataOffset, sound.DataLength, sound.FrameCount));
        var samples = sound.Samples.ToArray();
        Assert.Equal([0, 0, 0x7FF0, -16], Enumerable.Range(0, 4).Select(i => (int)BitConverter.ToInt16(samples, i * 2)));
        Assert.Equal((dataAt, 8L), (sound.Regions[0].Offset, sound.Regions[0].Length));
        Assert.Equal((dataAt + 8L, dataAt + 4L), (sound.Jumps[0].Offset, sound.Jumps[0].Destination)); // 3 bytes in: the second pair
    }

    [Fact]
    public void THX_s_sound_decodes_to_smooth_sound_not_noise()
    {
        if (Game == null) return;
        var root = ScummProject.Load(ScummProject.FindRelatedFiles(Game), null);
        var sound = SoundEffects.FromResources(new ScriptStore(root), root)(246);
        Assert.Equal((16, 2), (sound.Bits, sound.Channels)); // stored as 12-bit
        Assert.InRange(sound.FrameCount / (double)sound.SampleRate, 29, 31); // the room waits 28 s for it
        var buffer = new float[sound.SampleRate * 2 * 4];
        int frames = new ImusPlayer(sound).Read(buffer);
        double rms = Math.Sqrt(buffer.Take(frames * 2).Average(x => (double)x * x));
        double step = Enumerable.Range(1, frames - 1).Average(i => Math.Abs(buffer[i * 2] - buffer[(i - 1) * 2]));
        Assert.True(rms > 0.001 && step < rms / 2, $"rms {rms}, step between samples {step}"); // noise steps as far as it's loud
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
