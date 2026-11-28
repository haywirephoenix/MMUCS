using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
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
        // A save's thumbnail: the page as last shown (before the F1 menu draws itself, or at F6).
        Scripted.TakeThumbnail = () => Frame?.PagePng(ScriptedGame.ThumbnailWidth, ScriptedGame.ThumbnailHeight);
        Fonts = new FontChooser(settings);
        _settings = settings;
        Scripted.SmoothCamera = SmoothCamera = settings?.SmoothCamera ?? true;
        Scripted.SmoothWalk = SmoothWalk = settings?.SmoothWalk ?? true;
        Scripted.MusicModsEnabled = MusicModsEnabled = settings?.MusicModsEnabled ?? true;
        Scripted.ImageModsEnabled = ImageModsEnabled = settings?.ImageModsEnabled ?? true;
        Mods = new ModsViewModel(gameFile, settings, this);
        ShowInfo = settings?.ShowInfo ?? true;
        Fonts.Changed += () => { ApplyFont(); Refresh(); };
        Game.Changed += OnChanged;
        Scripted.Changed += OnChanged;
        Scripted.MouseWarped += () => MouseWarped?.Invoke();
        ScriptedView.Changed += OnChanged;
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

    private readonly AppSettings _settings;

    /// <summary>The game's camera eases instead of stepping as SCUMM's does (kept in the settings).</summary>
    [ObservableProperty]
    public partial bool SmoothCamera { get; set; }

    /// <summary>Whether the window shows the status line under its toolbar (kept in the settings).</summary>
    [ObservableProperty]
    public partial bool ShowInfo { get; set; }

    /// <summary>Whether the music mods beside the game play in place of its tracks (kept in the settings).</summary>
    [ObservableProperty]
    public partial bool MusicModsEnabled { get; set; }

    /// <summary>Whether the image mods beside the game are drawn in place of its own pictures (kept in the settings).</summary>
    [ObservableProperty]
    public partial bool ImageModsEnabled { get; set; }

    /// <summary>The Mods view: the mods beside the game and those a repository offers, to install, update and switch.</summary>
    public ModsViewModel Mods { get; }

    /// <summary>Whether the Mods view is shown over the game (the game pauses while it is).</summary>
    [ObservableProperty]
    public partial bool ShowMods { get; set; }

    partial void OnShowModsChanged(bool value)
    {
        if (!value) return;
        if (IsPlaying) Pause();
        Mods.Shown();
    }

    partial void OnImageModsEnabledChanged(bool value)
    {
        Scripted.ImageModsEnabled = value;
        ScriptedView.ReloadMods();
        if (_settings != null && _settings.ImageModsEnabled != value)
        {
            _settings.ImageModsEnabled = value;
            _settings.Save();
        }
        Refresh();
    }

    /// <summary>Reads the mods beside the game again (one was installed, updated or switched), so the game shows them now.</summary>
    public void ReloadMods()
    {
        Scripted.ReloadMods();
        ScriptedView.ReloadMods();
        Refresh();
    }

    /// <summary>Walking actors glide between frames instead of stepping as SCUMM's do, with the smooth camera (kept in the settings).</summary>
    [ObservableProperty]
    public partial bool SmoothWalk { get; set; }

    partial void OnSmoothCameraChanged(bool value)
    {
        Scripted.SmoothCamera = value;
        if (_settings != null && _settings.SmoothCamera != value)
        {
            _settings.SmoothCamera = value;
            _settings.Save();
        }
        Refresh();
    }

    partial void OnSmoothWalkChanged(bool value)
    {
        Scripted.SmoothWalk = value;
        if (_settings != null && _settings.SmoothWalk != value)
        {
            _settings.SmoothWalk = value;
            _settings.Save();
        }
        Refresh();
    }

    partial void OnShowInfoChanged(bool value)
    {
        if (_settings != null && _settings.ShowInfo != value)
        {
            _settings.ShowInfo = value;
            _settings.Save();
        }
    }

    partial void OnMusicModsEnabledChanged(bool value)
    {
        Scripted.MusicModsEnabled = value;
        if (_settings != null && _settings.MusicModsEnabled != value)
        {
            _settings.MusicModsEnabled = value;
            _settings.Save();
        }
        Refresh();
    }

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
    [NotifyCanExecuteChangedFor(nameof(PlayCommand), nameof(PauseCommand), nameof(StopCommand))]
    public partial bool IsPlaying { get; private set; }

    /// <summary>Whether the scripts' game has booted and not been stopped (playing or paused): Stop ends it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Hint))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    public partial bool IsStarted { get; private set; }

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
    public string Hint => !IsPlaying ? (!RunScripts ? "Press Play to start." : IsStarted ? "Paused: Space or Play carries on, Stop ends the game." : "Press Play to boot the game.")
        // : IsVideoPlaying ? "Esc skips the video."
        : IsMouseCaptured ? null
        // : RunScripts ? "Click to capture the mouse (Ctrl+Alt to release)"
        : "Click to capture the mouse (Ctrl+Alt to release).";

    [RelayCommand(CanExecute = nameof(CanPlay))]
    private void Play()
    {
        if (RunScripts) Scripted.Play();
        else Game.Play();
    }
    private bool CanPlay() => !IsPlaying;

    /// <summary>Pauses the game where it is (and lets the mouse go); Play carries on.</summary>
    [RelayCommand(CanExecute = nameof(CanPause))]
    private void Pause()
    {
        if (RunScripts) Scripted.Stop();
        else Game.Stop();
    }
    private bool CanPause() => IsPlaying;

    /// <summary>Whether <paramref name="code"/> pauses the scripts' game now (space, unless the scripts turned it off: under water, the F1 menu).</summary>
    public bool IsPauseKey(int code) => RunScripts && IsPlaying && Scripted.IsPauseKey(code);

    /// <summary>A script moved the mouse (<c>cursor put</c>): the window puts the system pointer on <see cref="CursorPosition"/>.</summary>
    public event Action MouseWarped;

    /// <summary>Ctrl+T: the next voice mode (speech only, speech and subtitles, subtitles only), said in the toast.</summary>
    public void CycleSubtitles()
    {
        if (!RunScripts) return;
        int mode = Scripted.CycleVoiceMode();
        if (mode >= 0) ShowToast(mode switch { 0 => "Speech only", 1 => "Speech and subtitles", _ => "Subtitles only" });
    }

    /// <summary>Ends the game, as its own Quit does: its state is cleared and Play boots it afresh. The stand-ins just stop.</summary>
    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        if (RunScripts) Scripted.Reset();
        else Game.Stop();
    }
    private bool CanStop() => IsPlaying || (RunScripts && IsStarted);

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

    /// <summary>A short note in the canvas's top right (the quick save's and load's), or null; it goes after a moment.</summary>
    [ObservableProperty] public partial string Toast { get; private set; }

    /// <summary>A system message waiting for a key (the F1 menu's "overwrite this game? (Y/N)"), shown in the middle of the canvas, or null.</summary>
    [ObservableProperty] public partial string SystemMessage { get; private set; }

    private DispatcherTimer _toastTimer;

    private void ShowToast(string text)
    {
        Toast = text;
        _toastTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(1.5), DispatcherPriority.Normal, (_, _) =>
        {
            _toastTimer.Stop();
            Toast = null;
        });
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    /// <summary>Loads a save file into the running game (the command line's <c>--load</c>), saying so in the toast.</summary>
    public bool LoadSave(string path)
    {
        bool loaded = Scripted.LoadFile(path);
        ShowToast(loaded ? "Loaded" : "Can't load the save");
        return loaded;
    }

    private const int QuickLoadKey = 318, QuickSaveKey = 320; // F4, F6

    /// <summary>
    /// A key, as the engine numbers it (ASCII; F1–F9 are 315–323). The scripts get every key; the stand-ins only
    /// know I. F6 and F4 are the host's quick save and load, which the scripts don't see. Returns whether it was used.
    /// </summary>
    public bool PressKey(int code)
    {
        if (!IsPlaying || code <= 0) return false;
        if (RunScripts)
        {
            if (code == QuickSaveKey) ShowToast(Scripted.QuickSave() ? "Quick Saved" : "Can't quick save now");
            else if (code == QuickLoadKey) ShowToast(Scripted.QuickLoad() ? "Quick Loaded" : Scripted.HasQuickSave ? "Can't quick load now" : "No quick save");
            else Scripted.PressKey(code);
            return true;
        }
        if (code != 'i') return false;
        Game.ToggleInventory();
        return true;
    }

    /// <summary>A key came up; the scripts ask whether keys are held (the cannon's and the ship's Tab and arrows).</summary>
    public void ReleaseKey(int code)
    {
        if (RunScripts && code > 0) Scripted.ReleaseKey(code);
    }

    /// <summary>Every key lets go (the window lost the keyboard).</summary>
    public void ReleaseKeys() => Scripted.ReleaseKeys();

    /// <summary>One jiffy (1/60 s) of game time.</summary>
    public void Tick()
    {
        if (RunScripts) Scripted.Tick();
        else Game.Tick();
    }

    /// <summary>
    /// Runs <paramref name="jiffies"/> jiffies and shows the picture as it is <paramref name="sinceTick"/> of a
    /// jiffy after the last of them: one picture however many ran, and one between ticks while the smooth camera
    /// or sliding actors move it.
    /// </summary>
    public void Advance(int jiffies, double sinceTick)
    {
        _advancing = true;
        try
        {
            for (int i = 0; i < jiffies; i++) Tick();
        }
        finally
        {
            _advancing = false;
        }
        bool moving = RunScripts && Scripted.Present(sinceTick);
        if (_changed || moving)
        {
            _changed = false;
            Refresh();
        }
    }

    private bool _advancing, _changed;

    /// <summary>The game changed: shown now, or once the jiffies being run are done.</summary>
    private void OnChanged()
    {
        if (_advancing) _changed = true;
        else Refresh();
    }

    private void Refresh()
    {
        IsPlaying = RunScripts ? Scripted.IsPlaying : Game.IsPlaying;
        IsStarted = RunScripts && Scripted.IsStarted;
        Status = RunScripts ? Scripted.Status : Game.Status;
        Frame = RunScripts ? ScriptedView.Capture() : View.Capture();
        SystemMessage = RunScripts ? Scripted.SystemMessage : null;
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
