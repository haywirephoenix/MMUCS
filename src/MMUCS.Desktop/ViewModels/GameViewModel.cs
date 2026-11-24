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
/// The game window hosting the player: it passes the window's input and time to the running game
/// (MMUCS.Runtime) and takes a new <see cref="Frame"/> from the player (MMUCS.Player) whenever the game changes,
/// for the canvas to draw. With <see cref="RunScripts"/> (the default) that's the game run by its own scripts
/// (<see cref="ScriptedGame"/>, shown by <see cref="ScriptedView"/>); without, the hand-written stand-ins
/// (<see cref="GameState"/>: a hotspot, the verb coin and the inventory). The font the game's text is drawn in
/// is the app's choice, kept in the settings.
/// </summary>
public sealed partial class GameViewModel : ObservableObject, IDisposable
{
    private MediaEngine _sound;
    private bool _disposed;
    private readonly string _gameFile;
    private GlyphFont[] _gameFonts;
    private bool _wasCaptured, _wasVideo;

    /// <param name="gameFile">The game file opened, whose NUT fonts the game's text is drawn in (none without it).</param>
    /// <param name="settings">Where the font choice is kept (shared with the media player).</param>
    public GameViewModel(ScummBlock root, string gameFile = null, AppSettings settings = null)
    {
        _gameFile = gameFile;
        View = new GameView(Game);
        ScriptedView = new ScriptedView(Scripted);
        Fonts = new FontChooser(settings);
        Fonts.Changed += () => { ApplyFont(); Refresh(); };
        Game.Changed += Refresh;
        Scripted.Changed += Refresh;
        ScriptedView.Changed += Refresh;
        Refresh();
        Loading = Task.WhenAll(Game.LoadAsync(root), LoadFontsAsync(), LoadScriptsAsync(root), StartSoundAsync());
    }

    /// <summary>Opens the sound output on a worker thread (the game's videos play their sound through it).</summary>
    private async Task StartSoundAsync()
    {
        var sound = await Task.Run(() => new MediaEngine());
        if (_disposed)
        {
            sound.Dispose();
            return;
        }
        _sound = sound;
        Scripted.SetAudio(sound.Mixer, sound.Output);
    }

    /// <summary>Whether a script's video is playing (Esc skips it).</summary>
    public bool IsVideoPlaying => RunScripts && Scripted.IsVideoPlaying;

    /// <summary>Ends the playing video; the cutscene it was in is skipped too, as the game does.</summary>
    public void SkipVideo() => Scripted.SkipVideo();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Scripted.Dispose();
        _sound?.Dispose();
        Frame = null;
    }

    /// <summary>The game run by its own scripts.</summary>
    public ScriptedGame Scripted { get; } = new();

    /// <summary>What shows it.</summary>
    public ScriptedView ScriptedView { get; }

    /// <summary>Whether the window plays the game's scripts (on) or the hand-written stand-ins (off). Switching stops the game.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Hint))]
    public partial bool RunScripts { get; set; } = true;

    partial void OnRunScriptsChanged(bool value)
    {
        Game.Stop();
        Scripted.Stop();
        Refresh();
    }

    private async Task LoadScriptsAsync(ScummBlock root)
    {
        if (root == null) return;
        try
        {
            await Task.Run(() => Scripted.Load(root, _gameFile));
            Refresh();
        }
        catch (Exception ex) { Log.Warn($"Game scripts: {ex.Message}"); }
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
        get => RunScripts ? Scripted.IsMouseCaptured : Game.IsMouseCaptured;
        set
        {
            if (RunScripts) Scripted.SetMouseCaptured(value);
            else Game.SetMouseCaptured(value);
        }
    }

    [ObservableProperty] public partial string Status { get; private set; }

    /// <summary>The game cursor's position in game units (where the window puts a stray pointer back).</summary>
    public Point CursorPosition => RunScripts
        ? new(Scripted.CursorPosition.X, Scripted.CursorPosition.Y)
        : new(Game.CursorPosition.X, Game.CursorPosition.Y);

    /// <summary>The line drawn over the canvas: what to do next.</summary>
    public string Hint => !IsPlaying ? (RunScripts ? "Press Play to boot the game." : "Press Play to start.")
        : IsVideoPlaying ? "Esc skips the video."
        : IsMouseCaptured ? null
        : RunScripts ? "Click the canvas to capture the mouse (Ctrl+Alt lets it go). Clicks and keys go to the game's scripts; Esc skips cutscenes."
        : "Click the canvas to capture the mouse (Ctrl+Alt lets it go). I or a right-click opens the inventory.";

    [RelayCommand(CanExecute = nameof(CanPlay))]
    private void Play()
    {
        if (RunScripts) Scripted.Play();
        else Game.Play();
    }
    private bool CanPlay() => !IsPlaying;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        if (RunScripts) Scripted.Stop();
        else Game.Stop();
    }
    private bool CanStop() => IsPlaying;

    /// <summary>Moves the game cursor to <paramref name="position"/> (game units).</summary>
    public void MoveCursor(Point position)
    {
        var at = new GamePoint(position.X, position.Y);
        if (RunScripts) Scripted.MoveCursor(at);
        else Game.MoveCursor(at);
    }

    public void PressCursor()
    {
        if (RunScripts) Scripted.Press(Runtime.Scripts.MouseButton.Left);
        else Game.PressCursor();
    }

    public void ReleaseCursor()
    {
        if (RunScripts) Scripted.Release(Runtime.Scripts.MouseButton.Left);
        else Game.ReleaseCursor();
    }

    /// <summary>The right button went down: the scripts see it as their right click; the stand-ins' is global 33.</summary>
    public void RightClick()
    {
        if (RunScripts) Scripted.Press(Runtime.Scripts.MouseButton.Right);
        else Game.RightClick();
    }

    public void ReleaseRight()
    {
        if (RunScripts) Scripted.Release(Runtime.Scripts.MouseButton.Right);
    }

    public void ToggleInventory() => Game.ToggleInventory();

    /// <summary>
    /// A key, as the engine numbers it (ASCII; F1–F9 are 315–323). The scripts get every key; the stand-ins only
    /// know I. Returns whether it was used.
    /// </summary>
    public bool PressKey(int code)
    {
        if (!IsPlaying || code <= 0) return false;
        if (RunScripts)
        {
            Scripted.PressKey(code);
            return true;
        }
        if (code != 'i') return false;
        Game.ToggleInventory();
        return true;
    }

    /// <summary>One jiffy (1/60 s) of game time.</summary>
    public void Tick()
    {
        if (RunScripts) Scripted.Tick();
        else Game.Tick();
    }

    private void Refresh()
    {
        IsPlaying = RunScripts ? Scripted.IsPlaying : Game.IsPlaying;
        Status = RunScripts ? Scripted.Status : Game.Status;
        Frame = RunScripts ? ScriptedView.Capture() : View.Capture();
        if (IsVideoPlaying != _wasVideo)
        {
            _wasVideo = IsVideoPlaying;
            OnPropertyChanged(nameof(IsVideoPlaying));
            OnPropertyChanged(nameof(Hint));
        }
        bool captured = IsMouseCaptured;
        if (captured == _wasCaptured) return;
        _wasCaptured = captured;
        OnPropertyChanged(nameof(IsMouseCaptured));
        OnPropertyChanged(nameof(Hint));
    }

    /// <summary>A replaced frame lets go of its cutscene picture (the canvas holds its own while it draws).</summary>
    partial void OnFrameChanged(GameFrame oldValue, GameFrame newValue) => oldValue?.Release();

    private void ApplyFont()
    {
        if (_gameFonts == null) return;
        View.Text = Fonts.CreateRenderer(_gameFonts, subtitles: true, out string info);
        ScriptedView.Text = View.Text;
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
