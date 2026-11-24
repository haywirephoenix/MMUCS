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
/// holding the button on a hotspot opens the verb coin until it's let go, and I toggles the inventory.
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

    public GameWindow()
    {
        InitializeComponent();
        Canvas.PointerPressed += OnCanvasPressed;
        Canvas.PointerMoved += OnCanvasMoved;
        Canvas.PointerExited += OnCanvasExited;
        Canvas.PointerReleased += OnCanvasReleased;
        // Game time runs in jiffies (1/60 s), counted from a clock so a late timer doesn't slow the game down.
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1 / 60.0), DispatcherPriority.Render, (_, _) => Advance());
        Opened += (_, _) => { _clock.Restart(); _timer.Start(); };
        Canvas.SizeChanged += (_, _) => Confine();
        PositionChanged += (_, _) => Confine();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, (_, e) => _chord.Up(e.Key), RoutingStrategies.Tunnel);
        Deactivated += (_, _) =>
        {
            _chord.Reset();
            ReleaseMouse("the window lost focus");
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _vm?.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as GameViewModel;
        _vm?.PropertyChanged += OnVmPropertyChanged;
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
        var at = e.GetPosition(Canvas);
        Canvas.Focus();
        Canvas.Cursor = Hidden;
        _vm.IsMouseCaptured = true;
        Log.Info($"Game window: mouse captured ({(_vm.IsMouseCaptured ? "the game has it" : "the game didn't take it")})");
        Confine();
        _vm.MoveCursor(Canvas.ToGame(at));
        e.Handled = true;
    }

    private void OnCanvasMoved(object sender, PointerEventArgs e)
    {
        if (_vm is not { IsMouseCaptured: true }) return;
        var at = e.GetPosition(Canvas);
        _confinement.KeepInside(Canvas.PointToScreen(at));
        _vm.MoveCursor(Canvas.ToGame(at));
    }

    /// <summary>Runs the game for the jiffies that have passed since the last time (a few at most after a stall).</summary>
    private void Advance()
    {
        long now = _clock.ElapsedTicks * 60 / System.Diagnostics.Stopwatch.Frequency;
        for (int i = 0; i < 6 && _jiffies < now; i++, _jiffies++) _vm?.Tick();
        _jiffies = Math.Max(_jiffies, now - 6);
    }

    private void OnCanvasReleased(object sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Left) _vm?.ReleaseCursor();
        else if (e.InitialPressMouseButton == MouseButton.Right) _vm?.ReleaseRight();
    }

    /// <summary>A pointer that got out between two moves goes back to where the game cursor is.</summary>
    private void OnCanvasExited(object sender, PointerEventArgs e)
    {
        if (_vm is { IsMouseCaptured: true }) _confinement.Warp(Canvas.PointToScreen(Canvas.ToControl(_vm.CursorPosition)));
    }

    /// <summary>
    /// Ctrl+Alt lets the mouse go (pressed together, in either order). Esc skips a playing video, otherwise it
    /// goes to the scripts like any other key, where it skips the cutscene on (the THX screen, the intro).
    /// </summary>
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (ReleaseChord.IsPart(e.Key))
        {
            if (_chord.Down(e.Key)) ReleaseMouse("Ctrl+Alt");
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _vm is { IsVideoPlaying: true })
        {
            _vm.SkipVideo();
            e.Handled = true;
        }
        else if (_vm is { IsPlaying: true } && (e.KeyModifiers & ~KeyModifiers.Shift) == KeyModifiers.None && _vm.PressKey(KeyCode(e.Key)))
        {
            e.Handled = true;
        }
    }

    /// <summary>A key as the engine numbers it: letters (lower case), digits, space, Enter, Tab, Backspace, Esc, F1–F9 (315–323); 0 for others.</summary>
    private static int KeyCode(Key key) => key switch
    {
        >= Key.A and <= Key.Z => 'a' + (key - Key.A),
        >= Key.D0 and <= Key.D9 => '0' + (key - Key.D0),
        >= Key.NumPad0 and <= Key.NumPad9 => '0' + (key - Key.NumPad0),
        >= Key.F1 and <= Key.F9 => 315 + (key - Key.F1),
        Key.Space => ' ',
        Key.Enter => 13,
        Key.Tab => 9,
        Key.Back => 8,
        Key.Escape => 27,
        Key.OemPeriod => '.',
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
        _timer.Stop();
        ReleaseMouse("the window closed");
        _confinement.Dispose();
        _vm?.PropertyChanged -= OnVmPropertyChanged;
        _vm?.Dispose();
        base.OnClosed(e);
    }
}
