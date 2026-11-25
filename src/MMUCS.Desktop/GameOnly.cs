using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using MMUCS.Core;
using MMUCS.Core.Diagnostics;
using MMUCS.Core.Scumm;
using MMUCS.Desktop.Services;
using MMUCS.Runtime;
using MMUCS.Desktop.ViewModels;
using MMUCS.Desktop.Views.Game;

namespace MMUCS.Desktop;

/// <summary>
/// <c>MMUCS --game [FILE] [--boot N] [--load SAVE] [--record [OUT]] [--replay [REC]] [--verbose]</c>: just the game window, for quick testing until there's a standalone
/// player. It loads FILE (or the game opened last), without the main window, the hierarchy's costume pass or
/// the single-instance hand-off, opens the game window and presses Play once it's ready. Closing it quits.
/// <c>--boot N</c> gives the boot script parameter N (ScummVM's <c>-b</c>), which COMI uses to start in a room.
/// <c>--load SAVE</c> loads a save once the game has booted: 0 is the quick save (F6's), anything else a save
/// file's path.
/// <c>--record [OUT]</c> writes every input that reaches the engine to OUT (an <see cref="InputRecording"/>; by default
/// a dated file in the app data's recordings folder), and on quitting prints the mprobe command that replays it
/// headless. <c>--replay [REC]</c> (by default the latest recording in that folder) plays a recording back in the window instead: its inputs drive the game at the
/// jiffies they were recorded at (the host's are ignored meanwhile), from the recording's boot parameter unless
/// <c>--boot</c> gives one; when it ends the game is the user's. <c>--verbose</c> logs more as it happens: rooms, videos, sounds, temporary saves and the inputs, each
/// with the game's time as mprobe boot shows it.
/// </summary>
internal static class GameOnly
{
    public const string Flag = "--game";
    public const string BootFlag = "--boot";
    public const string LoadFlag = "--load";
    public const string RecordFlag = "--record";
    public const string VerboseFlag = "--verbose";
    public const string ReplayFlag = "--replay";

    /// <summary>Where <c>--record</c> writes when given no path, and where <c>--replay</c> takes the latest from.</summary>
    private static string RecordingsFolder => InputRecording.DefaultFolder;

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
        string record = null;
        int recordAt = Array.IndexOf(args, RecordFlag);
        if (recordAt >= 0)
            record = Path.GetFullPath(recordAt + 1 < args.Length && !args[recordAt + 1].StartsWith("--") ? args[recordAt + 1]
                : Path.Combine(RecordingsFolder, $"{DateTime.Now:yyyyMMdd-HHmmss}{InputRecording.Extension}"));
        bool verbose = args.Contains(VerboseFlag);
        (int Boot, System.Collections.Generic.List<InputRecording.Event> Events)? replay = null;
        int replayAt = Array.IndexOf(args, ReplayFlag);
        if (replayAt >= 0)
        {
            // No path: the latest recording --record made in its default folder.
            string rec = replayAt + 1 < args.Length && !args[replayAt + 1].StartsWith("--") ? args[replayAt + 1] : InputRecording.Latest();
            if (rec == null || !File.Exists(rec) || load != null)
            {
                Log.Error($"{ReplayFlag}: {(load != null ? $"not with {LoadFlag} (the recording loads what it loaded)" : rec == null ? $"give a recording's path (there are none in {RecordingsFolder})" : $"{rec} not found")}");
                desktop.Shutdown(1);
                return;
            }
            try { replay = InputRecording.Read(rec); }
            catch (Exception ex) when (ex is FormatException or IOException)
            {
                Log.Error($"{ReplayFlag}: {ex.Message}");
                desktop.Shutdown(1);
                return;
            }
            if (bootAt < 0) boot = replay.Value.Boot;
            Log.Info($"Game: replaying {Path.GetFullPath(rec)} ({replay.Value.Events.Count} inputs, boot param {boot})");
        }
        _ = OpenAsync(desktop, Path.GetFullPath(file), settings, boot, load, record, verbose, replay?.Events);
    }

    private static async Task OpenAsync(IClassicDesktopStyleApplicationLifetime desktop, string file, AppSettings settings, int bootParam, string load, string record, bool verbose,
        System.Collections.Generic.List<InputRecording.Event> replay)
    {
        try
        {
            var files = ScummProject.FindRelatedFiles(file);
            var root = await Task.Run(() => ScummProject.Load(files, null));
            var vm = new GameViewModel(root, files.Count > 0 ? Path.GetFullPath(files[0]) : file, settings);
            vm.Scripted.BootParam = bootParam;
            vm.Scripted.Verbose = verbose;
            if (record != null) StartRecording(desktop, vm.Scripted, record, bootParam, file);
            var window = new GameWindow { DataContext = vm };
            desktop.MainWindow = window;
            window.Show();
            await vm.Loading;
            if (replay != null) vm.Scripted.StartReplay(replay);
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

    /// <summary>Records the inputs from boot on, and prints the mprobe command that replays them when the app quits.</summary>
    private static void StartRecording(IClassicDesktopStyleApplicationLifetime desktop, ScriptedGame game, string path, int bootParam, string gameFile)
    {
        var recording = new InputRecording(path, bootParam, gameFile);
        game.Recorder = recording;
        recording.Recorded += line => { if (game.Verbose) Log.Info($"{game.When} INPUT {line}"); };
        Log.Info($"Game: recording the inputs to {recording.Path}");
        desktop.Exit += (_, _) =>
        {
            if (game.Recorder == recording) recording.End(game.Loop?.Jiffies ?? recording.LastJiffy);
            recording.Dispose();
            Console.Error.WriteLine();
            Console.Error.WriteLine($"Recorded {recording.LastJiffy / 60.0:0.##} s of game time. To replay it headless:");
            Console.Error.WriteLine($"  dotnet run --project tools/mprobe -c Release -- --game \"{gameFile}\" boot --replay \"{recording.Path}\"");
        };
    }
}
