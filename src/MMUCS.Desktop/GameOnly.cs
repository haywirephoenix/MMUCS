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
/// <c>MMUCS --game [FILE] [--boot N]</c>: just the game window, for quick testing until there's a standalone
/// player. It loads FILE (or the game opened last), without the main window, the hierarchy's costume pass or
/// the single-instance hand-off, opens the game window and presses Play once it's ready. Closing it quits.
/// <c>--boot N</c> gives the boot script parameter N (ScummVM's <c>-b</c>), which COMI uses to start in a room.
/// </summary>
internal static class GameOnly
{
    public const string Flag = "--game";
    public const string BootFlag = "--boot";

    public static bool Requested(string[] args) => args?.Contains(Flag) == true;

    public static void Start(IClassicDesktopStyleApplicationLifetime desktop)
    {
        desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
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
        _ = OpenAsync(desktop, Path.GetFullPath(file), settings, boot);
    }

    private static async Task OpenAsync(IClassicDesktopStyleApplicationLifetime desktop, string file, AppSettings settings, int bootParam)
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
        }
        catch (Exception ex)
        {
            Log.Error($"{Flag}: {ex}");
            Dispatcher.UIThread.Post(() => desktop.Shutdown(1));
        }
    }
}
