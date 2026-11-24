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
