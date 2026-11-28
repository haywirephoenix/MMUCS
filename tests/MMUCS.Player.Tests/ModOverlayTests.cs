using MMUCS.Core.Imaging;
using MMUCS.Core.Scumm;
using MMUCS.Player.Rooms;
using Xunit;

namespace MMUCS.Player.Tests;

/// <summary>
/// A mod picture with "base": "game" is drawn over the game's own (ModOverlay), so a shared mod holds only its author's
/// pixels; mods --strip makes one from a PNG that holds the whole picture. The game test needs MMUCS_GAME.
/// </summary>
public class ModOverlayTests
{
    private static string Game => Environment.GetEnvironmentVariable("MMUCS_GAME") is { } g && File.Exists(g) ? g : null;

    private static IndexedSurface Surface(int w, int h, params byte[] pixels) =>
        new() { Width = w, Height = h, Pitch = w, Pixels = pixels, TransparentIndex = 0 };

    [Fact]
    public void An_overlay_is_drawn_over_the_game_s_image_and_its_erasing_pixels_take_the_game_s_away()
    {
        // The game's 3×1 image at 10,5; the mod's 2×2 at 9,5: a new pixel left of it, one erasing the game's middle.
        var game = Surface(3, 1, 1, 2, 3);
        var mod = Surface(2, 2, 7, 0, 8, 0);
        var erased = new[] { false, true, false, false };
        var (s, x, y) = ModOverlay.Over(game, 10, 5, mod, 9, 5, 0, erased);
        Assert.Equal((9, 5, 4, 2), (x, y, s.Width, s.Height));
        Assert.Equal(new byte[] { 7, 0, 2, 3, 8, 0, 0, 0 }, s.Pixels);
    }

    [Fact]
    public void Strip_keeps_only_what_differs_from_the_game_s_marks_what_it_hides_and_draws_back_the_same()
    {
        // The game's 3×1 starts a column left of the mod's 3×2: the mod leaves out the game's first pixel, has its
        // second, changes its third and adds one below.
        var game = Surface(3, 1, 1, 2, 3);
        var modded = Surface(3, 2, 2, 9, 0, 0, 0, 5);
        uint Colour(byte v) => v == 0 ? 0 : 0xFF000000u | v;
        var argb = modded.Pixels.Select(Colour).ToArray();
        // The game's image starts left of the mod's: the overlay is widened by a column to erase its first pixel.
        var stripped = ModOverlay.Strip(argb, modded, game, -1, 0, 0);
        Assert.Equal((4, 2, -1, 0), (stripped.Width, stripped.Height, stripped.ShiftX, stripped.ShiftY));
        Assert.Equal((2, 1, 1), (stripped.Kept, stripped.Cleared, stripped.Erased));

        // Read back as the loaders do, drawn over the game's, it's what the mod drew in place of it.
        var px = (uint[])stripped.Argb.Clone();
        var erased = ModOverlay.TakeErased(px);
        var overlay = Surface(stripped.Width, stripped.Height, [.. px.Select(c => (byte)(c >> 24 == 0 ? 0 : c & 0xFF))]);
        var (drawn, x, y) = ModOverlay.Over(game, -1, 0, overlay, stripped.ShiftX, stripped.ShiftY, 0, erased);
        Assert.Equal(0, ModOverlay.Differences(drawn, x, y, modded, 0, 0, 0));
    }

    [Fact]
    public async Task An_object_image_over_the_game_s_draws_its_pixels_and_erasures_on_the_game_s_and_strip_round_trips()
    {
        if (Game == null) return;
        var root = ScummProject.Load(ScummProject.FindRelatedFiles(Game), null);
        var room = root.FindChildrenRecursive(ScummTag.LFLF).Select(l => RoomModel.TryCreate(l, out var r) ? r : null)
            .First(r => r != null && r.RoomNo == 22);
        var caber = room.Objects.Single(o => o.Name == "clring-a-haggis-caber");
        int palette = RoomSceneBuilder.PaletteOf(room, caber);
        var colours = ObimCache.ExtractPaletteContext(room.Room, palette).rawBytes;
        var game = (await ObimCache.GetCachedObimAsync(caber.Block, 0, palette, bakeImage: false, default)).Surface;
        int see = ObimCache.GetTransparentIndex(room.Room, caber.Block, 0);
        // Two opaque game pixels side by side, and a third in their row.
        var (gx, gy) = Enumerable.Range(0, game.Height).SelectMany(y => Enumerable.Range(0, game.Width - 2).Select(x => (x, y)))
            .First(p => Enumerable.Range(0, 3).All(k => game.Pixels[p.y * game.Pitch + p.x + k] != see));
        int at = gy * game.Pitch + gx;

        string dir = Path.Combine(Path.GetTempPath(), "mmucs-overlay-" + Guid.NewGuid().ToString("N"));
        string objects = Path.Combine(dir, "_mods", "Wide", "rooms", "022_clring-a", "objects");
        Directory.CreateDirectory(objects);
        try
        {
            File.WriteAllText(Path.Combine(dir, "_mods", "Wide", "mod.json"), """{ "enabled": true }""");
            string png = Path.Combine(objects, "041_clring-a-haggis-caber_f0.png");
            // A 2×1 overlay on those pixels: colour 40's RGB, then an erasing pixel.
            var rgba = new byte[] { colours[120], colours[121], colours[122], 255, 0, 0, 0, 1 };
            using (var f = File.Create(png)) PngWriter.Write(f, 2, 1, rgba);
            File.WriteAllText(Path.ChangeExtension(png, ".meta.json"), $$"""{ "x": {{caber.X + gx}}, "y": {{caber.Y + gy}}, "base": "game" }""");

            var mods = ObjectImageMods.Load(Path.Combine(dir, "COMI.LA0"));
            var scene = await RoomSceneBuilder.BuildIndexedAsync(room, o => o == caber ? 1 : 0, mods: mods);
            Assert.Empty(mods.Problems);
            var drawn = scene.Objects.Single();
            Assert.Equal((caber.X, caber.Y, game.Width, game.Height), (drawn.X, drawn.Y, drawn.Surface.Width, drawn.Surface.Height));
            Assert.Equal(40, PaletteMatch(colours, drawn.Surface.Pixels[at]));
            Assert.Equal(see, drawn.Surface.Pixels[at + 1]);
            // The rest is the game's.
            Assert.Equal(game.Pixels[at + 2], drawn.Surface.Pixels[at + 2]);

            // A whole-picture PNG (the game's image with one pixel changed) strips to that pixel and draws the same.
            var whole = new byte[game.Width * game.Height * 4];
            for (int i = 0; i < game.Width * game.Height; i++)
            {
                byte v = game.Pixels[(i / game.Width) * game.Pitch + i % game.Width];
                if (v == see) continue;
                (whole[i * 4], whole[i * 4 + 1], whole[i * 4 + 2], whole[i * 4 + 3]) = (colours[v * 3], colours[v * 3 + 1], colours[v * 3 + 2], 255);
            }
            int wi = gy * game.Width + gx;
            (whole[wi * 4], whole[wi * 4 + 1], whole[wi * 4 + 2]) = (colours[120], colours[121], colours[122]);
            using (var f = File.Create(png)) PngWriter.Write(f, game.Width, game.Height, whole);
            File.Delete(Path.ChangeExtension(png, ".meta.json"));
            var image = mods.Find(room, caber, 0);
            var stripped = mods.Strip(image, game, caber.X, caber.Y, colours, see);
            Assert.Equal((0, 0, 0), (stripped.ShiftX, stripped.ShiftY, stripped.Erased));
            Assert.InRange(stripped.Kept, PaletteMatch(colours, game.Pixels[at]) == PaletteMatch(colours, 40) ? 0 : 1, 1);
            string overlay = Path.Combine(dir, "overlay.png");
            using (var f = File.Create(overlay)) PngWriter.Write(f, stripped.Width, stripped.Height, ModOverlay.ToRgba(stripped.Argb));
            var before = mods.Drawn(image, game, caber.X, caber.Y, colours, see);
            var after = mods.Drawn(image with { File = overlay, OverGame = true }, game, caber.X, caber.Y, colours, see);
            Assert.Equal(0, ModOverlay.Differences(before.Value.Surface, before.Value.X, before.Value.Y, after.Value.Surface, after.Value.X, after.Value.Y, (byte)see));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>The palette index with <paramref name="index"/>'s colour that's lowest, so two indices of one colour compare equal.</summary>
    private static int PaletteMatch(byte[] palette, int index) => PaletteIndex(palette, index, -1);

    private static int PaletteIndex(byte[] palette, int index, int skip)
    {
        for (int i = 0; i < 256; i++)
            if (i != skip && palette[i * 3] == palette[index * 3] && palette[i * 3 + 1] == palette[index * 3 + 1] && palette[i * 3 + 2] == palette[index * 3 + 2])
                return i;
        return index;
    }
}
