using MMUCS.Core.Scumm;
using MMUCS.Runtime.Scripts;
using Xunit;

namespace MMUCS.Runtime.Tests;

/// <summary>
/// The screen's colours as ScummVM's v8 engine keeps them: intensity and saturation scale the room's own
/// colours (the darken base), not the scaled ones, so a fade comes back to the room; single colours set
/// the base too; transforms step once a frame; shadow tables and remapped costumes pick the nearest
/// colour. With the game (MMUCS_GAME), room 9's light and global 130's fade from black.
/// </summary>
public class PaletteTests
{
    private static string Game => Environment.GetEnvironmentVariable("MMUCS_GAME") is { } g && File.Exists(g) ? g : null;

    private static byte[] Ramp()
    {
        var rgb = new byte[768];
        for (int i = 0; i < 256; i++) (rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2]) = ((byte)i, (byte)(255 - i), 200);
        return rgb;
    }

    [Fact]
    public void Intensity_scales_the_room_s_colours_and_comes_back_to_them()
    {
        var p = new ScreenPalette();
        p.Set(Ramp());
        int version = p.Version;
        p.Darken(0, 0, 0, 24, 255);
        Assert.Equal(((byte)0, (byte)0, (byte)0), p[100]);
        Assert.Equal(((byte)10, (byte)245, (byte)200), p[10]); // outside the range
        Assert.True(p.Version > version);
        p.Darken(128, 255, 510, 24, 255); // from the base, not from black; at most 255
        Assert.Equal(((byte)(100 * 128 / 255), (byte)155, (byte)255), p[100]);
        p.Darken(255, 255, 255, 24, 255);
        Assert.Equal(((byte)100, (byte)155, (byte)200), p[100]);

        version = p.Version;
        p.Darken(255, 255, 255, 24, 255); // nothing changes: the version stays
        Assert.Equal(version, p.Version);

        p.SetColour(100, 40, 80, 120); // a set colour is the new base
        p.Darken(128, 128, 128, 100, 100);
        Assert.Equal(((byte)20, (byte)40, (byte)60), p[100]);
    }

    [Fact]
    public void Saturation_turns_grey_and_a_transform_moves_over_frames()
    {
        var p = new ScreenPalette();
        p.Set(Ramp());
        p.Desaturate(255, 0, 255, 50, 50); // no saturation: grey (to 1, the engine's integer HLS) at the colour's lightness
        var (r, g, b) = p[50];
        Assert.True(Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) <= 1, $"{r},{g},{b}");
        p.Desaturate(255, 255, 255, 50, 50); // unscaled: the colour again, to the engine's rounding
        Assert.InRange(p[50].R, 48, 52);

        var target = new byte[768];
        p.StartTransform(target, 0, 10, 4);
        Assert.True(p.IsTransforming);
        p.Step();
        Assert.Equal(6, p[8].R); // 8 → 0 a quarter of the way
        for (int i = 0; i < 3; i++) p.Step();
        Assert.Equal(((byte)0, (byte)0, (byte)0), p[8]);
        Assert.Equal(((byte)10, (byte)245, (byte)200), p[10]); // the end isn't included
        Assert.False(p.IsTransforming);
    }

    [Fact]
    public void Shadow_tables_and_remapped_colours_pick_the_nearest_screen_colour()
    {
        var p = new ScreenPalette();
        var rgb = new byte[768];
        for (int i = 0; i < 256; i++) rgb[i * 3] = rgb[i * 3 + 1] = rgb[i * 3 + 2] = (byte)i;
        p.Set(rgb);
        Assert.Equal(128, p.Nearest(128, 128, 128));
        Assert.Equal(24, p.Nearest(0, 0, 0)); // 0–23 are never picked
        p.SetShadow(6, 128, 128, 128, 0, 229);
        Assert.Equal(100, p.Shadows[6 * 256 + 200]);
        Assert.Equal(240, p.Shadows[6 * 256 + 240]); // outside the range: itself
    }

    [Fact]
    public void Room_instructions_change_the_screen_s_colours()
    {
        var code = new Asm()
            .Push(1).Push(2).Push(3).Push(40).Sub(0xab, 0x52)                      // room palette 40 is rgb 1,2,3
            .Push(0).Push(0).Push(255).Push(100).Push(110).Sub(0xab, 0x58)         // palette-intensity 0,0,255 slots 100 to 110
            .Push(0).Push(108).Push(6).Push(128).Push(128).Push(128).Push(0).Push(229).Push(7).Op(0xba) // build-palette-shadow
            .End().Global(1);
        var vm = new ScriptVm(ScriptStore.Of(code), new VmState(GameLimits.Comi));
        var faults = new List<ScriptFault>();
        vm.Faulted += faults.Add;
        vm.Palette.Set(Ramp());
        vm.RunScript(1);
        Assert.Empty(faults);
        Assert.Empty(vm.UnhandledCounts);
        Assert.Equal(((byte)1, (byte)2, (byte)3), vm.Palette[40]);
        Assert.Equal(((byte)0, (byte)0, (byte)200), vm.Palette[105]);
        Assert.Equal(((byte)99, (byte)156, (byte)200), vm.Palette[99]);
        Assert.NotEqual(200, vm.Palette.Shadows[6 * 256 + 200]);
    }

    [Fact]
    public void Room_9_fades_in_from_black_and_lights_guybrush_by_the_lamp()
    {
        if (Game == null) return;
        var root = ScummProject.Load(ScummProject.FindRelatedFiles(Game), null);
        var vm = new ScriptVm(new ScriptStore(root), new VmState(GameLimits.Read(root)));
        var faults = new List<ScriptFault>();
        vm.Faulted += faults.Add;
        vm.Boot();
        var loop = new FrameLoop(vm);
        for (int i = 0; i < 60; i++) loop.Tick();
        var hard = vm.RoomObjects.First(o => o.Id == 1364); // Mega-Monkey
        loop.Mouse = (hard.X + hard.Width / 2, hard.Y + hard.Height / 2);
        loop.Press(MouseButton.Left);
        for (int i = 0; i < 6; i++) loop.Tick();
        loop.Release(MouseButton.Left);
        for (int i = 0; i < 60 * 120 && !vm.IsScriptRunning(130); i++) loop.Tick();
        Assert.True(vm.IsScriptRunning(130));

        // Global 130 blacks out room 9 and raises it 6 a frame to 252, the lamp's slots warm and dimmer.
        var room = vm.RoomPalettes(9)[0];
        int Level(int slot) => room[slot * 3] == 0 ? -1 : vm.Palette.Current[slot * 3] * 255 / room[slot * 3];
        for (int i = 0; i < 60 * 30 && vm.CurrentRoom != 9; i++) loop.Tick();
        var levels = new List<int>();
        for (int i = 0; i < 60 * 15; i++)
        {
            loop.Tick();
            if (levels.Count == 0 || levels[^1] != Level(100)) levels.Add(Level(100));
        }
        Assert.Equal(9, vm.CurrentRoom);
        Assert.True(levels.Count > 10, string.Join(",", levels));
        Assert.True(levels.Zip(levels.Skip(1)).All(p => p.First <= p.Second), string.Join(",", levels)); // only ever brighter
        Assert.InRange(levels[^1], 250, 253);
        Assert.InRange(Level(240), 195, 205); // the lamp's red: 200 of 255
        Assert.True(vm.Palette.Current[240 * 3 + 2] < room[240 * 3 + 2] * 140 / 255); // and its blue, 130
        Assert.Empty(faults);
    }
}
