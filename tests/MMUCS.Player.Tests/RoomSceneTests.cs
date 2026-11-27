using MMUCS.Core.Imaging;
using MMUCS.Core.Scumm;
using MMUCS.Player.Rooms;
using MMUCS.Runtime;
using SkiaSharp;
using Xunit;

namespace MMUCS.Player.Tests;

/// <summary>
/// Rooms are drawn one way for the game, the Room viewer and mprobe (RoomScene): the background, the objects
/// shown, then the actors in SCUMM's order, cut by their z-plane; in the game frame the camera places the room,
/// and a 16:9 canvas shows more of a wide room. The game tests need MMUCS_GAME; MMUCS_FRAMES=folder saves PNGs.
/// </summary>
public class RoomSceneTests
{
    private static string Game => Environment.GetEnvironmentVariable("MMUCS_GAME") is { } g && File.Exists(g) ? g : null;

    private static SKImage Solid(int w, int h, SKColor color)
    {
        using var bitmap = new SKBitmap(w, h);
        bitmap.Erase(color);
        return SKImage.FromBitmap(bitmap);
    }

    private static SKBitmap Draw(RoomScene scene, SKColor? cut = null)
    {
        var bitmap = new SKBitmap(scene.Width, scene.Height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);
        scene.Draw(canvas, new SKSamplingOptions(SKFilterMode.Nearest), cutColor: cut);
        return bitmap;
    }

    [Fact]
    public void An_actor_is_cut_by_its_plane_once_where_it_stands_and_not_at_all_where_the_plane_is_empty()
    {
        // A plane over x 8–15 of a 32×8 room, and a 4×4 frame.
        var hides = new byte[32 * 8];
        for (int y = 0; y < 8; y++)
            for (int x = 8; x < 16; x++) hides[y * 32 + x] = 255;
        using var mask = SKImage.FromPixelCopy(new SKImageInfo(32, 8, SKColorType.Alpha8, SKAlphaType.Premul), hides, 32);
        var frame = PaletteImages.Indices(new IndexedSurface { Width = 4, Height = 4, Pitch = 4, Pixels = [.. Enumerable.Repeat((byte)7, 16)] });

        Assert.Same(frame, RoomScene.Cut(frame, 20, 2, mask)); // the plane has nothing under it there
        var cut = RoomScene.Cut(frame, 6, 2, mask);            // its right half is behind the plane
        Assert.NotSame(frame, cut);
        Assert.Same(cut, RoomScene.Cut(frame, 6, 2, mask));     // standing still: the same cut
        using var pixels = SKBitmap.FromImage(cut);
        Assert.Equal(255, pixels.GetPixel(1, 1).Alpha);
        Assert.Equal(0, pixels.GetPixel(2, 1).Alpha);
        Assert.Equal(7, pixels.GetPixel(0, 3).Red); // still an index image
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_paletted_room_draws_as_the_coloured_one_in_any_colours(bool colourOnCpu)
    {
        // On the CPU as the game window's software rendering does, or through the shader as on its GPU.
        PaletteImages.ColourOnCpu = colourOnCpu;
        try { PalettedRoom(); }
        finally { PaletteImages.ColourOnCpu = true; }
    }

    private static void PalettedRoom()
    {
        // A 16×8 room of indices 0–127, an object with a left-out index, and an actor frame in indices.
        var bg = new IndexedSurface { Width = 16, Height = 8, Pitch = 16, Pixels = [.. Enumerable.Range(0, 128).Select(i => (byte)i)] };
        var obj = new IndexedSurface { Width = 4, Height = 2, Pitch = 4, Pixels = [200, 201, 202, 203, 204, 9, 206, 207] };
        var room = new IndexedRoom { Width = 16, Height = 8, Background = bg, Objects = [new IndexedObject(null, obj, 9, 3, 2)] };
        var actor = new IndexedSurface { Width = 2, Height = 3, Pitch = 2, Pixels = [250, 251, 252, 253, 254, 255] };
        var random = new Random(3);
        foreach (var palette in new[] { new byte[768], new byte[768] }.Select(p => { random.NextBytes(p); return p; }))
        {
            var colours = Enumerable.Range(0, 256).Select(i => new SKColor(palette[i * 3], palette[i * 3 + 1], palette[i * 3 + 2])).ToArray();
            using var actorColour = SKImage.FromBitmap(new SKBitmap(2, 3) { Pixels = [.. actor.Pixels.Select(i => colours[i])] });
            var coloured = room.Colour(palette).WithActors([new SceneActor(actorColour, 10, 4)]);
            var paletted = room.Paletted(palette).WithActors([new SceneActor(PaletteImages.Indices(actor), 10, 4)]);
            Assert.Null(coloured.Palette);
            Assert.NotNull(paletted.Palette);
            using var a = Draw(coloured);
            using var b = Draw(paletted);
            Assert.Equal(a.Pixels, b.Pixels);
            Assert.Equal(colours[202], b.GetPixel(5, 2));
            Assert.Equal(colours[16 * 3 + 4], b.GetPixel(4, 3)); // the object's left-out index shows the background
            Assert.Equal(colours[253], b.GetPixel(11, 5));

            // Drawn smaller than the game (a small window), it blends neighbouring game pixels as a filtered image does
            // (within Skia's own rounding on the CPU).
            SKBitmap Small(RoomScene scene)
            {
                var bitmap = new SKBitmap(10, 5);
                using var canvas = new SKCanvas(bitmap);
                canvas.Clear(SKColors.Black);
                canvas.Scale(0.6f); // no pixel's centre on an image's edge, where the two may round apart
                scene.Draw(canvas, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
                return bitmap;
            }
            using var c = Small(coloured);
            using var d = Small(paletted);
            for (int y = 1; y < 4; y++)
                for (int x = 1; x < 9; x++)
                {
                    SKColor p = c.GetPixel(x, y), q = d.GetPixel(x, y);
                    Assert.True(Math.Abs(p.Red - q.Red) <= 10 && Math.Abs(p.Green - q.Green) <= 10 && Math.Abs(p.Blue - q.Blue) <= 10, $"({x},{y}): {p} vs {q}");
                }
        }
        // The index images are made once: a new palette only makes a new strip.
        Assert.Same(room.Paletted(new byte[768]).Background, room.Paletted(new byte[768]).Background);
    }

    [Fact]
    public void Objects_go_over_the_background_and_actors_over_them_cut_by_their_plane_in_depth_order()
    {
        // Plane 1 covers x 8–15 of a 32×8 room.
        var plane = new ZPlane(4, 8);
        for (int y = 0; y < 8; y++) plane.Data[y * 4 + 1] = 0xFF;
        var scene = new RoomScene
        {
            Width = 32, Height = 8,
            Background = Solid(32, 8, SKColors.Blue),
            Objects = [new SceneObject(null, Solid(4, 8, SKColors.Green), 28, 0)],
            Actors =
            [
                new SceneActor(Solid(12, 8, SKColors.Red), 4, 0, ZPlane: 1, Depth: 10),   // behind the plane from x 8
                new SceneActor(Solid(4, 8, SKColors.White), 6, 0, Depth: 5),             // drawn first: lower depth
                new SceneActor(Solid(4, 8, SKColors.Yellow), 24, 0, Scale: 2, Depth: 1), // twice its size, over the object
            ],
            ZMasks = [RoomScene.MaskImage(plane, 32, 8)],
        };
        using var picture = Draw(scene);
        Assert.Equal(SKColors.Blue, picture.GetPixel(1, 4));
        Assert.Equal(SKColors.Red, picture.GetPixel(6, 4));   // red (depth 10) over white (depth 5)
        Assert.Equal(SKColors.White, picture.GetPixel(9, 4)); // red cut by the plane: white shows
        Assert.Equal(SKColors.Blue, picture.GetPixel(12, 4)); // red cut, nothing else there
        Assert.Equal(SKColors.Yellow, picture.GetPixel(30, 4)); // over the green object
        Assert.Equal(SKColors.Blue, picture.GetPixel(20, 4));

        // With a cut colour the hidden pixels show in it.
        using var debug = Draw(scene, SKColors.Magenta);
        Assert.Equal(SKColors.Magenta, debug.GetPixel(12, 4));
        Assert.Equal(SKColors.Red, debug.GetPixel(6, 4));
    }

    [Fact]
    public async Task An_object_shows_the_image_its_state_says_and_none_in_state_0()
    {
        if (Game == null) return;
        var root = ScummProject.Load(ScummProject.FindRelatedFiles(Game), null);
        var room = Room(root, 87);
        // The difficulty room's buttons start in state 0: the background alone.
        var entry = await RoomSceneBuilder.BuildAsync(room, _ => 0);
        Assert.Empty(entry.Objects);
        Assert.Equal((640, 480), (entry.Background.Width, entry.Background.Height));

        // In state 1 every object with an image is drawn with its first image, in the room's draw order.
        var all = await RoomSceneBuilder.BuildAsync(room, _ => 1);
        var withImages = room.Objects.Where(o => o.ImageCount > 0).ToList();
        Assert.Equal(room.InDrawOrder(withImages).Select(o => o.Name), all.Objects.Select(o => o.Object.Name));
        Assert.All(all.Objects, o => Assert.Equal((o.Object.X, o.Object.Y), (o.X, o.Y)));
        Assert.Equal(RoomSceneBuilder.Shown(room, _ => 1).Select(s => s.Image), withImages.Select(_ => 0));
    }

    [Fact]
    public async Task The_game_frame_places_the_room_by_the_camera_and_shows_more_of_a_wide_room()
    {
        if (Game == null) return;
        var root = ScummProject.Load(ScummProject.FindRelatedFiles(Game), null);
        var room = Room(root, 14); // fortbase, 1496 wide
        var states = RoomObjectStates.Initial(room);
        var scene = await RoomSceneBuilder.BuildAsync(room, o => states.GetValueOrDefault(o), RoomSceneBuilder.Actors(RoomActors.Analyze(room)));
        Assert.Equal(1496, scene.Width);
        Assert.NotEmpty(scene.Actors);
        Assert.NotEmpty(scene.ZMasks);

        var view = new GameView(new GameState()) { Room = scene };
        // At the room's left end (the camera's centre at 320) the 640 page shows x 0–639, black to its left.
        using (var left = Frame(view.Capture(), "fortbase-left"))
        {
            Assert.Equal(SKColors.Black, left.GetPixel(50, 240));
            Assert.NotEqual(SKColors.Black, left.GetPixel(200, 240));
        }
        // In the middle, the 16:9 canvas is room from edge to edge.
        view.Camera = new GamePoint(748, 240);
        using var middle = Frame(view.Capture(), "fortbase-middle");
        Assert.NotEqual(SKColors.Black, middle.GetPixel(2, 240));
        Assert.NotEqual(SKColors.Black, middle.GetPixel(850, 240));
        // Room pixel (748 − 426⅔ + x) is canvas pixel x: the picture is the room's, unscaled.
        using var room1 = Draw(scene);
        Assert.Equal(room1.GetPixel(748 - 427 + 600, 300), middle.GetPixel(600, 300));
    }

    [Fact]
    public async Task A_mod_s_png_where_the_export_writes_an_object_image_is_drawn_in_its_place_in_the_room_s_colours()
    {
        if (Game == null) return;
        var root = ScummProject.Load(ScummProject.FindRelatedFiles(Game), null);
        var room = Room(root, 22);
        var caber = room.Objects.Single(o => o.Name == "clring-a-haggis-caber");
        var guybrush = room.Objects.Single(o => o.Name == "clring-a-guybrush-caber");
        Assert.Equal("022_clring-a", ScummExporters.RoomFolderName(room));
        Assert.Equal("041_clring-a-haggis-caber_f0.png", ScummExporters.ObjectFrameFile(caber, 0));

        var palette = ObimCache.ExtractPaletteContext(room.Room, RoomSceneBuilder.PaletteOf(room, caber)).rawBytes;
        string dir = Path.Combine(Path.GetTempPath(), "mmucs-mods-" + Guid.NewGuid().ToString("N"));
        string objects = Path.Combine(dir, "_mods", "Wide", "rooms", "022_clring-a", "objects");
        Directory.CreateDirectory(objects);
        try
        {
            File.WriteAllText(Path.Combine(dir, "_mods", "Wide", "mod.json"), """{ "enabled": true }""");
            // 240×40: colour 40's exact RGB on the left half, a colour near 41's on the right, the top row see-through.
            int w = 240, h = 40;
            var rgba = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int c = x < w / 2 ? 40 : 41, o = (y * w + x) * 4;
                    rgba[o] = palette[c * 3];
                    rgba[o + 1] = palette[c * 3 + 1];
                    rgba[o + 2] = (byte)Math.Max(0, palette[c * 3 + 2] - (c == 41 ? 1 : 0));
                    rgba[o + 3] = (byte)(y == 0 ? 0 : 255);
                    if (y == 1) (rgba[o], rgba[o + 1], rgba[o + 2], rgba[o + 3]) = (0, 0, 0, 102); // a shadow: 40% black
                }
            using (var f = File.Create(Path.Combine(objects, "041_clring-a-haggis-caber_f0.png"))) PngWriter.Write(f, w, h, rgba);
            File.WriteAllText(Path.Combine(objects, "041_clring-a-haggis-caber_f0.meta.json"), """{ "x": 236, "y": 416 }""");

            var mods = ObjectImageMods.Load(Path.Combine(dir, "COMI.LA0"));
            Assert.Equal(["Wide"], mods.Mods);
            var scene = await RoomSceneBuilder.BuildIndexedAsync(room, o => o == caber || o == guybrush ? 1 : 0, mods: mods);
            Assert.Empty(mods.Problems);
            var modded = scene.Objects.Single(o => o.Object == caber);
            Assert.Equal((236, 416, 240, 40), (modded.X, modded.Y, modded.Surface.Width, modded.Surface.Height));
            byte At(int x, int y) => modded.Surface.Pixels[y * modded.Surface.Pitch + x];
            Assert.Equal(modded.Transparent, At(10, 0));
            // The shadow row (the export's 40% black) is the room's background under it darkened as a costume's shadow
            // is: by shadow table 6, COMI's 189 of 255 when the game's tables aren't given; in the nearest palette colour.
            var bg = scene.Background;
            void ShadowIs(IndexedObject o, double factor)
            {
                for (int x = 0; x < w; x += 37)
                {
                    int under = bg.Pixels[417 * bg.Pitch + 236 + x], got = o.Surface.Pixels[1 * o.Surface.Pitch + x];
                    Assert.NotEqual(o.Transparent, got);
                    for (int k = 0; k < 3; k++)
                        Assert.InRange(palette[got * 3 + k], palette[under * 3 + k] * factor - 20, palette[under * 3 + k] * factor + 20);
                }
            }
            ShadowIs(modded, 189 / 255.0);
            // The game's own tables when they're given: table 6 at half.
            var tables = new byte[24];
            tables[18] = tables[19] = tables[20] = 128;
            var halved = (await RoomSceneBuilder.BuildIndexedAsync(room, o => o == caber ? 1 : 0, mods: mods, shadowTables: tables)).Objects.Single();
            ShadowIs(halved, 128 / 255.0);
            // "shadowTable": 7 in the .meta.json picks COMI's lighter one.
            File.WriteAllText(Path.Combine(objects, "041_clring-a-haggis-caber_f0.meta.json"), """{ "x": 236, "y": 416, "shadowTable": 7 }""");
            var lighter = (await RoomSceneBuilder.BuildIndexedAsync(room, o => o == caber ? 1 : 0, mods: mods)).Objects.Single();
            ShadowIs(lighter, 219 / 255.0);
            Assert.Equal(40, PaletteMatch(palette, At(10, 5), 40));
            Assert.Equal(41, PaletteMatch(palette, At(200, 5), 41));
            // Guybrush's caber has no PNG in the mod: the game's image at its place.
            var other = scene.Objects.Single(o => o.Object == guybrush);
            Assert.Equal((guybrush.X, guybrush.Y, guybrush.Width), (other.X, other.Y, other.Surface.Width));

            // Without the .meta.json it goes at the object's place; a mod that's off has none.
            File.Delete(Path.Combine(objects, "041_clring-a-haggis-caber_f0.meta.json"));
            Assert.Equal((null, null), (mods.Find(room, caber, 0).X, mods.Find(room, caber, 0).Y));
            File.WriteAllText(Path.Combine(dir, "_mods", "Wide", "mod.json"), """{ "enabled": false }""");
            Assert.Null(ObjectImageMods.Load(Path.Combine(dir, "COMI.LA0")).Find(room, caber, 0));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task A_hi_res_mod_s_background_and_object_images_are_drawn_at_their_scale_and_tinted_as_the_colours_change()
    {
        if (Game == null) return;
        var root = ScummProject.Load(ScummProject.FindRelatedFiles(Game), null);
        var room = Room(root, 22); // clring-a, 1072×480
        var caber = room.Objects.Single(o => o.Name == "clring-a-haggis-caber");
        string dir = Path.Combine(Path.GetTempPath(), "mmucs-hires-" + Guid.NewGuid().ToString("N"));
        string folder = Path.Combine(dir, "_mods", "Upscaled", "rooms", "022_clring-a");
        Directory.CreateDirectory(Path.Combine(folder, "objects"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "_mods", "Upscaled", "mod.json"), """{ "enabled": true, "scale": 2 }""");
            void Png(string file, int w, int h, Func<int, int, (byte, byte, byte)> colour)
            {
                var rgba = new byte[w * h * 4];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        var (r, g, b) = colour(x, y);
                        int o = (y * w + x) * 4;
                        (rgba[o], rgba[o + 1], rgba[o + 2], rgba[o + 3]) = (r, g, b, 255);
                    }
                using var f = File.Create(file);
                PngWriter.Write(f, w, h, rgba);
            }
            // The room at 2×: white on the left half, green on the right; the caber's image blue, at 2× its size.
            Png(Path.Combine(folder, "background.png"), 2144, 960, (x, _) => x < 1072 ? ((byte)255, (byte)255, (byte)255) : ((byte)0, (byte)255, (byte)0));
            Png(Path.Combine(folder, "objects", "041_clring-a-haggis-caber_f0.png"), caber.Width * 2, caber.Height * 2, (_, _) => (0, 0, 255));

            string game = Path.Combine(dir, "COMI.LA0");
            // The palette mods leave a hi-res mod alone; the hi-res mods have it.
            Assert.Empty(ObjectImageMods.Load(game).Mods);
            Assert.Empty(CostumeCelMods.Load(game).Mods);
            var hiRes = HiResMods.Load(game);
            Assert.Equal(["Upscaled"], hiRes.Mods);
            Assert.Equal(2, hiRes.Background(room).Scale);

            var indexed = await RoomSceneBuilder.BuildIndexedAsync(room, o => o == caber ? 1 : 0, hiRes: hiRes);
            Assert.NotNull(indexed.HiResBackground);
            var basePalette = ObimCache.ExtractPaletteContext(room.Room, 0).rawBytes;

            // In the room's own colours the pictures are drawn as they are, over the room and the object's place.
            var scene = indexed.Colour(basePalette);
            Assert.Null(scene.HiResBackground.Tinted);
            Assert.Equal((0f, 0f, 1072f, 480f), (scene.HiResBackground.X, scene.HiResBackground.Y, scene.HiResBackground.Width, scene.HiResBackground.Height));
            var obj = scene.Objects.Single(o => o.Object == caber);
            Assert.Equal((caber.X, caber.Y, (float)caber.Width, (float)caber.Height), ((int)obj.HiRes.X, (int)obj.HiRes.Y, obj.HiRes.Width, obj.HiRes.Height));

            // A spot of the background where the game's colour is bright and the same all round (no tint blending in).
            var bg = indexed.Background;
            (int X, int Y) spot = default;
            for (int y = 10; y < 470 && spot == default; y += 3)
                for (int x = 10; x < 500 && spot == default; x += 3)
                {
                    int i = bg.Pixels[y * bg.Pitch + x];
                    if (basePalette[i * 3] > 120 && basePalette[i * 3 + 1] > 120 && basePalette[i * 3 + 2] > 120 &&
                        Enumerable.Range(-1, 3).All(dy => Enumerable.Range(-1, 3).All(dx => bg.Pixels[(y + dy) * bg.Pitch + x + dx] == i)) &&
                        !(x >= caber.X - 2 && x < caber.X + caber.Width + 2 && y >= caber.Y - 2 && y < caber.Y + caber.Height + 2))
                        spot = (x, y);
                }
            Assert.NotEqual(default, spot);
            using (var plain = Draw(scene))
            {
                Assert.Equal(new SKColor(255, 255, 255), plain.GetPixel(spot.X, spot.Y));
                Assert.Equal(new SKColor(0, 255, 0), plain.GetPixel(900, 10));
                Assert.Equal(new SKColor(0, 0, 255), plain.GetPixel(caber.X + caber.Width / 2, caber.Y + caber.Height / 2));
            }

            // The screen at half brightness (a fade): the picture darkens with the game's colours under it.
            var half = basePalette.Select(b => (byte)(b / 2)).ToArray();
            var dimmed = indexed.Colour(half);
            Assert.NotNull(dimmed.HiResBackground.Tinted);
            using var dark = Draw(dimmed);
            int under = bg.Pixels[spot.Y * bg.Pitch + spot.X];
            for (int k = 0; k < 3; k++)
            {
                double b = basePalette[under * 3 + k] / 255.0, expected = 255 * (half[under * 3 + k] / 255.0 + 0.02) / (b + 0.02);
                byte got = k switch { 0 => dark.GetPixel(spot.X, spot.Y).Red, 1 => dark.GetPixel(spot.X, spot.Y).Green, _ => dark.GetPixel(spot.X, spot.Y).Blue };
                Assert.InRange(got, expected - 4, expected + 4);
            }

            // A mod that's off has none.
            File.WriteAllText(Path.Combine(dir, "_mods", "Upscaled", "mod.json"), """{ "enabled": false, "scale": 2 }""");
            Assert.Null(HiResMods.Load(game).Background(room));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary><paramref name="expected"/> when <paramref name="index"/> has its colour (a palette can hold one colour twice), else the index.</summary>
    private static int PaletteMatch(byte[] palette, int index, int expected) =>
        palette.AsSpan(index * 3, 3).SequenceEqual(palette.AsSpan(expected * 3, 3)) ? expected : index;

    private static RoomModel Room(ScummBlock root, int number)
    {
        foreach (var lflf in root.FindChildrenRecursive(ScummTag.LFLF))
            if (RoomModel.TryCreate(lflf, out var room) && room.RoomNo == number) return room;
        throw new InvalidOperationException($"no room {number}");
    }

    /// <summary>A frame at the canvas's own size (853×480), so a canvas pixel is a game unit.</summary>
    private static SKBitmap Frame(GameFrame frame, string name)
    {
        var bitmap = new SKBitmap(853, 480);
        using (var canvas = new SKCanvas(bitmap)) frame.Render(canvas, new SKRect(0, 0, 853, 480));
        if (Environment.GetEnvironmentVariable("MMUCS_FRAMES") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            using var data = SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(Path.Combine(dir, $"{name}.png"), data.ToArray());
        }
        return bitmap;
    }

    [Fact]
    public void With_the_smooth_camera_actors_slide_from_the_last_frame_s_place_and_jumps_land_at_once()
    {
        static Runtime.Scripts.ActorPicture At(int fromX, int fromY, int x, int y) =>
            new(1, new Runtime.Scripts.Costume(2, null, null), new AkosFrame(), false, 255, x, y, 0, false, fromX, fromY);

        Assert.Equal((100f, 50f), RoomSceneBuilder.Slid(At(100, 50, 108, 52), 0));
        Assert.Equal((104f, 51f), RoomSceneBuilder.Slid(At(100, 50, 108, 52), 0.5));
        Assert.Equal((108f, 52f), RoomSceneBuilder.Slid(At(100, 50, 108, 52), 1)); // without it: where the engine has it
        Assert.Equal((300f, 50f), RoomSceneBuilder.Slid(At(100, 50, 300, 50), 0.5)); // a put-actor across the room
    }
}
