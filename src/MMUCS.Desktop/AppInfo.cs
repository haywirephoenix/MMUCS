using System.Reflection;

namespace MMUCS.Desktop;

public static class AppInfo
{
    /// <summary>Version from Directory.Build.props (any "+commit" build metadata is stripped).</summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var info = typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(info)) return typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        int plus = info.IndexOf('+');
        return plus >= 0 ? info[..plus] : info;
    }
}
