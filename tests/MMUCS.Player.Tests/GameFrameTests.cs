using MMUCS.Core.Fonts;
using MMUCS.Core.Scumm;
using MMUCS.Player;
using MMUCS.Player.Text;
using MMUCS.Runtime;
using SkiaSharp;
using Xunit;

namespace MMUCS.Player.Tests;

/// <summary>
/// The player draws the game headless, onto an off-screen Skia surface of any size: the 16:9 picture
/// letterboxed, the inventory on the 4:3 page in its middle, the label in the game's font, the cursor on
/// its hotspot. Needs the game (MMUCS_GAME); MMUCS_FRAMES=folder also saves the frames as PNGs.
/// </summary>
public class GameFrameTests
{
    private static string Game => Environment.GetEnvironmentVariable("MMUCS_GAME") is { } g && File.Exists(g) ? g : null;

    [Fact]
    public void Viewport_and_game_points_map_both_ways()
    {
        // A 4:3 viewport: the 16:9 picture fills its width, with bars above and below.
        var view = new SKRect(0, 0, 800, 600);
        var picture = GameView.PictureRect(view);
        Assert.Equal(800, picture.Width, 3);
        Assert.Equal(450, picture.Height, 3);
        Assert.Equal(75, picture.Top, 3);
        var p = GameView.ToGame(view, new SKPoint(400, 300));
        Assert.Equal(GameState.CanvasWidth / 2, p.X, 3);
        Assert.Equal(GameState.CanvasHeight / 2, p.Y, 3);
        var back = GameView.ToView(view, new GamePoint(10, 20));
        var again = GameView.ToGame(view, back);
        Assert.Equal(10, again.X, 3);
        Assert.Equal(20, again.Y, 3);
    }

    [Fact]
    public async Task Frames_draw_the_inventory_label_and_cursor()
    {
        if (Game == null) return;
        var root = ScummProject.Load(ScummProject.FindRelatedFiles(Game), null);
        var game = new GameState();
        await game.LoadAsync(root);
        var view = new GameView(game) { Text = SubtitleRenderer.Create(NutFont.LoadAll(Game), SubtitleFontKind.Game, null, true) };

        // A stopped game: letterbox and a dimmed stage, nothing else.
        using var stopped = Draw(view.Capture(), 1000, 480, "stopped");
        Assert.Equal(SKColors.Black, stopped.GetPixel(10, 240)); // the bars either side of 853×480
        Assert.NotEqual(SKColors.Black, stopped.GetPixel(500, 240));

        game.Play();
        game.SetMouseCaptured(true);
        game.ToggleInventory();
        var item = GameState.PageToGame(Inventory.ItemAt(0)) + new GamePoint(20, 20);
        game.MoveCursor(item);
        var frame = view.Capture();
        Assert.Equal(1 + 1 + Inventory.PageSize, frame.Blasts.Count);
        Assert.NotNull(frame.LabelText);
        Assert.NotNull(frame.Cursor);
        Assert.Equal(game.CursorPosition - game.CursorHotspot, frame.Cursor.Value.Bounds.TopLeft);

        // At the game's own size (854×480): the chest, the label under it, the cursor over the item.
        using var open = Draw(frame, 854, 480, "inventory");
        var chest = GameState.PageToGame(new GamePoint(320, 330)); // the chest's lower boards
        Assert.NotEqual(Stage(open), open.GetPixel((int)chest.X, (int)chest.Y));
        int labelInk = 0;
        for (int x = 250; x < 600; x++)
            for (int y = 395; y < 425; y++)
                if (open.GetPixel(x, y) != Stage(open) && open.GetPixel(x, y) != SKColors.Black) labelInk++;
        Assert.True(labelInk > 50, $"{labelInk} label pixels");

        // The same frame at twice the size: the same picture, scaled (game pixels stay square).
        using var big = Draw(frame, 1708, 960, "inventory-2x");
        Assert.Equal(open.GetPixel((int)chest.X, (int)chest.Y), big.GetPixel((int)chest.X * 2, (int)chest.Y * 2));
    }

    private static SKColor Stage(SKBitmap b) => b.GetPixel(5, 5);

    private static SKBitmap Draw(GameFrame frame, int width, int height, string name)
    {
        var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap)) frame.Render(canvas, new SKRect(0, 0, width, height));
        if (Environment.GetEnvironmentVariable("MMUCS_FRAMES") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            using var data = SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(Path.Combine(dir, $"{name}.png"), data.ToArray());
        }
        return bitmap;
    }
}
