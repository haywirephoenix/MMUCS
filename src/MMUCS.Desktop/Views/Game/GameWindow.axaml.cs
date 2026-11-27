using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MMUCS.Core.Diagnostics;
using MMUCS.Desktop.Controls;
using MMUCS.Desktop.Services;
using MMUCS.Desktop.ViewModels;

namespace MMUCS.Desktop.Views.Game;

/// <summary>
/// The game window (Window ▸ Game, a testing ground). While playing, a click on the canvas captures the
/// mouse: the system cursor hides and is kept inside the picture, and the game draws its own. Ctrl+Alt,
/// Pause, Stop, or leaving the window lets it go; Esc goes to the game (it skips videos and cutscenes). With Scripts on, buttons and keys go to the game's scripts; with it off,
/// holding the button on a hotspot opens the verb coin until it's let go, and I toggles the inventory. Space pauses
/// and carries on (where the game allows it), Ctrl+T goes through the voice modes, and a script moving the mouse
/// (the cannon's <c>cursor put</c>) moves the system pointer with it.
/// </summary>
public partial class GameWindow : Window
{
    private static readonly Cursor Hidden = new(StandardCursorType.None);
    private readonly PointerConfinement _confinement = new();
    private readonly ReleaseChord _chord = new();
    private readonly DispatcherTimer _timer;
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private long _jiffies;
    private GameViewModel _vm;
    private bool _recaptureOnResume;

    public GameWindow()
    {
        InitializeComponent();
        Canvas.PointerPressed += OnCanvasPressed;
        Canvas.PointerMoved += OnCanvasMoved;
        Canvas.PointerExited += OnCanvasExited;
        Canvas.PointerReleased += OnCanvasReleased;
        // Game time runs in jiffies (1/60 s), counted from a clock so a late timer doesn't slow the game down. The
        // window's animation frames run it and draw, so each picture shows the game at the moment it's drawn; the
        // timer keeps it going when there are none (a hidden window).
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1 / 60.0), DispatcherPriority.Render, (_, _) =>
        {
            if (_clock.Elapsed.TotalSeconds - _lastAnimationFrame > 0.1) Advance();
        });
        Opened += (_, _) => { _clock.Restart(); _timer.Start(); RequestAnimationFrame(OnAnimationFrame); };
        Canvas.SizeChanged += (_, _) => Confine();
        PositionChanged += (_, _) => Confine();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, (_, e) =>
        {
            _chord.Up(e.Key);
            _vm?.ReleaseKey(KeyCode(e.Key, (e.KeyModifiers & KeyModifiers.Shift) != 0));
        }, RoutingStrategies.Tunnel);
        Deactivated += (_, _) =>
        {
            _chord.Reset();
            _vm?.ReleaseKeys();
            ReleaseMouse("the window lost focus");
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.MouseWarped -= OnMouseWarped;
        }
        _vm = DataContext as GameViewModel;
        if (_vm != null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
            _vm.MouseWarped += OnMouseWarped;
        }
    }

    /// <summary>A script put the mouse somewhere (the cannon keeps it in the middle, as it reads how far it moved): the system pointer goes there too.</summary>
    private void OnMouseWarped()
    {
        WarpToCursor();
    }

    private void OnVmPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        // Stop (or anything else) ending the capture puts the system cursor back.
        if (e.PropertyName is nameof(GameViewModel.IsMouseCaptured) or nameof(GameViewModel.IsPlaying)
            && _vm is { IsMouseCaptured: false })
        {
            _confinement.Release();
            Canvas.Cursor = null;
        }
    }

    private void OnCanvasPressed(object sender, PointerPressedEventArgs e)
    {
        if (_vm is not { IsPlaying: true }) return;
        if (_vm.IsMouseCaptured)
        {
            // The left button acts where the cursor is (the verb coin, the inventory); the right one is global 33.
            var button = e.GetCurrentPoint(Canvas).Properties.PointerUpdateKind;
            if (button == PointerUpdateKind.LeftButtonPressed) _vm.PressCursor();
            else if (button == PointerUpdateKind.RightButtonPressed) _vm.RightClick();
            e.Handled = true;
            return;
        }
        // Anywhere on the canvas captures, the bars beside the picture too (the cursor is kept to the picture).
        Capture();
        _vm.MoveCursor(Canvas.ToGame(e.GetPosition(Canvas)));
        e.Handled = true;
    }

    private void Capture()
    {
        Canvas.Focus();
        Canvas.Cursor = Hidden;
        _vm.IsMouseCaptured = true;
        Log.Info($"Game window: mouse captured ({(_vm.IsMouseCaptured ? "the game has it" : "the game didn't take it")})");
        Confine();
        // Under XWayland a warp only takes while the pointer is over the window, and the first one with the cursor
        // hidden makes XWayland lock the pointer (true confinement). Warping right away, before the pointer can
        // stray, starts that lock now rather than at the first warp that lands. Posted so the hidden cursor
        // (on Avalonia's own X connection) is in place first, and the press has moved the game cursor.
        Dispatcher.UIThread.Post(WarpToCursor, DispatcherPriority.Background);
    }

    /// <summary>Puts the system pointer where the game cursor is, while the mouse is captured.</summary>
    private void WarpToCursor()
    {
        if (_vm is { IsMouseCaptured: true }) _ = _confinement.Warp(Canvas.PointToScreen(Canvas.ToControl(_vm.CursorPosition)));
    }

    private void OnCanvasMoved(object sender, PointerEventArgs e)
    {
        if (_vm is not { IsMouseCaptured: true }) return;
        var at = e.GetPosition(Canvas);
        _confinement.KeepInside(Canvas.PointToScreen(at));
        _vm.MoveCursor(Canvas.ToGame(at));
    }

    private double _lastAnimationFrame = double.NegativeInfinity;
    private bool _closed;

    private void OnAnimationFrame(TimeSpan frameTime)
    {
        if (_closed) return;
        _lastAnimationFrame = _clock.Elapsed.TotalSeconds;
        Advance();
        RequestAnimationFrame(OnAnimationFrame);
    }

    /// <summary>
    /// Runs the game for the jiffies that have passed since the last time (a few at most after a stall) and
    /// shows it as it is now, part way into the next jiffy.
    /// </summary>
    private void Advance()
    {
        double now = _clock.Elapsed.TotalSeconds * 60;
        long whole = (long)now;
        int run = 0;
        for (; run < 6 && _jiffies < whole; run++) _jiffies++;
        _jiffies = Math.Max(_jiffies, whole - 6);
        _vm?.Advance(run, _jiffies == whole ? now - whole : 0);
    }

    private void OnCanvasReleased(object sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Left) _vm?.ReleaseCursor();
        else if (e.InitialPressMouseButton == MouseButton.Right) _vm?.ReleaseRight();
    }

    /// <summary>A pointer that got out between two moves goes back to where the game cursor is.</summary>
    private void OnCanvasExited(object sender, PointerEventArgs e)
    {
        WarpToCursor();
    }

    /// <summary>
    /// Ctrl+Alt lets the mouse go (pressed together, in either order). Esc skips a playing video, otherwise it
    /// goes to the scripts like any other key, where it skips the cutscene on (the THX screen, the intro). Ctrl+T
    /// goes through the voice modes; the game's pause key (space) pauses it, keeping no hold on the mouse, and space
    /// carries on, taking the mouse back if the pause let it go.
    /// </summary>
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (ReleaseChord.IsPart(e.Key))
        {
            if (_chord.Down(e.Key)) ReleaseMouse("Ctrl+Alt");
            e.Handled = true;
        }
        else if (e.Key == Key.T && e.KeyModifiers == KeyModifiers.Control && _vm is { IsStarted: true })
        {
            _vm.CycleSubtitles();
            e.Handled = true;
        }
        else if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
        {
            // Avalonia's frame rate and render time graph over the window, to see what drawing a room costs.
            const Avalonia.Rendering.RendererDebugOverlays frames = Avalonia.Rendering.RendererDebugOverlays.Fps | Avalonia.Rendering.RendererDebugOverlays.RenderTimeGraph;
            RendererDiagnostics.DebugOverlays ^= frames;
            Log.Info($"Game window: frame overlay {((RendererDiagnostics.DebugOverlays & frames) != 0 ? "on" : "off")} (Ctrl+F)");
            e.Handled = true;
        }
        else if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.None && _vm != null && _vm.IsPauseKey(' '))
        {
            _recaptureOnResume = _vm.IsMouseCaptured;
            _vm.PauseCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.None && _vm is { IsPlaying: false, IsStarted: true })
        {
            _vm.PlayCommand.Execute(null);
            if (_recaptureOnResume && _vm.IsPlaying)
            {
                Capture();
            }
            _recaptureOnResume = false;
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _vm is { IsVideoPlaying: true })
        {
            _vm.SkipVideo();
            e.Handled = true;
        }
        else if (_vm is { IsPlaying: true } && (e.KeyModifiers & ~KeyModifiers.Shift) == KeyModifiers.None && _vm.PressKey(KeyCode(e.Key, (e.KeyModifiers & KeyModifiers.Shift) != 0)))
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// A key as the engine numbers it: letters (capitals with Shift), digits, space, Enter, Tab, Backspace, Esc,
    /// F1–F9 (315–323), and the arrows and editing keys as the DOS scan code plus 256 (Home 327, Up 328, Page Up 329,
    /// Left 331, Right 333, End 335, Down 336, Page Down 337, Delete 339: the F1 menu's name editing, the ship's
    /// steering); 0 for others.
    /// </summary>
    private static int KeyCode(Key key, bool shift) => key switch
    {
        >= Key.A and <= Key.Z => (shift ? 'A' : 'a') + (key - Key.A),
        >= Key.D0 and <= Key.D9 => '0' + (key - Key.D0),
        >= Key.NumPad0 and <= Key.NumPad9 => '0' + (key - Key.NumPad0),
        >= Key.F1 and <= Key.F9 => 315 + (key - Key.F1),
        Key.Space => ' ',
        Key.Enter => 13,
        Key.Tab => 9,
        Key.Back => 8,
        Key.Escape => 27,
        Key.OemPeriod => '.',
        Key.OemComma => ',',
        Key.OemMinus => shift ? '_' : '-',
        Key.Home => 327,
        Key.Up => 328,
        Key.PageUp => 329,
        Key.Left => 331,
        Key.Right => 333,
        Key.End => 335,
        Key.Down => 336,
        Key.PageDown => 337,
        Key.Delete => 339,
        _ => 0,
    };

    /// <summary>Keeps the system pointer on the picture (again, after the window or canvas moved).</summary>
    private void Confine()
    {
        if (_vm is not { IsMouseCaptured: true }) return;
        var rect = Canvas.PictureRect;
        var topLeft = Canvas.PointToScreen(rect.TopLeft);
        var bottomRight = Canvas.PointToScreen(rect.BottomRight);
        _confinement.Confine(new PixelRect(topLeft, bottomRight));
    }

    private void ReleaseMouse(string why)
    {
        if (_vm is not { IsMouseCaptured: true }) return;
        _vm.IsMouseCaptured = false;
        Log.Info($"Game window: mouse let go ({why})");
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _timer.Stop();
        ReleaseMouse("the window closed");
        _confinement.Dispose();
        _vm?.PropertyChanged -= OnVmPropertyChanged;
        _vm?.MouseWarped -= OnMouseWarped;
        _vm?.Dispose();
        base.OnClosed(e);
    }
}
