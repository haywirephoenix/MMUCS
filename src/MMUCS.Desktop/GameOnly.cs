using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using MMUCS.Core.Diagnostics;
using MMUCS.Core.Scumm;
using MMUCS.Desktop.Services;
using MMUCS.Desktop.ViewModels;
using MMUCS.Desktop.Views.Game;

namespace MMUCS.Desktop;

/// <summary>
/// <c>MMUCS --game [FILE] [--boot N] [--load SAVE]</c>: just the game window, for quick testing until there's a standalone
/// player. It loads FILE (or the game opened last), without the main window, the hierarchy's costume pass or
/// the single-instance hand-off, opens the game window and presses Play once it's ready. Closing it quits.
/// <c>--boot N</c> gives the boot script parameter N (ScummVM's <c>-b</c>), which COMI uses to start in a room.
/// <c>--load SAVE</c> loads a save once the game has booted: 0 is the quick save (F6's), anything else a save
/// file's path.
/// </summary>
internal static class GameOnly
{
    public const string Flag = "--game";
    public const string BootFlag = "--boot";
    public const string LoadFlag = "--load";

    public static bool Requested(string[] args) => args?.Contains(Flag) == true;

    public static void Start(IClassicDesktopStyleApplicationLifetime desktop)
    {
        desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
        // No main window shows the log here, so it goes to the terminal.
        Log.Message += (level, text) => Console.Error.WriteLine(level == LogLevel.Info ? text : $"{level}: {text}");
        var args = desktop.Args ?? [];
        int at = Array.IndexOf(args, Flag);
        var settings = AppSettings.Load();
        string file = at + 1 < args.Length && !args[at + 1].StartsWith("--") ? args[at + 1] : settings.LastFile;
        if (file == null || !File.Exists(file))
        {
            Log.Error($"{Flag}: no game to open ({(file == null ? "give a file, or open one in MMUCS first" : $"{file} not found")})");
            desktop.Shutdown(1);
            return;
        }
        int boot = 0;
        int bootAt = Array.IndexOf(args, BootFlag);
        if (bootAt >= 0 && (bootAt + 1 >= args.Length || !int.TryParse(args[bootAt + 1], out boot)))
        {
            Log.Error($"{BootFlag}: give the boot parameter as a number");
            desktop.Shutdown(1);
            return;
        }
        string load = null;
        int loadAt = Array.IndexOf(args, LoadFlag);
        if (loadAt >= 0)
        {
            load = loadAt + 1 < args.Length && !args[loadAt + 1].StartsWith("--") ? args[loadAt + 1] : null;
            if (load == null || (load != "0" && !File.Exists(load)))
            {
                Log.Error($"{LoadFlag}: give 0 for the quick save or a save file's path{(load == null ? "" : $" ({load} not found)")}");
                desktop.Shutdown(1);
                return;
            }
            if (load != "0") load = Path.GetFullPath(load);
        }
        _ = OpenAsync(desktop, Path.GetFullPath(file), settings, boot, load);
    }

    private static async Task OpenAsync(IClassicDesktopStyleApplicationLifetime desktop, string file, AppSettings settings, int bootParam, string load)
    {
        try
        {
            var files = ScummProject.FindRelatedFiles(file);
            var root = await Task.Run(() => ScummProject.Load(files, null));
            var vm = new GameViewModel(root, files.Count > 0 ? Path.GetFullPath(files[0]) : file, settings);
            vm.Scripted.BootParam = bootParam;
            var window = new GameWindow { DataContext = vm };
            desktop.MainWindow = window;
            window.Show();
            await vm.Loading;
            if (vm.PlayCommand.CanExecute(null)) vm.PlayCommand.Execute(null);
            if (load != null)
            {
                string path = load == "0" ? vm.Scripted.QuickSavePath : load;
                if (!File.Exists(path)) Log.Error($"{LoadFlag} 0: no quick save yet ({path})");
                else if (!vm.LoadSave(path)) Log.Error($"{LoadFlag}: couldn't load {path} (a video was playing, or see above)");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"{Flag}: {ex}");
            Dispatcher.UIThread.Post(() => desktop.Shutdown(1));
        }
    }
}
