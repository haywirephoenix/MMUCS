using MMUCS.Core.Fonts;
using MMUCS.Core.Media;
using MMUCS.Player;
using MMUCS.Player.Text;
using SkiaSharp;
using Xunit;

namespace MMUCS.Player.Tests;

/// <summary>
/// The player draws cutscene frames headless too: the picture with its text in the game's font, fitted into
/// any viewport with black around it. Needs the game (MMUCS_GAME); MMUCS_FRAMES=folder saves the frame.
/// </summary>
public class CutsceneFrameTests
{
    private static string Game => Environment.GetEnvironmentVariable("MMUCS_GAME") is { } g && File.Exists(g) ? g : null;

    [Fact]
    public void A_frame_with_text_draws_fitted_and_letterboxed()
    {
        if (Game == null) return;
        var item = MediaLibrary.Scan(Game, MediaKind.Cutscene, null).First(i => i.Id == "video/opening");
        var video = new SmushPlayer(MediaLibrary.LoadVideo(item));
        var text = SubtitleRenderer.Create(NutFont.LoadAll(Game), SubtitleFontKind.Game, null, subtitles: true);
        int index = 0;
        while (index < video.FrameCount && video.ShowFrame(index) && video.Current.Texts.Count == 0) index++;
        Assert.True(index < video.FrameCount, "no frame with text in the opening");

        var frame = CutsceneFrame.From(video, text);
        try
        {
            Assert.Equal((640, 480), (frame.Width, frame.Height));
            Assert.NotNull(frame.Subtitles);

            // A 16:9 viewport: the 4:3 picture as tall as it, bars either side.
            using var bitmap = new SKBitmap(1280, 720);
            using (var canvas = new SKCanvas(bitmap)) frame.Render(canvas, new SKRect(0, 0, 1280, 720));
            Assert.Equal(SKColors.Black, bitmap.GetPixel(20, 360));
            Assert.Equal(SKColors.Black, bitmap.GetPixel(1260, 360));
            if (Environment.GetEnvironmentVariable("MMUCS_FRAMES") is { Length: > 0 } dir)
            {
                Directory.CreateDirectory(dir);
                using var data = SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(Path.Combine(dir, $"opening-{index}.png"), data.ToArray());
            }

            // Drawn without the text, the frame differs where the text goes.
            var plain = CutsceneFrame.From(video);
            try
            {
                using var a = Draw(frame);
                using var b = Draw(plain);
                int differ = 0;
                for (int y = 0; y < 480; y++)
                    for (int x = 0; x < 640; x++)
                        if (a.GetPixel(x, y) != b.GetPixel(x, y)) differ++;
                Assert.True(differ > 100, $"{differ} pixels of text");
            }
            finally { plain.Release(); }
        }
        finally { frame.Release(); }
    }

    private static SKBitmap Draw(CutsceneFrame frame)
    {
        var bitmap = new SKBitmap(640, 480);
        using var canvas = new SKCanvas(bitmap);
        frame.Draw(canvas);
        return bitmap;
    }
}
