using System;

namespace MMUCS.Core.Diagnostics;

public enum LogLevel { Info, Warning, Error }

/// <summary>
/// Tiny logging seam replacing GD.Print / GD.PrintErr / StatusBar.SetStatus.
/// The UI subscribes to <see cref="Message"/> to surface status text.
/// </summary>
public static class Log
{
    public static event Action<LogLevel, string> Message;

    public static void Info(string msg) => Emit(LogLevel.Info, msg);
    public static void Warn(string msg) => Emit(LogLevel.Warning, msg);
    public static void Error(string msg) => Emit(LogLevel.Error, msg);

    private static void Emit(LogLevel level, string msg)
    {
        System.Diagnostics.Debug.WriteLine($"[{level}] {msg}");
        Message?.Invoke(level, msg);
    }
}
