using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MMUCS.Desktop.Services;
using MMUCS.Desktop.ViewModels;

namespace MMUCS.Desktop.Views.Game;

/// <summary>
/// The game window (Window ▸ Game, a testing ground). While playing, a click on the canvas captures the
/// mouse: the system cursor hides and is kept inside the picture, and the game draws its own. Esc, Stop,
/// or leaving the window lets it go. Holding the button on a hotspot opens the verb coin until it's let go; I toggles the inventory.
/// </summary>
public partial class GameWindow : Window
{
    private static readonly Cursor Hidden = new(StandardCursorType.None);
    private readonly PointerConfinement _confinement = new();
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
        Deactivated += (_, _) => ReleaseMouse();
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
        var at = e.GetPosition(Canvas);
        if (!Canvas.PictureRect.Contains(at)) return; // the letterbox isn't the game
        Canvas.Focus();
        Canvas.Cursor = Hidden;
        _vm.IsMouseCaptured = true;
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
    }

    /// <summary>A pointer that got out between two moves goes back to where the game cursor is.</summary>
    private void OnCanvasExited(object sender, PointerEventArgs e)
    {
        if (_vm is { IsMouseCaptured: true }) _confinement.Warp(Canvas.PointToScreen(Canvas.ToControl(_vm.CursorPosition)));
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _vm is { IsMouseCaptured: true })
        {
            ReleaseMouse();
            e.Handled = true;
        }
        else if (e.Key == Key.I && e.KeyModifiers == KeyModifiers.None && _vm is { IsPlaying: true })
        {
            _vm.ToggleInventory();
            e.Handled = true;
        }
    }

    /// <summary>Keeps the system pointer on the picture (again, after the window or canvas moved).</summary>
    private void Confine()
    {
        if (_vm is not { IsMouseCaptured: true }) return;
        var rect = Canvas.PictureRect;
        var topLeft = Canvas.PointToScreen(rect.TopLeft);
        var bottomRight = Canvas.PointToScreen(rect.BottomRight);
        _confinement.Confine(new PixelRect(topLeft, bottomRight));
    }

    private void ReleaseMouse()
    {
        if (_vm != null) _vm.IsMouseCaptured = false;
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        ReleaseMouse();
        _confinement.Dispose();
        _vm?.PropertyChanged -= OnVmPropertyChanged;
        base.OnClosed(e);
    }
}
