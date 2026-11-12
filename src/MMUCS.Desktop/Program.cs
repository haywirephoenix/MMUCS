using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using MMUCS.Core;

namespace MMUCS.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrash(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException      += (_, e) => { WriteCrash(e.Exception); e.SetObserved(); };
        try
        {
            Directory.CreateDirectory(CorePaths.Root);
        }
        catch { /* ignore directory creation failures here */ }

        string lockFilePath = Path.Combine(CorePaths.Root, "app.lock");
        FileStream lockFile = null;

        try
        {
            lockFile = new FileStream(lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
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
            lockFile?.Dispose();
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
