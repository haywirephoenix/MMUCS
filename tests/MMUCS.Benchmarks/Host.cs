using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using MMUCS.Core.Scumm;
using MMUCS.Desktop;
using MMUCS.Desktop.ViewModels;
using MMUCS.Desktop.Views.Room;

namespace MMUCS.Benchmarks;

/// <summary>
/// What the benchmarks share: the app (its styles and resources) on Avalonia's headless platform with
/// real Skia rendering, the game, and windows holding the app's own views.
/// <para>Benchmarks run inside the dispatcher's main loop, as the app does. Pumping jobs by hand instead
/// leaves dispatcher timers unfired and adds a delay to every await, which skews timings.</para>
/// </summary>
public static class Host
{
    private static bool _started;
    private static ScummBlock _root;

    public static string GameFile { get; set; }

    /// <summary>The game's resource files, parsed once (with every room's costumes matched, as the app does).</summary>
    public static ScummBlock Root
    {
        get
        {
            if (_root != null) return _root;
            _root = ScummProject.Load(ScummProject.FindRelatedFiles(GameFile));
            RoomActors.AnalyzeAll(_root);
            return _root;
        }
    }

    public static List<RoomModel> Rooms(Func<RoomModel, bool> where = null) =>
        [.. Root.FindChildrenRecursive(ScummTag.LFLF)
            .Select(l => RoomModel.TryCreate(l, out var r) ? r : null)
            .Where(r => r != null && (where == null || where(r)))];

    /// <summary>Rooms with a scene to show: a background and a few objects.</summary>
    public static List<RoomModel> SceneRooms(int count) => [.. Rooms(r => r.Width > 0 && r.Objects.Count > 3).Take(count)];

    /// <summary>Runs <paramref name="benchmark"/> on the UI thread inside the dispatcher's main loop.</summary>
    public static void Run(Func<Task> benchmark)
    {
        if (!_started)
        {
            AppBuilder.Configure<App>().UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .SetupWithoutStarting();
            _started = true;
        }

        var done = new CancellationTokenSource();
        Exception failure = null;
        Dispatcher.UIThread.Post(async () =>
        {
            try { await benchmark(); }
            catch (Exception ex) { failure = ex; }
            done.Cancel();
        });
        Dispatcher.UIThread.MainLoop(done.Token);
        if (failure != null) throw new Exception("Benchmark failed", failure);
    }

    /// <summary>A window with the Room viewer's picture and its Objects panel, as the app lays them out.</summary>
    public static (Window Window, RoomViewModel Vm) RoomWindow(int showMode = RoomViewModel.ShowRoomStart, bool objectsPanel = true)
    {
        var vm = new RoomViewModel { ShowMode = showMode };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,300") };
        grid.Children.Add(new RoomPreviewView { DataContext = vm });
        if (objectsPanel)
        {
            var panel = new RoomObjectsView { DataContext = vm };
            Grid.SetColumn(panel, 1);
            grid.Children.Add(panel);
        }
        var window = new Window { Width = 1280, Height = 800, Content = grid };
        window.Show();
        return (window, vm);
    }

    /// <summary>Waits until a room switch has finished: nothing held, nothing covered.</summary>
    public static async Task Settled(RoomViewModel vm)
    {
        while (vm.IsSwitchingRoom || vm.IsCovered) await Task.Delay(1);
    }

    private static readonly Stopwatch SinceSwitch = Stopwatch.StartNew();

    /// <summary>
    /// A single room switch, as a click: at least 400 ms after the last one finished. Under 90 ms the
    /// viewer takes switches for a held key and covers them; and a switch within a few hundred ms of the
    /// last one still runs into its leftover work (room 3 measured 73–83 ms 150 ms after the last switch,
    /// 28–42 ms 400 ms after). Returns how long the switch took.
    /// </summary>
    public static async Task<double> Switch(RoomViewModel vm, RoomModel room)
    {
        while (SinceSwitch.Ms() < 400) await Task.Delay(5);
        var sw = Stopwatch.StartNew();
        _ = vm.LoadAsync(room);
        await Settled(vm);
        SinceSwitch.Restart();
        return sw.Ms();
    }

    /// <summary>Opens each room in turn and waits for it, so later timings run with warm caches.</summary>
    public static async Task Warm(RoomViewModel vm, IEnumerable<RoomModel> rooms)
    {
        foreach (var r in rooms) await Switch(vm, r);
    }

    /// <summary>The colour of one pixel of the window as rendered now (BGRA).</summary>
    public static unsafe uint Pixel(Window window, int x, int y)
    {
        using var frame = window.CaptureRenderedFrame();
        using var buffer = frame.Lock();
        return ((uint*)buffer.Address)[y * (buffer.RowBytes / 4) + x];
    }

    /// <summary>A point inside the room picture of <see cref="RoomWindow"/> (the room is fitted and centred there).</summary>
    public static (int X, int Y) RoomCentre => (490, 450);

    public const uint Black = 0xFF000000;

    public static double Ms(this Stopwatch sw) => sw.Elapsed.TotalMilliseconds;
}
