using System;
using System.Threading.Tasks;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MMUCS.Core.Diagnostics;
using MMUCS.Core.Fonts;
using MMUCS.Core.Scumm;
using MMUCS.Desktop.Services;
using MMUCS.Player;
using MMUCS.Runtime;

namespace MMUCS.Desktop.ViewModels;

/// <summary>
/// The game window hosting the player: it passes the window's input and time to the running
/// <see cref="GameState"/> (MMUCS.Runtime) and takes a new <see cref="Frame"/> from the <see cref="GameView"/>
/// (MMUCS.Player) whenever the game changes, for the canvas to draw. The font the game's text is drawn in is
/// the app's choice, kept in the settings.
/// </summary>
public sealed partial class GameViewModel : ObservableObject
{
    private readonly string _gameFile;
    private GlyphFont[] _gameFonts;
    private bool _wasCaptured;

    /// <param name="gameFile">The game file opened, whose NUT fonts the game's text is drawn in (none without it).</param>
    /// <param name="settings">Where the font choice is kept (shared with the media player).</param>
    public GameViewModel(ScummBlock root, string gameFile = null, AppSettings settings = null)
    {
        _gameFile = gameFile;
        View = new GameView(Game);
        Fonts = new FontChooser(settings);
        Fonts.Changed += () => { ApplyFont(); Refresh(); };
        Game.Changed += Refresh;
        Refresh();
        Loading = Task.WhenAll(Game.LoadAsync(root), LoadFontsAsync());
    }

    /// <summary>The game being played.</summary>
    public GameState Game { get; } = new();

    /// <summary>What shows it.</summary>
    public GameView View { get; }

    /// <summary>The game and its fonts loading: done when everything the window shows is in.</summary>
    public Task Loading { get; }

    /// <summary>The font the game's text is drawn in.</summary>
    public FontChooser Fonts { get; }

    /// <summary>What the game's text is drawn with, or why the chosen font couldn't be used.</summary>
    [ObservableProperty] public partial string FontInfo { get; set; }

    /// <summary>The game as it looks now, for the canvas.</summary>
    [ObservableProperty] public partial GameFrame Frame { get; private set; }

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

    /// <summary>The game cursor's position in game units (where the window puts a stray pointer back).</summary>
    public Point CursorPosition => new(Game.CursorPosition.X, Game.CursorPosition.Y);

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

    /// <summary>Moves the game cursor to <paramref name="position"/> (game units).</summary>
    public void MoveCursor(Point position) => Game.MoveCursor(new GamePoint(position.X, position.Y));
    public void PressCursor() => Game.PressCursor();
    public void ReleaseCursor() => Game.ReleaseCursor();
    public void RightClick() => Game.RightClick();
    public void ToggleInventory() => Game.ToggleInventory();

    /// <summary>One jiffy (1/60 s) of game time.</summary>
    public void Tick() => Game.Tick();

    private void Refresh()
    {
        IsPlaying = Game.IsPlaying;
        Status = Game.Status;
        Frame = View.Capture();
        bool captured = Game.IsMouseCaptured;
        if (captured == _wasCaptured) return;
        _wasCaptured = captured;
        OnPropertyChanged(nameof(IsMouseCaptured));
        OnPropertyChanged(nameof(Hint));
    }

    private void ApplyFont()
    {
        if (_gameFonts == null) return;
        View.Text = Fonts.CreateRenderer(_gameFonts, subtitles: true, out string info);
        FontInfo = info;
    }

    private async Task LoadFontsAsync()
    {
        if (_gameFile == null) return;
        try
        {
            _gameFonts = await Task.Run(() => NutFont.LoadAll(_gameFile));
            ApplyFont();
            Refresh();
        }
        catch (Exception ex) { Log.Warn($"Game window fonts: {ex.Message}"); }
    }
}
