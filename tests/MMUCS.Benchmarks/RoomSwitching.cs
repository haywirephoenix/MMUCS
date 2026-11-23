using System.Diagnostics;
using System.Reflection;
using MMUCS.Core.Scumm;
using MMUCS.Desktop.Rendering;
using MMUCS.Desktop.ViewModels;

namespace MMUCS.Benchmarks;

/// <summary>
/// Switching rooms in the Room viewer (objects and costumes shown, Objects panel open): what each step
/// costs, and what the picture does at different press speeds. A single switch should go straight from
/// the old room to the whole new one; a held key shows black until the selection settles.
/// </summary>
public static class RoomSwitching
{
    public static void Run() => Host.Run(async () =>
    {
        var rooms = Host.SceneRooms(60);
        int n = rooms.Count;

        // The steps of a room load, summed over the rooms (caches warm after the first pass).
        foreach (var r in rooms) await ScummBackgroundCache.GetCachedBackgroundAsync(BackgroundId(r), r.Rmhd, r.Room, false, 0, CancellationToken.None);
        double bg = 0, objects = 0, actors = 0, skia = 0;
        int objectCount = 0, actorCount = 0;
        var sw = new Stopwatch();
        foreach (var r in rooms)
        {
            sw.Restart();
            var b = await ScummBackgroundCache.GetCachedBackgroundAsync(BackgroundId(r), r.Rmhd, r.Room, false, 0, CancellationToken.None);
            bg += sw.Ms();
            sw.Restart(); b.ExecutedImage?.ToSkImage()?.Dispose(); skia += sw.Ms();
            foreach (var o in r.Objects.Where(o => o.ImageCount > 0))
            {
                sw.Restart();
                var res = await ObimCache.GetCachedObimAsync(o.Block, 0, 0, true, CancellationToken.None);
                objects += sw.Ms();
                objectCount++;
                sw.Restart(); res.ExecutedImage?.ToSkImage()?.Dispose(); skia += sw.Ms();
            }
            foreach (var a in RoomActors.Analyze(r).Where(a => a.CanDraw))
            {
                sw.Restart();
                lock (a.Data)
                {
                    AkosParser.UpdatePaletteOnly(a.Data, a.Akos, a.Data.DefaultPaletteIndex);
                    AkosFrames.Render(a.Data, a.Frame, out _, out _, a.Mirrored)?.ToSkImage()?.Dispose();
                }
                actors += sw.Ms();
                actorCount++;
            }
        }
        Console.WriteLine($"per room ({objectCount / (double)n:0} object images, {actorCount / (double)n:0.0} actors): background {bg / n:0.0} ms, " +
                          $"object images {objects / n:0.0} ms, actors {actors / n:0.0} ms, to Skia {skia / n:0.0} ms");

        var (window, vm) = Host.RoomWindow();
        await Host.Warm(vm, rooms);
        var counts = Count(vm);

        // Single presses: the whole switch, and what showed.
        double total = 0;
        counts.Reset();
        foreach (var r in rooms.Take(15))
        {
            sw.Restart();
            _ = vm.LoadAsync(r);
            await Host.Settled(vm);
            total += sw.Ms();
            await Task.Delay(400);
        }
        Console.WriteLine($"single presses: {total / 15:0} ms a switch; {counts.Whole} appeared whole, {counts.BackgroundAlone} showed a background alone, {counts.Covers} went black");

        // A held key: a new room every 33 ms, then let go.
        counts.Reset();
        int listed = 0;
        var clock = Stopwatch.StartNew();
        for (int i = 0; i < n; i++)
        {
            while (clock.Ms() < i * 33) await Task.Delay(1);
            _ = vm.LoadAsync(rooms[(i + 15) % n]);
            if (i >= 2) listed = Math.Max(listed, vm.Objects.Count);
            if (i == n / 2 && vm.IsCovered) counts.BlackOnScreen = Host.Pixel(window, Host.RoomCentre.X, Host.RoomCentre.Y) == Host.Black;
        }
        double release = clock.Ms();
        await Host.Settled(vm);
        Console.WriteLine($"held key over {n} rooms: black on screen mid-scroll {counts.BlackOnScreen}, {counts.BackgroundAlone} backgrounds alone, " +
                          $"Objects list at most {listed} entries while held; whole {clock.Ms() - release:0} ms after letting go");
        window.Close();
    });

    /// <summary>How often rooms go black at press speeds from a held key (33 ms) to slow presses.</summary>
    public static void RunPressSpeeds() => Host.Run(async () =>
    {
        var rooms = Host.SceneRooms(24);
        var (window, vm) = Host.RoomWindow();
        await Host.Warm(vm, rooms);
        var counts = Count(vm);
        foreach (int gap in new[] { 33, 50, 80, 100, 130, 160, 200, 300 })
        {
            counts.Reset();
            var clock = Stopwatch.StartNew();
            for (int i = 0; i < rooms.Count; i++)
            {
                while (clock.Ms() < i * gap) await Task.Delay(1);
                _ = vm.LoadAsync(rooms[(i + 1) % rooms.Count]);
            }
            await Host.Settled(vm);
            Console.WriteLine($"a press every {gap,3} ms over {rooms.Count} rooms: {counts.Covers,2} went black, {counts.Whole,2} appeared whole");
            await Task.Delay(500);
        }
        window.Close();
    });

    /// <summary>
    /// Single switches with each room's costume analysis held up for 300 ms (a slow machine): the old room
    /// should go black at about 100 ms and the new one appear whole.
    /// </summary>
    public static void RunSlow() => Host.Run(async () =>
    {
        var rooms = Host.SceneRooms(16);
        var (window, vm) = Host.RoomWindow();
        await Host.Warm(vm, rooms);
        var counts = Count(vm);
        var sync = typeof(RoomActors).GetField("Sync", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
                   ?? throw new InvalidOperationException("RoomActors.Sync not found: update the slow-machine simulation");
        foreach (bool slow in new[] { false, true })
        {
            counts.Reset();
            int black = 0;
            double total = 0;
            for (int i = 0; i < rooms.Count; i++)
            {
                if (slow)
                {
                    var held = new ManualResetEventSlim();
                    new Thread(() => { lock (sync) { held.Set(); Thread.Sleep(300); } }).Start();
                    held.Wait();
                }
                counts.Since.Restart();
                counts.CoveredAt = -1;
                bool sampled = false;
                _ = vm.LoadAsync(rooms[(i + 1) % rooms.Count]);
                while (vm.IsSwitchingRoom || vm.IsCovered)
                {
                    await Task.Delay(1);
                    if (!sampled && vm.IsCovered && counts.Since.Ms() > counts.CoveredAt + 20)
                    {
                        sampled = true;
                        if (Host.Pixel(window, Host.RoomCentre.X, Host.RoomCentre.Y) == Host.Black) black++;
                    }
                }
                total += counts.Since.Ms();
                await Task.Delay(600);
            }
            Console.WriteLine($"{(slow ? "slow" : "normal")} single presses: {total / rooms.Count:0} ms a switch; covered {counts.Covers} of {rooms.Count}" +
                              (counts.Covers > 0 ? $" (black on screen {black})" : "") + $"; appeared whole {counts.Whole}, background alone {counts.BackgroundAlone}");
        }
        window.Close();
    });

    private static int BackgroundId(RoomModel r) => (int)Core.StableHash.Of(r.Room.SourceKey, r.RoomNo > 0 ? r.RoomNo : r.Room.Offset);

    /// <summary>What the Room viewer showed each time a switch stopped holding the old room.</summary>
    private sealed class Counts
    {
        public int Covers, Whole, BackgroundAlone;
        public bool BlackOnScreen;
        public readonly Stopwatch Since = new();
        public double CoveredAt = -1;
        public void Reset() { Covers = Whole = BackgroundAlone = 0; BlackOnScreen = false; }
    }

    private static Counts Count(RoomViewModel vm)
    {
        var c = new Counts();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RoomViewModel.IsCovered) && vm.IsCovered)
            {
                c.Covers++;
                c.CoveredAt = c.Since.Ms();
            }
            if (e.PropertyName == nameof(RoomViewModel.IsSwitchingRoom) && !vm.IsSwitchingRoom && !vm.IsCovered)
            {
                if (vm.ObjectLayers is { Count: > 0 }) c.Whole++;
                else c.BackgroundAlone++;
            }
        };
        return c;
    }
}
