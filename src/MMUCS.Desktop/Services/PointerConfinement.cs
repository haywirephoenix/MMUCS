using System;
using System.Runtime.InteropServices;
using Avalonia;

namespace MMUCS.Desktop.Services;

/// <summary>
/// Keeps the system pointer inside a screen rectangle (physical pixels), for the game window's captured
/// mouse. Avalonia has no pointer lock, so this goes to the platform: Windows clips the cursor
/// (ClipCursor), X11 (XWayland included) warps it back with XWarpPointer when it strays. Elsewhere it
/// does nothing and the game cursor is only clamped to the canvas.
/// </summary>
public sealed class PointerConfinement : IDisposable
{
    private PixelRect? _rect;
    private IntPtr _display;

    /// <summary>Whether the pointer is being kept inside a rectangle.</summary>
    public bool IsActive => _rect != null;

    /// <summary>Keeps the pointer inside <paramref name="screenRect"/> (call again when the rectangle moves).</summary>
    public void Confine(PixelRect screenRect)
    {
        if (screenRect.Width <= 0 || screenRect.Height <= 0) return;
        _rect = screenRect;
        if (OperatingSystem.IsWindows())
        {
            var r = new Win32.Rect { Left = screenRect.X, Top = screenRect.Y, Right = screenRect.Right, Bottom = screenRect.Bottom };
            Win32.ClipCursor(ref r);
        }
    }

    /// <summary>Lets the pointer go anywhere again.</summary>
    public void Release()
    {
        if (_rect == null) return;
        _rect = null;
        if (OperatingSystem.IsWindows()) Win32.ClipCursor(IntPtr.Zero);
    }

    /// <summary>
    /// Moves the system pointer to <paramref name="screenPoint"/> where the platform allows it; true when it did.
    /// Used to put a stray pointer back inside the rectangle (Windows' clip never lets it stray).
    /// </summary>
    public bool Warp(PixelPoint screenPoint)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return Win32.SetCursorPos(screenPoint.X, screenPoint.Y);
            if (OperatingSystem.IsLinux())
            {
                if (_display == IntPtr.Zero) _display = X11.XOpenDisplay(IntPtr.Zero);
                if (_display == IntPtr.Zero) return false;
                X11.XWarpPointer(_display, IntPtr.Zero, X11.XDefaultRootWindow(_display), 0, 0, 0, 0, screenPoint.X, screenPoint.Y);
                X11.XFlush(_display);
                return true;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        return false;
    }

    /// <summary>Puts the pointer back inside the rectangle when <paramref name="screenPoint"/> lies outside it.</summary>
    public void KeepInside(PixelPoint screenPoint)
    {
        if (_rect is not { } r || OperatingSystem.IsWindows()) return;
        int x = Math.Clamp(screenPoint.X, r.X, r.Right - 1), y = Math.Clamp(screenPoint.Y, r.Y, r.Bottom - 1);
        if (x != screenPoint.X || y != screenPoint.Y) Warp(new PixelPoint(x, y));
    }

    public void Dispose()
    {
        Release();
        if (_display != IntPtr.Zero)
        {
            try { X11.XCloseDisplay(_display); } catch { /* the library went away */ }
            _display = IntPtr.Zero;
        }
    }

    private static class Win32
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct Rect { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] public static extern bool ClipCursor(ref Rect rect);
        [DllImport("user32.dll")] public static extern bool ClipCursor(IntPtr rect);
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    }

    private static class X11
    {
        private const string Lib = "libX11.so.6";
        [DllImport(Lib)] public static extern IntPtr XOpenDisplay(IntPtr name);
        [DllImport(Lib)] public static extern int XCloseDisplay(IntPtr display);
        [DllImport(Lib)] public static extern IntPtr XDefaultRootWindow(IntPtr display);
        [DllImport(Lib)] public static extern int XFlush(IntPtr display);
        [DllImport(Lib)]
        public static extern int XWarpPointer(IntPtr display, IntPtr srcWindow, IntPtr destWindow,
            int srcX, int srcY, uint srcWidth, uint srcHeight, int destX, int destY);
    }
}
