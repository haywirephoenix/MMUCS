using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace MMUCS.Desktop.Services;

/// <summary>
/// Registers MMUCS with the Linux desktop so the dock, taskbar and Alt-Tab show its icon. GNOME (and
/// other shells) take an app's icon from a .desktop entry whose StartupWMClass matches the window's
/// X11 class, not from the icon the window sets, so a build run straight from a folder shows a
/// generic icon until one exists. Writes ~/.local/share/applications/mmucs.desktop and a hicolor PNG,
/// pointing Exec at whichever build was launched last; files are only rewritten when they change.
/// </summary>
public static class LinuxDesktopEntry
{
    /// <summary>The X11 WM_CLASS the app's windows use; the .desktop entry matches on it.</summary>
    public const string WmClass = "MMUCS";

    private const string IconName = "mmucs";

    public static void Register()
    {
        if (!OperatingSystem.IsLinux()) return;
        Task.Run(() =>
        {
            try { Write(); }
            catch { /* desktop integration is cosmetic; never fail startup over it */ }
        });
    }

    private static void Write()
    {
        string exec = ExecCommand();
        if (exec == null) return;

        string data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrEmpty(data))
            data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

        string iconDir = Path.Combine(data, "icons", "hicolor", "256x256", "apps");
        string iconPath = Path.Combine(iconDir, IconName + ".png");
        if (!File.Exists(iconPath))
        {
            Directory.CreateDirectory(iconDir);
            // Skia's ICO decoder picks the largest (256 px) image in the .ico.
            using var bitmap = new Bitmap(AssetLoader.Open(new Uri("avares://MMUCS/Assets/mmucs.ico")));
            string tmp = iconPath + ".tmp";
            bitmap.Save(tmp, new PngBitmapEncoderOptions());
            File.Move(tmp, iconPath, overwrite: true);
        }

        string entry = new StringBuilder()
            .Append("[Desktop Entry]\n")
            .Append("Type=Application\n")
            .Append("Name=MMUCS\n")
            .Append("Comment=Modular Media Unpacker Content Studio\n")
            .Append("Exec=").Append(exec).Append(" %f\n")
            // An absolute path, not the theme name: a stale ~/.local/share/icons/hicolor/icon-theme.cache
            // (written by another app's installer) hides icons it doesn't list from GNOME Shell.
            .Append("Icon=").Append(iconPath.Replace("\\", "\\\\")).Append('\n')
            .Append("StartupWMClass=").Append(WmClass).Append('\n')
            .Append("Terminal=false\n")
            .Append("Categories=Graphics;Viewer;\n")
            .ToString();

        string appsDir = Path.Combine(data, "applications");
        string entryPath = Path.Combine(appsDir, "mmucs.desktop");
        if (File.Exists(entryPath) && File.ReadAllText(entryPath) == entry) return;
        Directory.CreateDirectory(appsDir);
        File.WriteAllText(entryPath + ".tmp", entry);
        File.Move(entryPath + ".tmp", entryPath, overwrite: true);
    }

    /// <summary>The command that relaunches this build: the apphost, or "dotnet MMUCS.dll" when run through dotnet.</summary>
    private static string ExecCommand()
    {
        string process = Environment.ProcessPath;
        if (string.IsNullOrEmpty(process)) return null;
        if (!Path.GetFileNameWithoutExtension(process).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return Quote(process);
        string dll = typeof(LinuxDesktopEntry).Assembly.Location;
        return string.IsNullOrEmpty(dll) ? null : Quote(process) + " " + Quote(dll);
    }

    /// <summary>Quotes an Exec argument per the Desktop Entry spec (then escapes backslashes for the string value).</summary>
    private static string Quote(string arg)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in arg)
        {
            if (c is '"' or '`' or '$' or '\\') sb.Append('\\');
            sb.Append(c);
        }
        return sb.Append('"').ToString().Replace("\\", "\\\\");
    }
}
