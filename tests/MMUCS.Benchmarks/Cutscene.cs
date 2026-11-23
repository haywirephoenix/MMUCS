using System.Diagnostics;
using Avalonia.Controls;
using MMUCS.Player;
using MMUCS.Core.Media;
using MMUCS.Desktop.Controls;

namespace MMUCS.Benchmarks;

/// <summary>The cutscene player's picture path: decode, convert to a Skia image, show, free.</summary>
public static class Cutscene
{
    public static void Run() => Host.Run(async () =>
    {
        var item = MediaLibrary.Scan(Host.GameFile, MediaKind.Cutscene, null).First(i => i.Id == "video/opening");

        // Time per frame: codec 47 decode, then the palette conversion into a Skia image.
        var player = new SmushPlayer(MediaLibrary.LoadVideo(item));
        double decode = 0, convert = 0;
        var sw = new Stopwatch();
        for (int i = 0; i < player.FrameCount; i++)
        {
            sw.Restart(); player.ShowFrame(i); decode += sw.Ms();
            sw.Restart(); CutsceneFrame.From(player).Release(); convert += sw.Ms();
        }
        int n = player.FrameCount;
        Console.WriteLine($"video/opening, {n} frames: decode {decode / n:0.00} ms, convert {convert / n:0.00} ms per frame");

        // Shown through the real canvas: each replaced frame is freed once the renderer is done with it.
        player = new SmushPlayer(MediaLibrary.LoadVideo(item));
        var canvas = new VideoCanvas();
        var window = new Window { Width = 800, Height = 600, Content = canvas };
        window.Show();
        var made = new List<CutsceneFrame>();
        CutsceneFrame current = null;
        for (int i = 0; i < 200; i++)
        {
            player.ShowFrame(i);
            var f = CutsceneFrame.From(player);
            made.Add(f);
            canvas.Image = f;
            current?.Release();
            current = f;
            window.CaptureRenderedFrame();
        }
        await Task.Delay(50);
        window.CaptureRenderedFrame();
        int freed = made.Count(f => f.Image.Handle == IntPtr.Zero);
        Console.WriteLine($"200 frames shown: {freed} freed, the one on screen {(made[^1].Image.Handle != IntPtr.Zero ? "alive" : "FREED")}");
        window.Close();
    });
}
