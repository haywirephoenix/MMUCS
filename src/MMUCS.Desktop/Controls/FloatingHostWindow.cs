using System;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;

namespace MMUCS.Desktop.Controls;

/// <summary>
/// The window a floating panel lives in. Dock's own <see cref="HostWindow"/>, plus:
///
///  - The panel's own header is the title bar (no system title bar): a tool panel's title bar, or a
///    viewer's tab strip, whose tabs get a close button here.
///  - Dragging it doesn't trigger Windows' snapping (Snap Layouts, half/maximize on the screen edges):
///    Dock starts the move with Windows' own move loop, which snaps; the window is moved here instead.
///    Dock still sees every position change, so dropping onto the main window's panels works as before.
/// </summary>
public sealed class FloatingHostWindow : HostWindow
{
    protected override Type StyleKeyOverride => typeof(HostWindow);

    public FloatingHostWindow()
    {
        if (OperatingSystem.IsWindows())
            Win32Properties.AddWndProcHookCallback(this, WndProcHook);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        // A viewer's tab strip stands in for the title bar, as a tool panel's title bar already does.
        // (Dock's theme drops the system title bar for :documentwindow with :documentchromecontrolswindow.)
        bool documents = HostedDock(DataContext as IRootDock) is IDocumentDock;
        PseudoClasses.Set(":documentwindow", documents);
        DocumentChromeControlsWholeWindow = documents;
        if (documents) ((IDocumentDock)HostedDock((IRootDock)DataContext)).CloseButtonShowMode = DocumentCloseButtonShowMode.Always;
    }

    private static IDock HostedDock(IRootDock root) =>
        root?.ActiveDockable as IDock ?? root?.DefaultDockable as IDock ?? root?.VisibleDockables?.OfType<IDock>().FirstOrDefault();

    // ── Moving without Windows' snapping ────────────────────────────────────────

    private const uint WM_SYSCOMMAND = 0x0112, WM_NCLBUTTONDOWN = 0x00A1, WM_LBUTTONUP = 0x0202;
    private const int SC_MOVE = 0xF010, HTCAPTION = 2, VK_LBUTTON = 0x01, VK_ESCAPE = 0x1B;
    private const uint PM_REMOVE = 1, SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
    private const uint QS_ALLINPUT = 0x04FF;

    private bool _moving;

    private IntPtr WndProcHook(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        bool move = msg == WM_SYSCOMMAND && ((int)wParam & 0xFFF0) == SC_MOVE && ((int)wParam & 0xF) != 0 // mouse move, not the keyboard Move command
                    || msg == WM_NCLBUTTONDOWN && (int)wParam == HTCAPTION;
        if (!move || _moving) return IntPtr.Zero;
        handled = true;
        MoveWithMouse(hWnd);
        return IntPtr.Zero;
    }

    /// <summary>Follows the mouse until the button is released (or Esc puts the window back).</summary>
    private void MoveWithMouse(IntPtr hWnd)
    {
        if (!GetCursorPos(out var start) || !GetWindowRect(hWnd, out var origin)) return;
        _moving = true;
        try
        {
            while ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0)
            {
                if ((GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0)
                {
                    SetWindowPos(hWnd, IntPtr.Zero, origin.Left, origin.Top, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                    break;
                }
                if (GetCursorPos(out var p))
                    SetWindowPos(hWnd, IntPtr.Zero, origin.Left + p.X - start.X, origin.Top + p.Y - start.Y, 0, 0,
                        SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

                // Keep the app responsive (rendering, Dock's drop previews) while the button is held.
                MsgWaitForMultipleObjects(0, IntPtr.Zero, false, 8, QS_ALLINPUT);
                while (PeekMessage(out var m, IntPtr.Zero, 0, 0, PM_REMOVE))
                {
                    // The release is Avalonia's to send once the move returns (it ends Dock's drag and drops
                    // the panel); delivered now it would drop while this loop still moves the window.
                    if (m.message == WM_LBUTTONUP && m.hwnd == hWnd) continue;
                    TranslateMessage(ref m);
                    DispatchMessage(ref m);
                }
            }
        }
        finally { _moving = false; }
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern uint MsgWaitForMultipleObjects(uint count, IntPtr handles, bool waitAll, uint ms, uint wakeMask);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out MSG m, IntPtr hWnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG m);
}
