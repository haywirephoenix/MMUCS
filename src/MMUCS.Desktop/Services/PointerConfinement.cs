using System;
using System.Runtime.InteropServices;
using Avalonia;

namespace MMUCS.Desktop.Services;

/// <summary>
/// Keeps the system pointer inside a screen rectangle (Avalonia's screen coordinates), for the game window's
/// captured mouse. Avalonia has no pointer lock, so this goes to the platform: Windows clips the cursor
/// (ClipCursor); X11 (XWayland included) and macOS warp it back when it strays (XWarpPointer,
/// CGWarpMouseCursorPosition). Elsewhere, or when the library isn't there, it does nothing and the game
/// cursor is only clamped to the canvas.
/// </summary>
public sealed partial class PointerConfinement : IDisposable
{
    private PixelRect? _rect;
    private IntPtr _display;
    private bool _cannotWarp;

    /// <summary>Keeps the pointer inside <paramref name="screenRect"/> (call again when the rectangle moves).</summary>
    public void Confine(PixelRect screenRect)
    {
        if (screenRect.Width <= 0 || screenRect.Height <= 0) return;
        _rect = screenRect;
        if (OperatingSystem.IsWindows())
        {
            var r = new Win32.Rect { Left = screenRect.X, Top = screenRect.Y, Right = screenRect.Right, Bottom = screenRect.Bottom };
            _ = Win32.ClipCursor(ref r);
        }
    }

    /// <summary>Lets the pointer go anywhere again.</summary>
    public void Release()
    {
        if (_rect == null) return;
        _rect = null;
        if (OperatingSystem.IsWindows()) _ = Win32.ClipCursor(IntPtr.Zero);
    }

    /// <summary>
    /// Moves the system pointer to <paramref name="screenPoint"/> where the platform allows it; true when it did.
    /// Used to put a stray pointer back inside the rectangle (Windows' clip never lets it stray). A platform that
    /// can't warp (no libX11, no X display) is tried once, not on every move.
    /// </summary>
    public bool Warp(PixelPoint screenPoint)
    {
        if (_cannotWarp) return false;
        try
        {
            if (OperatingSystem.IsWindows()) return Win32.SetCursorPos(screenPoint.X, screenPoint.Y);
            if (OperatingSystem.IsMacOS())
            {
                // Avalonia's screen coordinates on macOS are points from the main display's top-left, as
                // CoreGraphics' global ones are. A warp holds back mouse events for a moment (the pointer feels
                // stuck at the edge); re-associating the mouse with the cursor lifts that straight away.
                if (CoreGraphics.CGWarpMouseCursorPosition(new CoreGraphics.CGPoint(screenPoint.X, screenPoint.Y)) != 0) return false;
                _ = CoreGraphics.CGAssociateMouseAndMouseCursorPosition(1);
                return true;
            }
            if (OperatingSystem.IsLinux())
            {
                if (_display == IntPtr.Zero) _display = X11.XOpenDisplay(IntPtr.Zero);
                if (_display == IntPtr.Zero)
                {
                    _cannotWarp = true;   // Wayland without XWayland, or no display at all
                    return false;
                }
                _ = X11.XWarpPointer(_display, IntPtr.Zero, X11.XDefaultRootWindow(_display), 0, 0, 0, 0, screenPoint.X, screenPoint.Y);
                _ = X11.XFlush(_display);
                return true;
            }
            _cannotWarp = true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _cannotWarp = true;
        }
        return false;
    }

    /// <summary>Puts the pointer back inside the rectangle when <paramref name="screenPoint"/> lies outside it.</summary>
    public void KeepInside(PixelPoint screenPoint)
    {
        if (_rect is not { } r || OperatingSystem.IsWindows()) return;
        int x = Math.Clamp(screenPoint.X, r.X, r.Right - 1), y = Math.Clamp(screenPoint.Y, r.Y, r.Bottom - 1);
        if (x != screenPoint.X || y != screenPoint.Y) _ = Warp(new PixelPoint(x, y));
    }

    public void Dispose()
    {
        Release();
        if (_display != IntPtr.Zero)
        {
            try { _ = X11.XCloseDisplay(_display); } catch { /* the library went away */ }
            _display = IntPtr.Zero;
        }
    }

    private static partial class Win32
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct Rect { public int Left, Top, Right, Bottom; }

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool ClipCursor(ref Rect rect);
        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool ClipCursor(IntPtr rect);
        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool SetCursorPos(int x, int y);
    }

    private static partial class X11
    {
        private const string Lib = "libX11.so.6";
        [LibraryImport(Lib)] public static partial IntPtr XOpenDisplay(IntPtr name);
        [LibraryImport(Lib)] public static partial int XCloseDisplay(IntPtr display);
        [LibraryImport(Lib)] public static partial IntPtr XDefaultRootWindow(IntPtr display);
        [LibraryImport(Lib)] public static partial int XFlush(IntPtr display);
        [LibraryImport(Lib)] public static partial int XWarpPointer(IntPtr display, IntPtr srcWindow, IntPtr destWindow,
                                int srcX, int srcY, uint srcWidth, uint srcHeight, int destX, int destY);
    }

    private static partial class CoreGraphics
    {
        private const string Lib = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

        /// <summary>CGPoint: two CGFloats, which are doubles on 64-bit macOS.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public readonly struct CGPoint(double x, double y) { public readonly double X = x, Y = y; }

        /// <summary>Returns a CGError, 0 when it worked.</summary>
        [LibraryImport(Lib)] public static partial int CGWarpMouseCursorPosition(CGPoint point);
        /// <summary>Takes a boolean_t (a 32-bit int on macOS).</summary>
        [LibraryImport(Lib)] public static partial int CGAssociateMouseAndMouseCursorPosition(int connected);
    }
}
