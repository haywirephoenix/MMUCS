using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MMUCS.Desktop.Services;
using MMUCS.Desktop.ViewModels;

namespace MMUCS.Desktop.Views.Game;

/// <summary>
/// The game window (Window ▸ Game, a testing ground). While playing, a click on the canvas captures the
/// mouse: the system cursor hides and is kept inside the picture, and the game draws its own. Esc, Stop,
/// or leaving the window lets it go.
/// </summary>
public partial class GameWindow : Window
{
    private static readonly Cursor Hidden = new(StandardCursorType.None);
    private readonly PointerConfinement _confinement = new();
    private GameViewModel _vm;

    public GameWindow()
    {
        InitializeComponent();
        Canvas.PointerPressed += OnCanvasPressed;
        Canvas.PointerMoved += OnCanvasMoved;
        Canvas.PointerExited += OnCanvasExited;
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
        if (_vm is not { IsPlaying: true } || _vm.IsMouseCaptured) return;
        var at = e.GetPosition(Canvas);
        if (!Canvas.GameRect.Contains(at)) return; // the letterbox isn't the game
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
    }

    /// <summary>Keeps the system pointer on the picture (again, after the window or canvas moved).</summary>
    private void Confine()
    {
        if (_vm is not { IsMouseCaptured: true }) return;
        var rect = Canvas.GameRect;
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
        ReleaseMouse();
        _confinement.Dispose();
        _vm?.PropertyChanged -= OnVmPropertyChanged;
        base.OnClosed(e);
    }
}
