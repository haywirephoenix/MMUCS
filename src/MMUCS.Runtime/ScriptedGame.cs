using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MMUCS.Core.Audio;
using MMUCS.Core.Diagnostics;
using MMUCS.Core.Media;
using MMUCS.Core.Scumm;
using MMUCS.Core.Smush;
using MMUCS.Runtime.Scripts;
using MMUCS.Runtime.Sound;

namespace MMUCS.Runtime;

/// <summary>
/// The game run by its own scripts (MMUCS.Runtime.Scripts), for a host to show: it boots the game, turns
/// jiffies into the engine's frames, and takes the host's pointer, buttons and keys. Positions are in game
/// units on the 16:9 canvas, as <see cref="GameState"/> has them; the scripts see the 640×480 page in its
/// middle. <see cref="Changed"/> says when to look again (after a frame, input, or when the game loads).
/// <para>
/// The engine runs on its own thread (<see cref="EngineThread"/>), a step at a time while the host waits, so
/// <c>start-video</c> can stop everything in the middle of a frame as ScummVM's engine does: the host then
/// plays the video (<see cref="Video"/>, timed by the sound output's played clock when there is one, by
/// jiffies otherwise) and the frame carries on when it ends or is skipped.
/// </para>
/// </summary>
public sealed class ScriptedGame : IDisposable
{
    private readonly EngineThread _engine = new();
    private string _requestedVideo;
    private Task<(SmushVideo Video, SmushAudioTrack Sound)> _videoLoad;
    private MediaClock _videoClock;
    private MixerVoice _videoVoice;
    private long _videoJiffies;
    private Mixer _mixer;
    private IAudioOutput _output;

    /// <summary>The game's first resource file, beside which (or in its resource folder) the videos are.</summary>
    public string GameFile { get; private set; }

    /// <summary>The video playing (loaded), or null.</summary>
    public SmushPlayer Video { get; private set; }

    /// <summary>The name of the video a script started (playing or still loading), or null.</summary>
    public string VideoName { get; private set; }

    public bool IsVideoPlaying => VideoName != null;

    /// <summary>How far into <see cref="Video"/> it is.</summary>
    public TimeSpan VideoTime => _videoClock?.Position ?? TimeSpan.FromSeconds(_videoJiffies / 60.0);

    /// <summary>Where the game's sound goes (videos and music); without, videos play silently on jiffies and the music isn't heard.</summary>
    public void SetAudio(Mixer mixer, IAudioOutput output)
    {
        _mixer = mixer;
        _output = output;
        if (MusicOutput != null) MusicOutput.Mixer = mixer;
    }

    /// <summary>The music the scripts ask for (null without the game's program, which holds COMI's music tables).</summary>
    public ComiMusic Music { get; private set; }
    public MixerMusic MusicOutput { get; private set; }
    private bool _mouseInside, _booted;

    /// <summary>Something that's shown changed.</summary>
    public event Action Changed;

    public ScummBlock Root { get; private set; }
    public ScriptStore Scripts { get; private set; }
    public ScriptVm Vm { get; private set; }
    public FrameLoop Loop { get; private set; }

    public bool IsLoaded => Vm != null;
    public bool IsPlaying { get; private set; }

    /// <summary>Whether the host has the mouse for the game (its cursor shows and clicks count).</summary>
    public bool IsMouseCaptured { get; private set; }

    /// <summary>Scripts that faulted, most recent last (the game carries on without them).</summary>
    public int Faults { get; private set; }
    public string LastFault { get; private set; }

    /// <summary>The cursor in game units on the canvas.</summary>
    public GamePoint CursorPosition { get; private set; } = new(GameState.CanvasWidth / 2, GameState.CanvasHeight / 2);

    /// <summary>
    /// Where the camera looks in the room (its centre, room pixels), from the camera variables; the page's top
    /// left is half a screen up and left of it.
    /// </summary>
    public GamePoint Camera => Vm == null ? new(320, 240) : new(Vm.State[EngineVars.CameraPosX], Vm.State[EngineVars.CameraPosY]);

    public int CurrentRoom => Vm?.CurrentRoom ?? 0;

    /// <summary>What's happening, for a status line.</summary>
    public string Status => Vm == null ? "Open the game first."
        : !IsPlaying ? "Press Play to boot the game."
        : $"room {CurrentRoom} {Scripts.RoomName(CurrentRoom)} · {Loop.Frames} frames · {Running} scripts" +
          (IsVideoPlaying ? $" · video {VideoName}{(Video == null ? " (loading)" : $" {VideoTime:m\\:ss} / {Video.Duration:m\\:ss}")}" : "") +
          (MusicOutput?.Current is { } track ? $" · music {track.File}" : "") +
          (Faults > 0 ? $" · {Faults} faults (last: {LastFault})" : "");

    private int Running
    {
        get
        {
            int n = 0;
            foreach (var s in Vm.State.Slots) if (s.IsAlive) n++;
            return n;
        }
    }

    /// <summary>Gets the game's scripts and tables ready; <see cref="Play"/> boots it. Without <paramref name="gameFile"/> videos are skipped.</summary>
    public void Load(ScummBlock root, string gameFile = null)
    {
        Root = root ?? throw new ArgumentNullException(nameof(root));
        GameFile = gameFile;
        Scripts = new ScriptStore(root);
        Vm = new ScriptVm(Scripts, new VmState(GameLimits.Read(root) ?? GameLimits.Comi));
        Vm.Faulted += f =>
        {
            Faults++;
            LastFault = f.ToString();
        };
        // start-video: the engine thread waits here while the host plays it.
        Vm.PlayVideo = name =>
        {
            if (!_engine.IsEngineThread) return;
            _requestedVideo = name;
            _engine.Park();
        };
        Loop = new FrameLoop(Vm);
        _booted = false;
        Music = null;
        MusicOutput = null;
        if (MusicTable.Load(gameFile) is { } table)
        {
            MusicOutput = new MixerMusic(MixerMusic.FromBundles(gameFile)) { Mixer = _mixer };
            Music = new ComiMusic(table, MusicOutput);
            Music.Changed += what => Log.Info($"Game music: {what}");
            var music = Music;
            Vm.SoundKludge = list => music.Command(list);
            // iMUSE's refresh, once a frame after everything else (ScummVM scummLoop_handleSound).
            Loop.AfterScripts += _ =>
            {
                MusicOutput.Update();
                Music.Refresh();
            };
        }
        else if (gameFile != null) Log.Warn("Game: no comi.exe beside the game, so no music (its tables are in the program)");
        // The camera decides where the page's top left is in the room, for the virtual mouse.
        Loop.BeforeScripts += _ => Loop.ScreenOrigin = (Vm.State[EngineVars.CameraPosX] - 320, Vm.State[EngineVars.CameraPosY] - 240);
        Changed?.Invoke();
    }

    /// <summary>Boots the game (once; Stop leaves it where it is, Play carries on).</summary>
    public void Play()
    {
        if (Vm == null || IsPlaying) return;
        IsPlaying = true;
        if (!_booted)
        {
            _booted = true;
            Step(() => Vm.Boot());
        }
        else if (_videoClock != null)
        {
            _videoClock.Play();
            if (_videoVoice != null) _videoVoice.Paused = false;
        }
        if (MusicOutput != null) MusicOutput.Paused = false;
        Changed?.Invoke();
    }

    public void Stop()
    {
        if (!IsPlaying) return;
        IsPlaying = false;
        _videoClock?.Pause();
        if (_videoVoice != null) _videoVoice.Paused = true;
        if (MusicOutput != null) MusicOutput.Paused = true;
        SetMouseCaptured(false);
        Changed?.Invoke();
    }

    public void SetMouseCaptured(bool captured)
    {
        captured &= IsPlaying;
        if (captured == IsMouseCaptured) return;
        IsMouseCaptured = captured;
        if (!captured)
        {
            Loop?.Release(MouseButton.Left);
            Loop?.Release(MouseButton.Right);
        }
        Changed?.Invoke();
    }

    /// <summary>Moves the cursor (clamped to the canvas); the scripts see it on the page.</summary>
    public void MoveCursor(GamePoint position)
    {
        CursorPosition = new GamePoint(Math.Clamp(position.X, 0, GameState.CanvasWidth), Math.Clamp(position.Y, 0, GameState.CanvasHeight));
        var page = GameState.PageOrigin;
        _mouseInside = CursorPosition.X >= page.X && CursorPosition.X < page.X + GameState.PageSize.Width;
        if (Loop != null) Loop.Mouse = ((int)Math.Floor(CursorPosition.X - page.X), (int)Math.Floor(CursorPosition.Y - page.Y));
        Changed?.Invoke();
    }

    public void Press(MouseButton button)
    {
        if (IsMouseCaptured) Loop?.Press(button);
    }

    public void Release(MouseButton button) => Loop?.Release(button);

    /// <summary>A key, as the engine numbers it (ASCII; F1–F9 are 315–323).</summary>
    public void PressKey(int code)
    {
        if (IsPlaying) Loop?.PressKey(code);
    }

    /// <summary>One jiffy (1/60 s) while playing: the video plays on, or a frame runs every timer-next jiffies.</summary>
    public void Tick()
    {
        if (!IsPlaying || Loop == null) return;
        if (IsVideoPlaying)
        {
            AdvanceVideo();
            return;
        }
        bool ran = false;
        Step(() => ran = Loop.Tick());
        if (ran || IsVideoPlaying) Changed?.Invoke();
    }

    /// <summary>Ends the video now; the cutscene it was in is skipped on the next frame.</summary>
    public void SkipVideo()
    {
        if (IsVideoPlaying) EndVideo(skipped: true);
    }

    /// <summary>Runs a step on the engine thread; one that parks has started a video.</summary>
    private void Step(Action work)
    {
        try
        {
            if (!_engine.Run(work)) BeginVideo();
        }
        catch (Exception ex)
        {
            Log.Error($"Game engine: {ex}");
            Faults++;
            LastFault = ex.Message;
            IsPlaying = false;
        }
    }

    private void BeginVideo()
    {
        VideoName = _requestedVideo;
        _requestedVideo = null;
        _videoJiffies = 0;
        string path = GameFile == null ? null
            : SmushVideo.FindAll(GameFile).FirstOrDefault(f => string.Equals(Path.GetFileName(f), VideoName, StringComparison.OrdinalIgnoreCase));
        if (path == null)
        {
            Log.Warn($"Game: video {VideoName} not found, going on without it");
            EndVideo(skipped: false);
            return;
        }
        _videoLoad = Task.Run(() =>
        {
            var video = SmushVideo.Load(path);
            return (video, SmushAudio.Decode(video));
        });
        Changed?.Invoke();
    }

    /// <summary>A jiffy of the video: it starts once loaded, and ends when its time is up.</summary>
    private void AdvanceVideo()
    {
        if (Video == null)
        {
            if (_videoLoad is not { IsCompleted: true } load) return;
            _videoLoad = null;
            if (!load.IsCompletedSuccessfully)
            {
                Log.Warn($"Game: video {VideoName}: {load.Exception?.GetBaseException().Message}");
                EndVideo(skipped: false);
                return;
            }
            var (video, sound) = load.Result;
            Video = new SmushPlayer(video);
            if (_mixer != null && _output != null)
            {
                _videoClock = new MediaClock(_output);
                if (sound != null) _videoVoice = _mixer.Play(new PcmSource(sound.Pcm, sound.SampleRate, sound.Channels), MixGroup.Video);
                _videoClock.Play();
            }
        }
        _videoJiffies++;
        if (VideoTime >= Video.Duration) EndVideo(skipped: false);
        else Changed?.Invoke();
    }

    /// <summary>The video is over: its sound stops and the engine finishes the frame (which may start another).</summary>
    private void EndVideo(bool skipped)
    {
        _videoVoice?.Stop();
        _videoVoice = null;
        _videoClock = null;
        _videoLoad = null;
        Video = null;
        VideoName = null;
        if (skipped) Loop.VideoSkipped();
        try
        {
            if (!_engine.Resume()) BeginVideo();
        }
        catch (Exception ex)
        {
            Log.Error($"Game engine: {ex}");
            Faults++;
            LastFault = ex.Message;
            IsPlaying = false;
        }
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _videoVoice?.Stop();
        MusicOutput?.FadeOut(0);
        _engine.Dispose();
    }

    /// <summary>Whether the cursor is over the page (the scripts' screen) rather than the canvas's sides.</summary>
    public bool IsCursorOnPage => _mouseInside;
}
