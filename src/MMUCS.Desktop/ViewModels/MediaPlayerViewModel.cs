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

    /// <summary>A Speech filter choice: a scene or speaker code (null for all) and what the list shows.</summary>
    public sealed record CodeFilter(string Code, string Label)
    {
        public override string ToString() => Label;
    }

    private const int MaxEvents = 300;

    private readonly string _gameFile;
    private readonly MMUCS.Core.Scumm.ScummBlock _root;
    private readonly MediaEngine _engine;
    private readonly MediaClock _clock;
    private bool _disposed;
    // Each kind as its scan finishes: the category shown first, the rest after it.
    private readonly Dictionary<MediaKind, IReadOnlyList<MediaItem>> _library = [];
    private string _scanError;
    private int _loadGeneration;

    // Sound
    private ImusPlayer _player;
    private MixerVoice _voice;
    private MixGroup _group;
    private readonly ConcurrentQueue<ImusEvent> _pendingEvents = new();

    // Picture
    private SmushPlayer _video;
    // The cutscene's sound: the picture follows it as it is heard.
    private PcmSource _videoSound;
    private MixerVoice _videoVoice;
    private bool _decodingBusy;
    private int _shownFrame = -1;
    private int _wantedFrame = -1;
    private byte[] _rgba;

    private bool _updatingPosition;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("MvvmToolkit", "MVVMTK0042", Justification = "The constructor sets the field so OnSelectedCategoryChanged does not filter before the scan.")] [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsSpeech))] private Category _selectedCategory;
    [ObservableProperty] public partial IReadOnlyList<CodeFilter> Scenes { get; set; } = Array.Empty<CodeFilter>();
    [ObservableProperty] public partial CodeFilter SelectedScene { get; set; }
    [ObservableProperty] public partial IReadOnlyList<CodeFilter> Speakers { get; set; } = Array.Empty<CodeFilter>();
    [ObservableProperty] public partial CodeFilter SelectedSpeaker { get; set; }
    /// <summary>The library list, replaced as a whole when the filter changes (speech has thousands of lines).</summary>
    [ObservableProperty] public partial IReadOnlyList<MediaItem> Items { get; set; } = Array.Empty<MediaItem>();
    [ObservableProperty] public partial string Filter { get; set; } = "";
    [ObservableProperty] public partial MediaItem SelectedItem { get; set; }
    [ObservableProperty] public partial string Title { get; set; } = "Nothing loaded";
    [ObservableProperty] public partial string Status { get; set; }
    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial bool IsPlaying { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsEmpty))] public partial bool IsVideo { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsEmpty))] public partial bool IsAudio { get; set; }
    [ObservableProperty] public partial double Position { get; set; }
    [ObservableProperty] public partial double Duration { get; set; } = 1;
    [ObservableProperty] public partial string PositionText { get; set; } = "0:00.0 / 0:00.0";
    [ObservableProperty] public partial double Volume { get; set; } = 0.8;
    [ObservableProperty] public partial SKImage Frame { get; set; }
    /// <summary>The changing frame readout, drawn over the picture so it never resizes the canvas.</summary>
    [ObservableProperty] public partial string FrameText { get; set; }
    /// <summary>The cutscene's fixed details, for the viewer's toolbar.</summary>
    [ObservableProperty] public partial string VideoInfo { get; set; }
    [ObservableProperty] public partial bool ShowFrameInfo { get; set; } = true;
    [ObservableProperty] public partial ImusSound Sound { get; set; }
    [ObservableProperty] public partial long SoundFrame { get; set; }
    [ObservableProperty] public partial int Hook { get; set; }
    [ObservableProperty] public partial string HookText { get; set; } = "Track hook: 0";

    public IReadOnlyList<Category> Categories { get; } =
    [
        new Category(MediaKind.Cutscene, "Cutscenes"),
        new Category(MediaKind.Music, "Music"),
        new Category(MediaKind.Voice, "Speech"),
    ];

    public ObservableCollection<string> Events { get; } = [];

    public string OutputName => _engine.Output.Name;
    public bool IsEmpty => !IsVideo && !IsAudio;
    public bool IsSpeech => SelectedCategory?.Kind == MediaKind.Voice;

    public static readonly Avalonia.Data.Converters.IValueConverter PlayPauseLabel =
        new Avalonia.Data.Converters.FuncValueConverter<bool, string>(playing => playing ? "Pause" : "Play");
    public bool HasGame => _gameFile != null;

    /// <param name="root">The loaded project: speech gets its text, scenes and speakers from its scripts.</param>
    public MediaPlayerViewModel(string gameFile, MMUCS.Core.Scumm.ScummBlock root = null)
    {
        _gameFile = gameFile;
        _root = root;
        _engine = new MediaEngine();
        _engine.Mixer.MasterVolume = (float)Volume;
        _clock = new MediaClock(_engine.Output);
        _selectedCategory = Categories[0];
        if (gameFile == null)
        {
            Status = "Open the game (COMI.LA0) first: the media player lists its cutscenes and bundles.";
        }
        else
        {
            _ = ScanAsync();
        }
    }

    /// <summary>
    /// Scans the library a kind at a time, the category being shown first (switching category while it
    /// scans moves that one up), so the list fills as soon as its own kind is read: cutscenes take a few
    /// milliseconds, speech (thousands of lines) the longest.
    /// </summary>
    private async Task ScanAsync()
    {
        IsLoading = true;
        UpdateStatus();
        try
        {
            while (!_disposed && NextToScan() is { } kind)
            {
                var items = await Task.Run(() => MediaLibrary.Scan(_gameFile, kind, _root));
                if (_disposed) return;
                _library[kind] = items;
                if (kind == MediaKind.Voice) BuildScenes();
                if (SelectedCategory?.Kind == kind) ApplyFilter();
                UpdateStatus();
            }
        }
        catch (Exception ex)
        {
            _scanError = ex.Message;
            UpdateStatus();
        }
        finally { IsLoading = false; }
    }

    /// <summary>The category being shown when it hasn't been scanned yet, else the first one still to scan.</summary>
    private MediaKind? NextToScan()
    {
        if (SelectedCategory is { } shown && !_library.ContainsKey(shown.Kind)) return shown.Kind;
        return Categories.Select(c => (MediaKind?)c.Kind).FirstOrDefault(k => !_library.ContainsKey(k.Value));
    }

    private IReadOnlyList<MediaItem> Library(MediaKind kind) => _library.GetValueOrDefault(kind) ?? [];

    /// <summary>What the library holds ("15 cutscenes · 129 music tracks · scanning speech…") and the output.</summary>
    private void UpdateStatus()
    {
        if (_gameFile == null) return;
        if (_scanError != null)
        {
            Status = $"Couldn't read the game's media: {_scanError}";
            return;
        }
        var parts = new List<string>();
        var pending = new List<string>();
        foreach (var (kind, count, name) in new[]
                 {
                     (MediaKind.Cutscene, "{0} cutscenes", "cutscenes"),
                     (MediaKind.Music, "{0} music tracks", "music"),
                     (MediaKind.Voice, "{0:N0} speech lines", "speech"),
                 })
        {
            if (_library.TryGetValue(kind, out var items)) parts.Add(string.Format(count, items.Count));
            else pending.Add(name);
        }
        if (pending.Count > 0) parts.Add($"scanning {string.Join(" and ", pending)}…");
        parts.Add(OutputName);
        Status = string.Join(" · ", parts);
    }

    partial void OnSelectedCategoryChanged(Category value) => ApplyFilter();
    partial void OnFilterChanged(string value) => ApplyFilter();
    // The speech filters only change the list while speech is shown (speech's scan sets them up meanwhile).
    partial void OnSelectedSceneChanged(CodeFilter value)
    {
        BuildSpeakers();
        if (IsSpeech) ApplyFilter();
    }
    partial void OnSelectedSpeakerChanged(CodeFilter value)
    {
        if (IsSpeech) ApplyFilter();
    }

    private IReadOnlyList<MediaItem> Speech => Library(MediaKind.Voice);

    /// <summary>Speech scenes with their line counts, biggest first after "All".</summary>
    private void BuildScenes()
    {
        var scenes = Speech.GroupBy(i => i.Scene)
            .Select(g => new CodeFilter(g.Key, $"{g.Key} · {g.First().SceneName ?? "?"}  ({g.Count():N0})"))
            .OrderBy(f => f.Code, StringComparer.Ordinal);
        Scenes = [new CodeFilter(null, $"All scenes  ({Speech.Count:N0})"), .. scenes];
        SelectedScene = Scenes[0];
    }

    /// <summary>The speakers in the chosen scene, most lines first; keeps the speaker chosen when it's there.</summary>
    private void BuildSpeakers()
    {
        string keep = SelectedSpeaker?.Code;
        var lines = Speech.Where(i => SelectedScene?.Code == null || i.Scene == SelectedScene.Code).ToList();
        var speakers = lines.GroupBy(i => i.Speaker)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new CodeFilter(g.Key, $"{g.Key}{(g.First().SpeakerName is { } n ? " · " + n : "")}  ({g.Count():N0})"));
        Speakers = [new CodeFilter(null, $"All speakers  ({lines.Count:N0})"), .. speakers];
        SelectedSpeaker = Speakers.FirstOrDefault(s => s.Code == keep) ?? Speakers[0];
    }
    partial void OnVolumeChanged(double value) => _engine.Mixer.MasterVolume = (float)Math.Clamp(value, 0, 1);

    private void ApplyFilter()
    {
        var kind = SelectedCategory?.Kind ?? MediaKind.Music;
        string f = Filter?.Trim() ?? "";
        string scene = kind == MediaKind.Voice ? SelectedScene?.Code : null;
        string speaker = kind == MediaKind.Voice ? SelectedSpeaker?.Code : null;
        var keep = SelectedItem;
        Items = [.. Library(kind).Where(i => (scene == null || i.Scene == scene)
                && (speaker == null || i.Speaker == speaker)
                && (f.Length == 0 || i.Label.Contains(f, StringComparison.OrdinalIgnoreCase)
                    || (i.SceneName?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (i.SpeakerName?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false)))];
        if (keep != null && Items.Contains(keep))
        {
            SelectedItem = keep;
        }
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
                var (video, sound) = await Task.Run(() =>
                {
                    var v = MediaLibrary.LoadVideo(item);
                    return (v, MMUCS.Core.Smush.SmushAudio.Decode(v));
                });
                if (generation != _loadGeneration)
                {
                    return;
                }

                if (sound != null)
                {
                    _videoSound = new PcmSource(sound.Pcm, sound.SampleRate, sound.Channels);
                }

                _video = new SmushPlayer(video);
                _rgba = new byte[video.Width * video.Height * 4];
                Duration = _video.Duration.TotalSeconds;
                IsVideo = true;
                VideoInfo = $"{video.Width}×{video.Height} · {_video.FrameRate} fps · {_video.FrameCount} frames · " +
                            (sound != null ? $"sound {sound.SampleRate:N0} Hz stereo, {sound.Duration.TotalSeconds:0.0} s" : "no sound");
                FrameText = null;
                _clock.Pause();
                _clock.Seek(TimeSpan.Zero);
                RequestFrame(0);
                if (play) { _clock.Play(); IsPlaying = true; }
                SetVideoSound(IsPlaying);
            }
            else
            {
                var sound = await Task.Run(() => MediaLibrary.LoadSound(item));
                if (generation != _loadGeneration)
                {
                    return;
                }

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
            if (generation == _loadGeneration)
            {
                IsLoading = false;
            }
        }
    }

    private void Unload()
    {
        _voice?.Stop();
        _voice = null;
        _player = null;
        _video = null;
        _videoVoice?.Stop();
        _videoVoice = null;
        _videoSound = null;
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
            _voice?.Paused = !IsPlaying;
        }
        else if (_video != null)
        {
            if (!IsPlaying && _clock.Position >= _video.Duration) { _clock.Seek(TimeSpan.Zero); _videoSound?.Seek(0); }
            IsPlaying = !IsPlaying;
            if (IsPlaying)
            {
                _clock.Play();
            }
            else
            {
                _clock.Pause();
            }

            SetVideoSound(IsPlaying);
        }
    }

    [RelayCommand]
    private void Stop()
    {
        if (_player != null)
        {
            _voice?.Paused = true;

            _player.Restart();
            if (_voice == null || _voice.Finished) { _voice = _engine.Mixer.Play(_player, _group); _voice.Paused = true; }
            Events.Clear();
        }
        else if (_video != null)
        {
            _clock.Pause();
            _clock.Seek(TimeSpan.Zero);
            _videoSound?.Seek(0);
            SetVideoSound(false);
            RequestFrame(0);
        }
        IsPlaying = false;
        SetPosition(0);
    }

    /// <summary>Sets the track's hook: the next jump carrying that hook will be taken.</summary>
    [RelayCommand]
    private void SetHook()
    {
        if (_player == null)
        {
            return;
        }

        _player.Hook = Hook;
        AddEvent($"hook set to {Hook}");
    }

    /// <summary>Hook 128: the next hook-0 jump (a loop) falls through once.</summary>
    [RelayCommand]
    private void ExitLoop()
    {
        if (_player == null)
        {
            return;
        }

        _player.Hook = ImusPlayer.ExitLoopHook;
        AddEvent("hook set to 128 (leave the loop)");
    }

    /// <summary>Seeks the audio to a frame (from the timeline).</summary>
    public void SeekSound(long frame)
    {
        if (_player == null)
        {
            return;
        }

        if (_player.Finished && _voice != null && _voice.Finished)
        {
            _player.Restart();
            _voice = _engine.Mixer.Play(_player, _group);
            _voice.Paused = !IsPlaying;
        }
        if (!_player.Seek(frame))
        {
            AddEvent($"frame {frame} isn't in a region");
        }
    }

    partial void OnPositionChanged(double value)
    {
        if (_updatingPosition) return;
        if (_video != null)
        {
            _clock.Seek(TimeSpan.FromSeconds(value));
            _videoSound?.Seek((long)(value * _videoSound.SampleRate));
            SetVideoSound(IsPlaying);
            RequestFrame(_video.FrameAt(TimeSpan.FromSeconds(value)));
        }
        else if (_player != null)
            SeekSound((long)(value * _player.SampleRate));
    }

    /// <summary>Follows playback: called by the view on every animation tick.</summary>
    public void Tick()
    {
        while (_pendingEvents.TryDequeue(out var e))
        {
            AddEvent(Describe(e));
        }

        if (_player != null)
        {
            HookText = $"Track hook: {_player.Hook}";
            // What is heard lags what is mixed by the output's latency.
            long lag = (long)(_engine.Output.Latency.TotalSeconds * _player.SampleRate);
            long heard = Math.Max(0, _player.Frame - (IsPlaying ? lag : 0));
            SoundFrame = heard;
            SetPosition(heard / (double)_player.SampleRate);
            if (_player.Finished && IsPlaying && (_voice == null || _voice.Finished))
            {
                IsPlaying = false;
            }
        }
        else if (_video != null)
        {
            // While the sound plays, the clock follows it as heard (what's mixed reaches the speakers one
            // latency later); once it ends, the clock runs on by itself to the last frame.
            if (IsPlaying && _videoSound != null && !_videoSound.Ended)
            {
                double heard = _videoSound.Frame / (double)_videoSound.SampleRate - _engine.Output.Latency.TotalSeconds;
                _clock.Seek(TimeSpan.FromSeconds(Math.Max(0, heard)));
            }
            var t = _clock.Position;
            if (IsPlaying && t >= _video.Duration)
            {
                _clock.Pause();
                _clock.Seek(_video.Duration);
                IsPlaying = false;
            }
            SetPosition(Math.Min(t.TotalSeconds, Duration));
            if (IsPlaying)
            {
                _wantedFrame = _video.FrameAt(t);
            }

            DecodeWanted();
        }
    }

    /// <summary>Plays or pauses the cutscene's sound, adding it to the mixer again if it had played to its end.</summary>
    private void SetVideoSound(bool playing)
    {
        if (_videoSound == null)
        {
            return;
        }

        if (_videoVoice == null || (_videoVoice.Finished && !_videoSound.Ended))
        {
            _videoVoice = _engine.Mixer.Play(_videoSound, MixGroup.Video);
        }

        _videoVoice.Paused = !playing;
    }

    /// <summary>Asks for a frame; the newest request wins.</summary>
    private void RequestFrame(int index)
    {
        _wantedFrame = index;
        DecodeWanted();
    }

    /// <summary>
    /// Decodes the wanted frame in the background, one decode at a time, and keeps going while a newer
    /// frame is wanted (seeking or playing on meanwhile).
    /// </summary>
    private async void DecodeWanted()
    {
        if (_decodingBusy)
        {
            return;
        }

        var video = _video;
        if (video == null)
        {
            return;
        }

        _decodingBusy = true;
        try
        {
            while (_video == video && _wantedFrame >= 0 && _wantedFrame != _shownFrame)
            {
                int index = _wantedFrame;
                var rgba = _rgba;
                var image = await Task.Run(() =>
                {
                    video.ShowFrame(index);
                    video.CopyRgba(rgba);
                    var info = new SKImageInfo(video.Video.Width, video.Video.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
                    return SKImage.FromPixelCopy(info, rgba, video.Video.Width * 4);
                });
                // Frames aren't disposed here: the render thread may still be drawing the previous one.
                if (_video != video)
                {
                    return;
                }

                Frame = image;
                _shownFrame = index;
                FrameText = $"frame {video.CurrentIndex + 1} / {video.FrameCount}  ·  {Format(video.CurrentIndex / video.FrameRate)}";
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Media player: frame {_wantedFrame}: {ex.Message}");
        }
        finally
        {
            _decodingBusy = false;
        }
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
        while (Events.Count > MaxEvents)
        {
            Events.RemoveAt(Events.Count - 1);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _loadGeneration++;
        Unload();
        _engine.Dispose();
    }
}
