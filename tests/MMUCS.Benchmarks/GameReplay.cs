using System.Diagnostics;
using MMUCS.Core.Scumm;
using MMUCS.Player;
using MMUCS.Player.Rooms;
using MMUCS.Player.Text;
using MMUCS.Runtime;
using SkiaSharp;

namespace MMUCS.Benchmarks;

/// <summary>
/// A game window recording played through the window's own drawing: <see cref="ScriptedGame"/> ticked a jiffy
/// at a time, <see cref="ScriptedGame.Present"/> and <see cref="ScriptedView.Capture"/> several times a jiffy
/// (a fast display while the smooth camera moves), each frame drawn on a 1920×1080 raster surface with
/// <c>--draw</c> (slow: on the CPU a hi-res mod's tint shader takes hundreds of ms a frame, which the window's GPU doesn't). Prints, for
/// each room, what capturing and drawing cost and how memory moved, then what's left after a full collection
/// (memory still held then is kept by something; memory freed only then was garbage waiting on the GC).
/// <para>Options after the game: <c>--file F</c> (default: the latest recording), <c>--presents N</c> per jiffy
/// (default 2, a 120 Hz display), <c>--no-smooth</c>, <c>--every S</c> seconds for a memory line within a room
/// (default 10), <c>--collect</c> (a full collection at each line, to show what's held), <c>--seconds S</c> (run on past the recording's end, the game left idle), <c>--draw</c>, <c>--fast</c> (as fast as it goes, not in real time: scenes built in the
/// background may not arrive). Hi-res mods are on when the game's mods folder enables them, as in the window. No sound.</para>
/// </summary>
public static class GameReplay
{
    public static void Run()
    {
        var args = Environment.GetCommandLineArgs();
        string Opt(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        string file = Opt("--file") ?? InputRecording.Latest();
        if (file == null || !File.Exists(file))
        {
            Console.WriteLine($"no recording (give --file, or record one in {InputRecording.DefaultFolder})");
            return;
        }
        int presents = int.TryParse(Opt("--presents"), out var p) ? Math.Max(1, p) : 2;
        double every = double.TryParse(Opt("--every"), System.Globalization.CultureInfo.InvariantCulture, out var e) ? e : 10;
        bool smooth = !args.Contains("--no-smooth");
        bool drawing = args.Contains("--draw"), fast = args.Contains("--fast");

        var (boot, events) = InputRecording.Read(file);
        long endJiffy = events.Find(x => x.Kind == "end") is { Kind: not null } end ? end.Jiffy : events.Count > 0 ? events[^1].Jiffy + 60 : 600;
        if (double.TryParse(Opt("--seconds"), System.Globalization.CultureInfo.InvariantCulture, out var secs)) endJiffy = (long)(secs * 60);
        var root = ScummProject.Load(ScummProject.FindRelatedFiles(Host.GameFile), null);
        using var game = new ScriptedGame();
        game.Load(root, Host.GameFile);
        game.BootParam = boot;
        game.SmoothCamera = smooth;
        game.SmoothWalk = smooth;
        var view = new ScriptedView(game) { Text = SubtitleRenderer.Create(game.Fonts, SubtitleFontKind.Game, null, true) };
        var hiRes = HiResMods.Load(Host.GameFile);
        Console.WriteLine($"{Path.GetFileName(file)}: {events.Count} inputs over {endJiffy / 60.0:0} s, boot {boot}; {presents} presents a jiffy{(drawing ? ", drawn" : ", not drawn")}, smooth camera {(smooth ? "on" : "off")}; " +
                          $"hi-res mods: {(hiRes.Mods.Count == 0 ? "none" : string.Join(", ", hiRes.Mods))}");
        game.StartReplay(events);
        game.Play();

        using var surface = SKSurface.Create(new SKImageInfo(1920, 1080, SKColorType.Rgba8888, SKAlphaType.Premul));
        var viewport = new SKRect(0, 0, 1920, 1080);
        var proc = Process.GetCurrentProcess();
        var seg = new Segment(game, proc);
        long nextSample = (long)(every * 60);
        GameFrame last = null;
        var watch = new Stopwatch();
        var clock = Stopwatch.StartNew();
        Console.WriteLine("   room  seconds  captures  capture ms (avg/max)  draw ms (avg/max)  palette changes  MB/s made  GCs 0/1/2, paused  managed MB  working set MB  private MB  shown");
        for (long j = 0; j < endJiffy && game.IsPlaying; j++)
        {
            game.Tick();
            if (game.CurrentRoom != seg.Room)
            {
                seg.Print(proc, "");
                seg = new Segment(game, proc);
            }
            for (int k = 0; k < presents; k++)
            {
                // In real time, as the window runs, so scenes built in the background (hi-res pictures) arrive as they would.
                double due = (j + k / (double)presents) * 1000 / 60;
                if (!fast && clock.Elapsed.TotalMilliseconds < due) Thread.Sleep(TimeSpan.FromMilliseconds(due - clock.Elapsed.TotalMilliseconds));
                game.Present(k / (double)presents);
                watch.Restart();
                var frame = view.Capture();
                double capture = watch.Elapsed.TotalMilliseconds;
                watch.Restart();
                if (drawing)
                {
                    surface.Canvas.Clear(SKColors.Black);
                    frame.Render(surface.Canvas, viewport);
                    surface.Canvas.Flush();
                }
                double draw = watch.Elapsed.TotalMilliseconds;
                if (!ReferenceEquals(last, frame)) last?.Release();
                last = frame;
                seg.Add(capture, draw, frame);
            }
            if (game.Loop != null && game.Loop.Jiffies >= nextSample)
            {
                nextSample += (long)(every * 60);
                seg.Print(proc, "…");
            }
        }
        seg.Print(proc, "");
        last?.Release();
        Console.WriteLine($"last scene build: {view.Building.Status} {view.Building.Exception?.GetBaseException().Message}");

        proc.Refresh();
        long before = proc.PrivateMemorySize64;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        proc.Refresh();
        Console.WriteLine($"after a full collection: managed {GC.GetTotalMemory(false) >> 20} MB, working set {proc.WorkingSet64 >> 20} MB, " +
                          $"private {proc.PrivateMemorySize64 >> 20} MB (was {before >> 20} MB); {game.Faults} script faults");
    }

    /// <summary>A stretch of one room: frame costs and memory from its start.</summary>
    private sealed class Segment
    {
        private readonly ScriptedGame _game;
        private readonly long _jiffy;
        private readonly int _palette;
        private readonly long _allocated;
        private readonly int[] _gcs;
        private readonly TimeSpan _paused = GC.GetTotalPauseDuration();
        public int Room { get; }
        private int _count;
        private double _capture, _captureMax, _draw, _drawMax;

        public Segment(ScriptedGame game, Process proc)
        {
            _game = game;
            Room = game.CurrentRoom;
            _jiffy = game.Loop?.Jiffies ?? 0;
            _palette = game.Vm?.Palette.Version ?? 0;
            _allocated = GC.GetTotalAllocatedBytes();
            _gcs = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
        }

        private GameFrame _last;

        public void Add(double capture, double draw, GameFrame frame)
        {
            _last = frame;
            _count++;
            _capture += capture;
            _draw += draw;
            _captureMax = Math.Max(_captureMax, capture);
            _drawMax = Math.Max(_drawMax, draw);
        }

        public void Print(Process proc, string mark)
        {
            if (_count == 0) return;
            proc.Refresh();
            double seconds = ((_game.Loop?.Jiffies ?? 0) - _jiffy) / 60.0;
            int palette = (_game.Vm?.Palette.Version ?? 0) - _palette;
            Console.WriteLine($"{mark,1} {Room,5}  {seconds,7:0.0}  {_count,8}  {_capture / _count,9:0.00} / {_captureMax,7:0.0}  {_draw / _count,8:0.00} / {_drawMax,6:0.0}  " +
                              $"{palette,15}  {(GC.GetTotalAllocatedBytes() - _allocated) / Math.Max(seconds, 0.001) / (1 << 20),8:0.0}  {GC.CollectionCount(0) - _gcs[0],3}/{GC.CollectionCount(1) - _gcs[1],2}/{GC.CollectionCount(2) - _gcs[2],2} {(GC.GetTotalPauseDuration() - _paused).TotalMilliseconds,6:0} ms  " +
                              $"{GC.GetTotalMemory(false) >> 20,10}  {proc.WorkingSet64 >> 20,14}  {proc.PrivateMemorySize64 >> 20,10}  {Shown(_last)}");
            if (Environment.GetCommandLineArgs().Contains("--collect"))
            {
                // What's really held: everything collectable collected and finalized (slows the run at each line).
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                proc.Refresh();
                var gc = GC.GetGCMemoryInfo();
                Console.WriteLine($"         collected: managed {GC.GetTotalMemory(false) >> 20} MB (GC committed {gc.TotalCommittedBytes >> 20} MB, fragmented {gc.FragmentedBytes >> 20} MB), working set {proc.WorkingSet64 >> 20} MB, private {proc.PrivateMemorySize64 >> 20} MB");
            }
        }

        /// <summary>What the last frame showed: a video, black, or the room's hi-res background, objects and actors.</summary>
        private static string Shown(GameFrame f) =>
            f?.Cutscene != null ? "video" : f?.Room is not { } r || ReferenceEquals(r, RoomScene.Blank) ? "black"
            : $"{(r.HiResBackground != null ? "hi-res, " : "")}{r.Objects.Count} objects, {r.Actors.Count} actors";
    }
}
