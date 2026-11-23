using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MMUCS.Core.Diagnostics;
using MMUCS.Core.Fonts;
using MMUCS.Core.Scumm;
using MMUCS.Core.Smush;
using MMUCS.Desktop.Rendering;
using MMUCS.Desktop.Services;
using MMUCS.Runtime;

namespace MMUCS.Desktop.ViewModels;

/// <summary>An image the game canvas draws and where it goes, in game units; its bitmap is made when it's first drawn.</summary>
public sealed record CanvasBlast(MMUCS.Core.Imaging.RgbaImage Image, Rect Bounds)
{
    public Bitmap Bitmap => BitmapCache.Of(Image);
}

/// <summary>
/// The game window's view of a running <see cref="GameState"/> (MMUCS.Runtime): it passes the window's input
/// and time to the game, and turns what the game shows into what the canvas binds to (bitmaps, Avalonia
/// geometry, the hover label drawn in the chosen font).
/// </summary>
public sealed partial class GameViewModel : ObservableObject
{
    /// <summary>The canvas and the 640×480 page in it, in game units, for the canvas.</summary>
    public static Size CanvasSize { get; } = new(GameState.CanvasWidth, GameState.CanvasHeight);
    public static Point PageOrigin { get; } = ToPoint(GameState.PageOrigin);

    private readonly string _gameFile;
    private GlyphFont[] _gameFonts;
    private SubtitleRenderer _text;
    private (bool Open, int PageStart, int Count) _blastsKey = (false, -1, -1);

    /// <param name="gameFile">The game file opened, whose NUT fonts the labels are drawn in (none without it).</param>
    /// <param name="settings">Where the font choice is kept (shared with the media player).</param>
    public GameViewModel(ScummBlock root, string gameFile = null, AppSettings settings = null)
    {
        _gameFile = gameFile;
        Fonts = new FontChooser(settings);
        Fonts.Changed += () => { ApplyFont(); UpdateLabel(); };
        Game.Changed += Refresh;
        Refresh();
        Loading = Task.WhenAll(Game.LoadAsync(root), LoadFontsAsync());
    }

    /// <summary>The game and its fonts loading: done when everything the window shows is in.</summary>
    public Task Loading { get; }

    /// <summary>The game being played.</summary>
    public GameState Game { get; } = new();

    /// <summary>The font the labels are drawn in.</summary>
    public FontChooser Fonts { get; }

    /// <summary>What the labels are drawn with, or why the chosen font couldn't be used.</summary>
    [ObservableProperty] public partial string FontInfo { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Hint))]
    [NotifyCanExecuteChangedFor(nameof(PlayCommand), nameof(StopCommand))]
    public partial bool IsPlaying { get; private set; }

    /// <summary>Whether the game has the mouse; the window sets it when it captures or lets go.</summary>
    public bool IsMouseCaptured
    {
        get => Game.IsMouseCaptured;
        set => Game.SetMouseCaptured(value);
    }

    [ObservableProperty] public partial string Status { get; private set; }
    /// <summary>The cursor's image now; <see cref="CursorImage"/> is its bitmap, made when the canvas first draws it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CursorImage))]
    public partial MMUCS.Core.Imaging.RgbaImage CursorFrame { get; private set; }
    public Bitmap CursorImage => BitmapCache.Of(CursorFrame);
    [ObservableProperty] public partial Point CursorHotspot { get; private set; }
    [ObservableProperty] public partial Point CursorPosition { get; private set; }
    [ObservableProperty] public partial bool IsOverHotspot { get; private set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CoinImage))]
    public partial MMUCS.Core.Imaging.RgbaImage CoinFrame { get; private set; }
    public Bitmap CoinImage => BitmapCache.Of(CoinFrame);
    [ObservableProperty] public partial Rect CoinBounds { get; private set; }
    [ObservableProperty] public partial IReadOnlyList<CanvasBlast> Blasts { get; private set; } = [];
    [ObservableProperty] public partial string Label { get; private set; }

    /// <summary><see cref="Label"/> drawn in the chosen font, in page pixels (null when there's none).</summary>
    [ObservableProperty] public partial SubtitleOverlay LabelOverlay { get; private set; }

    /// <summary>The stand-in hotspot, outlined on the canvas.</summary>
    public Rect Hotspot => ToRect(Game.Hotspot);

    /// <summary>The line drawn over the canvas: what to do next.</summary>
    public string Hint => !IsPlaying ? "Press Play to start."
        : IsMouseCaptured ? null
        : "Click the canvas to capture the mouse (Esc lets it go). I or a right-click opens the inventory.";

    [RelayCommand(CanExecute = nameof(CanPlay))]
    private void Play() => Game.Play();
    private bool CanPlay() => !IsPlaying;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => Game.Stop();
    private bool CanStop() => IsPlaying;

    /// <summary>Moves the game cursor to <paramref name="position"/> (game units on the canvas).</summary>
    public void MoveCursor(Point position) => Game.MoveCursor(new GamePoint(position.X, position.Y));
    public void PressCursor() => Game.PressCursor();
    public void ReleaseCursor() => Game.ReleaseCursor();
    public void RightClick() => Game.RightClick();
    public void ToggleInventory() => Game.ToggleInventory();

    /// <summary>One jiffy (1/60 s) of game time.</summary>
    public void Tick() => Game.Tick();

    /// <summary>Reads what the game shows now (the properties only notify when they change).</summary>
    private void Refresh()
    {
        bool captured = IsMouseCaptured;
        IsPlaying = Game.IsPlaying;
        Status = Game.Status;
        CursorFrame = Game.CursorFrame;
        CursorHotspot = ToPoint(Game.CursorHotspot);
        CursorPosition = ToPoint(Game.CursorPosition);
        IsOverHotspot = Game.IsOverHotspot;
        CoinFrame = Game.Coin.Frame;
        CoinBounds = ToRect(Game.Coin.Bounds);
        Label = Game.Label;
        var inv = Game.Inventory;
        var key = (inv.IsOpen && inv.IsLoaded, inv.PageStart, inv.Items.Count);
        if (key != _blastsKey)
        {
            _blastsKey = key;
            Blasts = inv.Blasts.Select(b => new CanvasBlast(b.Image, ToRect(b.Bounds))).ToList();
        }
        if (captured != _wasCaptured)
        {
            _wasCaptured = captured;
            OnPropertyChanged(nameof(IsMouseCaptured));
            OnPropertyChanged(nameof(Hint));
        }
    }

    private bool _wasCaptured;

    partial void OnLabelChanged(string value) => UpdateLabel();

    private void UpdateLabel()
    {
        // Overlays aren't disposed here: the canvas may still be drawing the last one on the render thread.
        LabelOverlay = string.IsNullOrEmpty(Label) || _text == null ? null : _text.BuildText(new SmushText
        {
            X = (int)GameState.LabelAt.X, Y = (int)GameState.LabelAt.Y, Flags = SmushTextFlags.Center | SmushTextFlags.Wrap,
            Font = GameState.LabelCharset, Color = GameState.LabelColor, Text = Label,
        }, Game.LabelPalette, (int)GameState.PageSize.Width, (int)GameState.PageSize.Height);
    }

    private void ApplyFont()
    {
        if (_gameFonts == null) return;
        _text = Fonts.CreateRenderer(_gameFonts, subtitles: true, out string info);
        FontInfo = info;
    }

    private async Task LoadFontsAsync()
    {
        if (_gameFile == null) return;
        try
        {
            _gameFonts = await Task.Run(() => NutFont.LoadAll(_gameFile));
            ApplyFont();
            UpdateLabel();
        }
        catch (Exception ex) { Log.Warn($"Game window fonts: {ex.Message}"); }
    }

    private static Point ToPoint(GamePoint p) => new(p.X, p.Y);
    private static Rect ToRect(GameRect r) => new(r.X, r.Y, r.Width, r.Height);
}
