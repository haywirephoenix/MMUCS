using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using MMUCS.Core;
using MMUCS.Core.Diagnostics;

namespace MMUCS.Desktop.Services;

/// <summary>
/// Keeps one MMUCS running per user. The first instance holds an exclusive lock on app.lock and
/// listens on a named pipe; a later launch sends it its file argument (or nothing), which brings
/// the running window forward and opens the file, then exits.
/// </summary>
internal static class SingleInstance
{
    private static readonly string PipeName = $"MMUCS-{Environment.UserName}";
    private static FileStream _lock;

    /// <summary>
    /// True when this process should run: it got the lock, or the lock couldn't be used at all
    /// (missing or read-only folder), in which case running without it beats not starting.
    /// </summary>
    public static bool TryAcquire()
    {
        try
        {
            Directory.CreateDirectory(CorePaths.Root);
            _lock = new FileStream(Path.Combine(CorePaths.Root, "app.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or UnauthorizedAccessException or PathTooLongException)
        {
            return true;
        }
        catch (IOException)
        {
            return false; // held by the running instance
        }
    }

    public static void Release()
    {
        _lock?.Dispose();
        _lock = null;
    }

    /// <summary>Hands the launch arguments to the running instance; false if it couldn't be reached.</summary>
    public static bool SendToRunning(string[] args)
    {
        try
        {
            string file = args.Length > 0 && File.Exists(args[0]) ? Path.GetFullPath(args[0]) : string.Empty;
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(5000); // the first instance may still be starting up
            using var writer = new StreamWriter(client);
            writer.WriteLine(file);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Listens for later launches; <paramref name="onLaunch"/> gets their file (or "").</summary>
    public static void Listen(Action<string> onLaunch, CancellationToken token = default)
    {
        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(token);
                    using var reader = new StreamReader(server);
                    string file = await reader.ReadLineAsync(token) ?? string.Empty;
                    onLaunch(file.Trim());
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    Log.Warn($"Single-instance listener: {ex.Message}");
                    await Task.Delay(1000, CancellationToken.None);
                }
            }
        }, token);
    }
}
