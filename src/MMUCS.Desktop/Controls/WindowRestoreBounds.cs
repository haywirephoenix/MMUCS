using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace MMUCS.Desktop.Controls;

/// <summary>
/// Adjusts the size a maximized window will restore to, without un-maximizing it. On Windows that
/// size lives in the window placement (rcNormalPosition), which also drives drag-to-restore.
/// </summary>
internal static class WindowRestoreBounds
{
    /// <summary>
    /// Grows or shrinks the stored restore rectangle by <paramref name="clientDelta"/> DIPs of height.
    /// Returns false where unsupported (non-Windows, no native handle).
    /// </summary>
    public static bool TryAdjustRestoreHeight(Window window, double clientDelta)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero) return false;

        var placement = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(handle, ref placement)) return false;

        // The placement is in physical pixels; keep left/top/width and extend the bottom edge.
        int delta = (int)Math.Round(clientDelta * window.RenderScaling);
        placement.rcNormalPosition.Bottom = Math.Max(placement.rcNormalPosition.Top + 1, placement.rcNormalPosition.Bottom + delta);
        return SetWindowPlacement(handle, ref placement);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);
}
