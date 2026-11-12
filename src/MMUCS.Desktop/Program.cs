using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using MMUCS.Core;
using MMUCS.Desktop.Services;

namespace MMUCS.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrash(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException      += (_, e) => { WriteCrash(e.Exception); e.SetObserved(); };
        // One instance per user: a second launch hands its file to the running one and exits.
        if (!SingleInstance.TryAcquire())
        {
            SingleInstance.SendToRunning(args);
            return;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            WriteCrash(ex);
            throw;
        }
        finally
        {
            SingleInstance.Release();
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    /// <summary>Appends crash details to %LocalAppData%/MMUCS/crash.log.</summary>
    private static void WriteCrash(Exception ex)
    {
        if (ex == null) return;
        try
        {
            Directory.CreateDirectory(CorePaths.Root);
            File.AppendAllText(Path.Combine(CorePaths.Root, "crash.log"), $"[{DateTime.Now:O}]\n{ex}\n\n");
        }
        catch { /* never throw from the crash handler */ }
    }
}
