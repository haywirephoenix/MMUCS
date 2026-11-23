using System.Diagnostics;
using Avalonia.VisualTree;
using MMUCS.Desktop.Controls;
using MMUCS.Desktop.ViewModels;
using SkiaSharp;

namespace MMUCS.Benchmarks;

/// <summary>The Room viewer's images: freed as rooms change, and loaded only when needed in an image library.</summary>
public static class RoomMemory
{
    /// <summary>Switches through 120 rooms twice, a click at a time: earlier rooms' images are freed, the shown ones never.</summary>
    public static void Run() => Host.Run(async () =>
    {
        var rooms = Host.Rooms().Take(120).ToList();
        var (window, vm) = Host.RoomWindow(objectsPanel: false);
        // Weak references, so watching the images doesn't keep them alive.
        var seen = new List<WeakReference<SKImage>>();
        var known = new HashSet<SKImage>(ReferenceEqualityComparer.Instance);
        var proc = Process.GetCurrentProcess();
        long peak = 0;
        var sw = Stopwatch.StartNew();
        for (int pass = 0; pass < 2; pass++)
            foreach (var r in rooms)
            {
                await Host.Switch(vm, r);
                window.CaptureRenderedFrame();
                foreach (var img in Shown(vm))
                    if (known.Add(img)) seen.Add(new WeakReference<SKImage>(img));
                known.Clear(); // only to skip repeats within one room
                proc.Refresh();
                peak = Math.Max(peak, proc.WorkingSet64);
            }
        await Task.Delay(50);
        window.CaptureRenderedFrame();
        var shownNow = Shown(vm).ToHashSet();
        int freed = seen.Count(w => !w.TryGetTarget(out var i) || i.Handle == IntPtr.Zero);
        Console.WriteLine($"{2 * rooms.Count} room switches in {sw.Ms():0} ms; {freed} of {seen.Count} images seen are freed; " +
                          $"shown now {shownNow.Count}, freed by mistake {shownNow.Count(i => i.Handle == IntPtr.Zero)}; peak working set {peak >> 20} MB");
        window.Close();
    });

    /// <summary>
    /// Room 3 (no background: an image library) opens on its backdrop with its icons hidden and unloaded;
    /// showing one loads just that one. A full-screen object toggled on draws even with Full-screen
    /// objects off, until the Show mode applies again.
    /// </summary>
    public static void RunLibrary() => Host.Run(async () =>
    {
        var all = Host.Rooms();
        var icons = all.Where(r => r.Width <= 0).ToList();
        var scenes = Host.SceneRooms(2);
        var (window, vm) = Host.RoomWindow();
        await Host.Warm(vm, scenes.Concat(icons));
        foreach (var r in icons)
        {
            await Host.Switch(vm, scenes[0]);
            double ms = await Host.Switch(vm, r);
            Console.WriteLine($"room {r.RoomNo} '{r.Name}': {ms:0} ms; shown: {string.Join(", ", vm.Objects.Where(o => o.IsVisible).Select(o => o.Object.Name))}; " +
                              $"background {vm.BackgroundObject?.Name ?? "none"}; {vm.ObjectLayers.Count(l => l.Image != null)} images loaded; " +
                              $"{vm.Objects.Count(o => o.HasToggle)} of {vm.Objects.Count} can be toggled");
        }

        if (icons.Count > 0)
        {
            var item = vm.Objects.First(o => o.Object.ImageCount > 0 && !o.IsVisible);
            int before = vm.ObjectLayers.Count(l => l.Image != null);
            item.IsVisible = true;
            for (int i = 0; i < 500 && vm.ObjectLayers.Count(l => l.Image != null) == before; i++) await Task.Delay(1);
            bool drawn = vm.ObjectLayers.First(l => ReferenceEquals(l.Object, item.Object)).Image != null;
            Console.WriteLine($"showing '{item.Object.Name}': {vm.ObjectLayers.Count(l => l.Image != null) - before} more image loaded, {(drawn ? "drawn" : "MISSING")}");
        }

        vm.ShowFullScreenObjects = false;
        var canvas = window.GetVisualDescendants().OfType<RoomCanvas>().First();
        foreach (var r in all.Where(r => r.Width > 0 && r.Objects.Any(o => o.ImageCount > 0 && o.Width >= RoomViewModel.FullWidth)))
        {
            await Host.Switch(vm, r);
            var full = vm.Objects.FirstOrDefault(o => o.Object.ImageCount > 0 && o.Object.Width >= RoomViewModel.FullWidth
                                                      && !ReferenceEquals(o.Object, vm.BackgroundObject) && o.HasToggle);
            if (full == null) continue;
            full.IsVisible = false;
            full.IsVisible = true;
            bool toggled = canvas.ToggledObjects?.Contains(full.Object) == true;
            vm.ResetVisibilityCommand.Execute(null);
            Console.WriteLine($"room {r.RoomNo}, '{full.Object.Name}' ({full.Object.Width} wide), Full-screen objects off: toggled on → drawn {toggled}; " +
                              $"Reset visibility → still forced {canvas.ToggledObjects?.Contains(full.Object) == true}");
            break;
        }
        window.Close();
    });

    private static IEnumerable<SKImage> Shown(RoomViewModel vm) =>
        new[] { vm.BackgroundImage }
            .Concat(vm.ObjectLayers?.SelectMany(l => new[] { l.Image, l.CostumeImage }) ?? [])
            .Concat(vm.ZMasks ?? [])
            .Where(i => i != null);
}
