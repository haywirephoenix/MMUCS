using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MMUCS.Core.Audio;
using MMUCS.Core.Diagnostics;
using MMUCS.Core.Media;
using MMUCS.Desktop.Services;
using SkiaSharp;

namespace MMUCS.Desktop.ViewModels;

/// <summary>
/// The media player: browse the game's cutscenes, music and speech, and play them through MMUCS's own
/// mixer and output (the same pieces the game runner will use).
/// <list type="bullet">
/// <item>Music and speech play through <see cref="ImusPlayer"/>, so loops, hooked jumps and markers behave
/// as in the game; the timeline shows the map and the hook controls steer it.</item>
/// <item>Cutscenes play through <see cref="SmushPlayer"/> against a <see cref="MediaClock"/> on the output's
/// played clock; frames are decoded on a background task so seeking never blocks the window.</item>
/// </list>
/// The view calls <see cref="Tick"/> about 60 times a second.
/// </summary>
public sealed partial class MediaPlayerViewModel : ObservableObject, IDisposable
{
    public sealed record Category(MediaKind Kind, string Title)
    {
        public override string ToString() => Title;
    }

    private const int MaxEvents = 300;

    private readonly string _gameFile;
    private readonly MediaEngine _engine;
    private readonly MediaClock _clock;
    private IReadOnlyList<MediaItem> _all = Array.Empty<MediaItem>();
    private int _loadGeneration;

    // Sound
    private ImusPlayer _player;
    private MixerVoice _voice;
    private MixGroup _group;
    private readonly ConcurrentQueue<ImusEvent> _pendingEvents = new();

    // Picture
    private SmushPlayer _video;
    private Task _decoding = Task.CompletedTask;
    private int _shownFrame = -1;
    private int _wantedFrame = -1;
    private byte[] _rgba;

    private bool _updatingPosition;

    [ObservableProperty] private Category _selectedCategory;
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private MediaItem _selectedItem;
    [ObservableProperty] private string _title = "Nothing loaded";
    [ObservableProperty] private string _status;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsEmpty))] private bool _isVideo;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsEmpty))] private bool _isAudio;
    [ObservableProperty] private double _position;
    [ObservableProperty] private double _duration = 1;
    [ObservableProperty] private string _positionText = "0:00.0 / 0:00.0";
    [ObservableProperty] private double _volume = 0.8;
    [ObservableProperty] private SKImage _frame;
    /// <summary>The changing frame readout, drawn over the picture so it never resizes the canvas.</summary>
    [ObservableProperty] private string _frameText;
    /// <summary>The cutscene's fixed details, for the viewer's toolbar.</summary>
    [ObservableProperty] private string _videoInfo;
    [ObservableProperty] private bool _showFrameInfo = true;
    [ObservableProperty] private ImusSound _sound;
    [ObservableProperty] private long _soundFrame;
    [ObservableProperty] private int _hook;
    [ObservableProperty] private string _hookText = "Track hook: 0";

    public IReadOnlyList<Category> Categories { get; } = new[]
    {
        new Category(MediaKind.Cutscene, "Cutscenes"),
        new Category(MediaKind.Music, "Music"),
        new Category(MediaKind.Voice, "Speech"),
    };

    public ObservableCollection<MediaItem> Items { get; } = new();
    public ObservableCollection<string> Events { get; } = new();

    public string OutputName => _engine.Output.Name;
    public bool IsEmpty => !IsVideo && !IsAudio;

    public static readonly Avalonia.Data.Converters.IValueConverter PlayPauseLabel =
        new Avalonia.Data.Converters.FuncValueConverter<bool, string>(playing => playing ? "Pause" : "Play");
    public bool HasGame => _gameFile != null;

    public MediaPlayerViewModel(string gameFile)
    {
        _gameFile = gameFile;
        _engine = new MediaEngine();
        _engine.Mixer.MasterVolume = (float)Volume;
        _clock = new MediaClock(_engine.Output);
        _selectedCategory = Categories[1];
        Status = gameFile == null ? "Open the game (COMI.LA0) first: the media player lists its cutscenes and bundles." : "Scanning…";
        if (gameFile != null) _ = ScanAsync();
    }

    private async Task ScanAsync()
    {
        IsLoading = true;
        try
        {
            _all = await Task.Run(() => MediaLibrary.Scan(_gameFile));
            Status = $"{_all.Count(i => i.Kind == MediaKind.Cutscene)} cutscenes · {_all.Count(i => i.Kind == MediaKind.Music)} music tracks · " +
                     $"{_all.Count(i => i.Kind == MediaKind.Voice):N0} speech lines · {OutputName}";
            ApplyFilter();
        }
        catch (Exception ex)
        {
            Status = $"Couldn't read the game's media: {ex.Message}";
        }
        finally { IsLoading = false; }
    }

    partial void OnSelectedCategoryChanged(Category value) => ApplyFilter();
    partial void OnFilterChanged(string value) => ApplyFilter();
    partial void OnVolumeChanged(double value) => _engine.Mixer.MasterVolume = (float)Math.Clamp(value, 0, 1);

    private void ApplyFilter()
    {
        var kind = SelectedCategory?.Kind ?? MediaKind.Music;
        string f = Filter?.Trim() ?? "";
        var keep = SelectedItem;
        Items.Clear();
        foreach (var i in _all)
            if (i.Kind == kind && (f.Length == 0 || i.Label.Contains(f, StringComparison.OrdinalIgnoreCase)))
                Items.Add(i);
        if (keep != null && Items.Contains(keep)) SelectedItem = keep;
    }

    partial void OnSelectedItemChanged(MediaItem value)
    {
        if (value != null) _ = LoadAsync(value, play: true);
    }

    private async Task LoadAsync(MediaItem item, bool play)
    {
        int generation = ++_loadGeneration;
        Unload();
        Title = item.Label;
        IsLoading = true;
        try
        {
            if (item.Kind == MediaKind.Cutscene)
            {
                var video = await Task.Run(() => MediaLibrary.LoadVideo(item));
                if (generation != _loadGeneration) return;
                _video = new SmushPlayer(video);
                _rgba = new byte[video.Width * video.Height * 4];
                Duration = _video.Duration.TotalSeconds;
                IsVideo = true;
                VideoInfo = $"{video.Width}×{video.Height} · {_video.FrameRate} fps · {_video.FrameCount} frames · picture only (sound not decoded yet)";
                FrameText = null;
                _clock.Pause();
                _clock.Seek(TimeSpan.Zero);
                RequestFrame(0);
                if (play) { _clock.Play(); IsPlaying = true; }
            }
            else
            {
                var sound = await Task.Run(() => MediaLibrary.LoadSound(item));
                if (generation != _loadGeneration) return;
                _player = new ImusPlayer(sound);
                _player.EventRaised += e => _pendingEvents.Enqueue(e);
                Sound = sound;
                Duration = sound.FrameCount / (double)sound.SampleRate;
                IsAudio = true;
                Hook = 0;
                _group = item.Kind == MediaKind.Music ? MixGroup.Music : MixGroup.Voice;
                _voice = _engine.Mixer.Play(_player, _group);
                _voice.Paused = !play;
                IsPlaying = play;
            }
        }
        catch (Exception ex)
        {
            Status = $"Couldn't load {item.Id}: {ex.Message}";
            Log.Warn($"Media player: {item.Id}: {ex.Message}");
        }
        finally
        {
            if (generation == _loadGeneration) IsLoading = false;
        }
    }

    private void Unload()
    {
        _voice?.Stop();
        _voice = null;
        _player = null;
        _video = null;
        _shownFrame = _wantedFrame = -1;
        _clock.Pause();
        Sound = null;
        Frame = null;
        IsAudio = IsVideo = IsPlaying = false;
        Events.Clear();
        while (_pendingEvents.TryDequeue(out _)) { }
        SetPosition(0);
    }

    [RelayCommand]
    private void PlayPause()
    {
        if (_player != null)
        {
            if (_player.Finished) { _player.Restart(); _voice = _engine.Mixer.Play(_player, _group); IsPlaying = true; return; }
            IsPlaying = !IsPlaying;
            if (_voice != null) _voice.Paused = !IsPlaying;
        }
        else if (_video != null)
        {
            if (!IsPlaying && _clock.Position >= _video.Duration) _clock.Seek(TimeSpan.Zero);
            IsPlaying = !IsPlaying;
            if (IsPlaying) _clock.Play(); else _clock.Pause();
        }
    }

    [RelayCommand]
    private void Stop()
    {
        if (_player != null)
        {
            if (_voice != null) _voice.Paused = true;
            _player.Restart();
            if (_voice == null || _voice.Finished) { _voice = _engine.Mixer.Play(_player, _group); _voice.Paused = true; }
            Events.Clear();
        }
        else if (_video != null)
        {
            _clock.Pause();
            _clock.Seek(TimeSpan.Zero);
            RequestFrame(0);
        }
        IsPlaying = false;
        SetPosition(0);
    }

    /// <summary>Sets the track's hook: the next jump carrying that hook will be taken.</summary>
    [RelayCommand]
    private void SetHook()
    {
        if (_player == null) return;
        _player.Hook = Hook;
        AddEvent($"hook set to {Hook}");
    }

    /// <summary>Hook 128: the next hook-0 jump (a loop) falls through once.</summary>
    [RelayCommand]
    private void ExitLoop()
    {
        if (_player == null) return;
        _player.Hook = ImusPlayer.ExitLoopHook;
        AddEvent("hook set to 128 (leave the loop)");
    }

    /// <summary>Seeks the audio to a frame (from the timeline).</summary>
    public void SeekSound(long frame)
    {
        if (_player == null) return;
        if (_player.Finished && _voice != null && _voice.Finished)
        {
            _player.Restart();
            _voice = _engine.Mixer.Play(_player, _group);
            _voice.Paused = !IsPlaying;
        }
        if (!_player.Seek(frame)) AddEvent($"frame {frame} isn't in a region");
    }

    partial void OnPositionChanged(double value)
    {
        if (_updatingPosition) return;
        if (_video != null)
        {
            _clock.Seek(TimeSpan.FromSeconds(value));
            RequestFrame(_video.FrameAt(TimeSpan.FromSeconds(value)));
        }
        else if (_player != null)
            SeekSound((long)(value * _player.SampleRate));
    }

    /// <summary>Follows playback: called by the view on every animation tick.</summary>
    public void Tick()
    {
        while (_pendingEvents.TryDequeue(out var e)) AddEvent(Describe(e));
        if (_player != null)
        {
            HookText = $"Track hook: {_player.Hook}";
            // What is heard lags what is mixed by the output's latency.
            long lag = (long)(_engine.Output.Latency.TotalSeconds * _player.SampleRate);
            long heard = Math.Max(0, _player.Frame - (IsPlaying ? lag : 0));
            SoundFrame = heard;
            SetPosition(heard / (double)_player.SampleRate);
            if (_player.Finished && IsPlaying && (_voice == null || _voice.Finished)) IsPlaying = false;
        }
        else if (_video != null)
        {
            var t = _clock.Position;
            if (IsPlaying && t >= _video.Duration)
            {
                _clock.Pause();
                _clock.Seek(_video.Duration);
                IsPlaying = false;
            }
            SetPosition(Math.Min(t.TotalSeconds, Duration));
            if (IsPlaying) _wantedFrame = _video.FrameAt(t);
            DecodeWanted();
        }
    }

    /// <summary>Asks for a frame; the newest request wins.</summary>
    private void RequestFrame(int index)
    {
        _wantedFrame = index;
        DecodeWanted();
    }

    /// <summary>
    /// Decodes the wanted frame in the background, one decode at a time; a frame asked for meanwhile is
    /// decoded when the current one finishes.
    /// </summary>
    private void DecodeWanted()
    {
        var video = _video;
        int index = _wantedFrame;
        if (video == null || index < 0 || index == _shownFrame || !_decoding.IsCompleted) return;
        var rgba = _rgba;
        _decoding = Task.Run(() =>
        {
            video.ShowFrame(index);
            video.CopyRgba(rgba);
            var info = new SKImageInfo(video.Video.Width, video.Video.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            return SKImage.FromPixelCopy(info, rgba, video.Video.Width * 4);
        }).ContinueWith(t =>
        {
            if (t.IsFaulted) { Log.Warn($"Media player: frame {index}: {t.Exception?.GetBaseException().Message}"); return; }
            // Frames aren't disposed here: the render thread may still be drawing the previous one.
            if (_video != video) return;
            Frame = t.Result;
            _shownFrame = index;
            FrameText = $"frame {video.CurrentIndex + 1} / {video.FrameCount}  ·  {Format(video.CurrentIndex / video.FrameRate)}";
            DecodeWanted();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void SetPosition(double seconds)
    {
        _updatingPosition = true;
        Position = seconds;
        _updatingPosition = false;
        PositionText = $"{Format(seconds)} / {Format(Duration)}";
    }

    private static string Format(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss\.f") : t.ToString(@"m\:ss\.f");
    }

    private string Describe(ImusEvent e)
    {
        string at = _player != null ? Format(e.Frame / (double)_player.SampleRate) : e.Frame.ToString();
        return e.Kind switch
        {
            ImusEvent.EventKind.Marker => $"{at}  marker \"{e.Text}\"",
            ImusEvent.EventKind.Region => $"{at}  region",
            ImusEvent.EventKind.Jump => $"{at}  jump → {Format(e.ToFrame / (double)(_player?.SampleRate ?? 1))}" + (e.Hook != 0 ? $" (hook {e.Hook})" : ""),
            ImusEvent.EventKind.JumpSkipped => $"{at}  jump skipped" + (e.Hook != 0 ? $" (needs hook {e.Hook})" : " (left the loop)"),
            ImusEvent.EventKind.Stop => $"{at}  end",
            _ => $"{at}  {e.Kind}",
        };
    }

    private void AddEvent(string text)
    {
        Events.Insert(0, text);
        while (Events.Count > MaxEvents) Events.RemoveAt(Events.Count - 1);
    }

    public void Dispose()
    {
        _loadGeneration++;
        Unload();
        _engine.Dispose();
    }
}
