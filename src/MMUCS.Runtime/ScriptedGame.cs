using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MMUCS.Core;
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
    private EngineThread _engine = new();
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
        MusicOutput?.Mixer = mixer;
        Effects?.Mixer = mixer;
        Voice?.Mixer = mixer;
    }

    /// <summary>
    /// <c>get-string-width</c> in these fonts (charset N is fontN.nut): the widest line's pen advance, the
    /// text's ^ codes skipped; 0 for a charset there's no font for.
    /// </summary>
    /// <summary>The scripts measure text in these fonts: string widths, and the line heights verbs are hit by.</summary>
    public static void UseFonts(ScriptVm vm, MMUCS.Core.Fonts.GlyphFont[] fonts)
    {
        vm.StringWidth = MeasureWith(fonts);
        vm.FontHeight = charset => charset >= 0 && charset < fonts.Length && fonts[charset] is { } font ? (int)font.LineHeight : 0;
    }

    public static Func<int, string, int> MeasureWith(MMUCS.Core.Fonts.GlyphFont[] fonts) => (charset, text) =>
    {
        var font = charset >= 0 && charset < fonts.Length ? fonts[charset] : null;
        if (font == null) return 0;
        int width = 0;
        foreach (var line in text.Split('\n')) width = Math.Max(width, (int)MMUCS.Core.Fonts.TextLayouter.Width(line, 0, line.Length, font));
        return width;
    };

    /// <summary>The game's fonts by charset number (empty without the game's files).</summary>
    public MMUCS.Core.Fonts.GlyphFont[] Fonts { get; private set; } = [];

    /// <summary>The lines' speech.</summary>
    public VoicePlayer Voice { get; private set; }

    /// <summary>The sound effects the scripts start.</summary>
    public SoundEffects Effects { get; private set; }

    /// <summary>The music the scripts ask for (null without the game's program, which holds COMI's music tables).</summary>
    public ComiMusic Music { get; private set; }
    public MixerMusic MusicOutput { get; private set; }
    private bool _mouseInside, _booted, _quitRequested;

    /// <summary>Something that's shown changed.</summary>
    public event Action Changed;

    public ScummBlock Root { get; private set; }
    public ScriptStore Scripts { get; private set; }
    public ScriptVm Vm { get; private set; }
    public FrameLoop Loop { get; private set; }

    public bool IsLoaded => Vm != null;
    public bool IsPlaying { get; private set; }

    /// <summary>Whether the game has booted (playing or paused) since it was loaded or last reset.</summary>
    public bool IsStarted => _booted;

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
    public GamePoint Camera => Vm == null ? new(320, 240) : SmoothCamera ? new(_viewX, _viewY) : CameraTarget;

    /// <summary>Where the camera shown heads: the engine's, kept inside the room (<see cref="InRoom"/>).</summary>
    public GamePoint CameraTarget => Vm == null ? new(320, 240) : InRoom(EngineCamera);

    /// <summary>
    /// A camera kept so the whole 16:9 canvas the window shows (wider than the engine's 640 screen) stays inside
    /// the room, so nothing past the room's edges is drawn: half the canvas in from each edge, or the room's
    /// middle when it's narrower or shorter than the canvas (a 640 room is where the engine has it). A wide room
    /// so starts further right than the engine's camera, with the mouse and texts moved to match; the scripts
    /// still see the engine's camera, which they may also let past the room.
    /// </summary>
    private GamePoint InRoom(GamePoint at)
    {
        if (CurrentRoom == 0) return at;
        int width = Vm.State[EngineVars.RoomWidth], height = Vm.State[EngineVars.RoomHeight];
        return new(Within(at.X, width, GameState.CanvasWidth), Within(at.Y, height, GameState.CanvasHeight));

        static double Within(double centre, int room, double view) =>
            room <= view ? room / 2.0 : Math.Clamp(centre, view / 2, room - view / 2);
    }

    /// <summary>Where the engine's camera is (ScummVM's stepping, what the scripts see).</summary>
    public GamePoint EngineCamera => Vm == null ? new(320, 240) : new(Vm.CameraX, Vm.CameraY);

    /// <summary>
    /// A smooth camera instead of the engine's steps: what's shown eases towards <see cref="CameraTarget"/> every
    /// jiffy (half the way in <see cref="SmoothCameraHalfLife"/> jiffies), snapping on a room change or a jump
    /// of more than <see cref="SmoothCameraSnap"/> pixels in a jiffy. Only the picture and the mouse follow it;
    /// the scripts still see the engine's camera.
    /// </summary>
    public bool SmoothCamera { get; set; }

    /// <summary>
    /// How far to slide actors from where the last frame drew them to where this one does: with the smooth
    /// camera, the way through the wait for the next frame (so walking glides with the camera instead of
    /// stepping against it); otherwise 1, where the engine has them.
    /// </summary>
    public double ActorSlide => SmoothCamera && Loop != null ? Loop.Progress : 1;

    /// <summary>Whether an actor drawn last frame moved, so the picture changes as it slides.</summary>
    private bool ActorsSliding()
    {
        foreach (var p in Vm.Actors.Pictures)
            if (p.X != p.FromX || p.Y != p.FromY) return true;
        foreach (var p in Vm.Actors.UpperPictures)
            if (p.X != p.FromX || p.Y != p.FromY) return true;
        return false;
    }

    public const double SmoothCameraHalfLife = 4;
    public const int SmoothCameraSnap = 100;

    private double _viewX = 320, _viewY = 240;
    private int _viewRoom = -1;
    private GamePoint _lastEngineCamera = new(320, 240);

    /// <summary>A jiffy of the smooth camera easing towards the engine's.</summary>
    private void EaseCamera()
    {
        var engine = EngineCamera;
        bool jumped = Math.Abs(engine.X - _lastEngineCamera.X) > SmoothCameraSnap || Math.Abs(engine.Y - _lastEngineCamera.Y) > SmoothCameraSnap;
        _lastEngineCamera = engine;
        var target = CameraTarget;
        if (!SmoothCamera || jumped || _viewRoom != CurrentRoom)
        {
            (_viewX, _viewY, _viewRoom) = (target.X, target.Y, CurrentRoom);
            return;
        }
        double f = 1 - Math.Pow(0.5, 1 / SmoothCameraHalfLife);
        _viewX += (target.X - _viewX) * f;
        _viewY += (target.Y - _viewY) * f;
        if (Math.Abs(target.X - _viewX) < 0.25) _viewX = target.X;
        if (Math.Abs(target.Y - _viewY) < 0.25) _viewY = target.Y;
    }

    public int CurrentRoom => Vm?.CurrentRoom ?? 0;

    /// <summary>What's happening, for a status line.</summary>
    public string Status => Vm == null ? "Open the game first."
        // : !IsPlaying ? "Press Play to boot the game."
        : $"room {CurrentRoom} {Scripts.RoomName(CurrentRoom)} · {Loop.Frames} frames · {Running} scripts" +
          (IsVideoPlaying ? $" · video {VideoName}{(Video == null ? " (loading)" : $" {VideoTime:m\\:ss} / {Video.Duration:m\\:ss}")}" : "") +
          (MusicOutput?.Current is { } track ? $" · music {track.File}" : "") +
          (BootParam != 0 ? $" · booted with param {BootParam}" : "") +
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
        Music = null;
        MusicOutput = null;
        Effects = new SoundEffects(SoundEffects.FromResources(Scripts, root)) { Mixer = _mixer };
        Voice = new VoicePlayer(VoicePlayer.FromBundles(gameFile)) { Mixer = _mixer };
        // The game's fonts, for get-string-width (the scripts centre and fit text with it).
        Fonts = gameFile == null ? [] : MMUCS.Core.Fonts.NutFont.LoadAll(gameFile);
        if (MusicTable.Load(gameFile) is { } table)
        {
            MusicOutput = new MixerMusic(MixerMusic.FromBundles(gameFile)) { Mixer = _mixer };
            Music = new ComiMusic(table, MusicOutput);
            Music.Changed += what => Log.Info($"Game music: {what}");
        }
        else if (gameFile != null) Log.Warn("Game: no comi.exe beside the game, so no music (its tables are in the program)");
        NewEngine();
        Changed?.Invoke();
    }

    /// <summary>A fresh engine on the loaded game's scripts and sounds, not booted yet.</summary>
    private void NewEngine()
    {
        Vm = new ScriptVm(Scripts, new VmState(GameLimits.Read(Root) ?? GameLimits.Comi));
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
        _quitRequested = false;
        _viewRoom = -1;
        var effects = Effects;
        Vm.Effects = effects;
        var voice = Voice;
        Vm.Speech = voice;
        UseFonts(Vm, Fonts);
        Vm.ViewWidth = (int)Math.Ceiling(GameState.CanvasWidth); // the camera keeps the whole canvas in the room
        Vm.Said += (text, tag, actor) => Log.Info($"Game: {(actor == 0xFF ? "line" : $"actor {actor}")}{(tag.Length > 0 ? $" ({tag})" : "")}: {text}");
        var music = Music;
        var musicOutput = MusicOutput;
        Vm.SoundKludge = SoundKludge(effects, voice, music, musicOutput);
        // iMUSE's refresh, once a frame after everything else (ScummVM scummLoop_handleSound).
        Loop.AfterScripts += jiffies =>
        {
            effects.Update(jiffies);
            voice.Update(jiffies);
            musicOutput?.Update();
            music?.Refresh();
        };
        FollowSaves(Loop, effects, voice, music);
        Loop.StateLoaded += () => _viewRoom = -1; // the camera shown goes straight to the loaded one
        // Quitting waits for the engine's step to end (it's in the middle of a script).
        Vm.QuitRequested += () => _quitRequested = true;
        // The camera shown decides where the page's top left is in the room, for the virtual mouse.
        Loop.BeforeScripts += _ =>
        {
            Loop.ScreenOrigin = ((int)Math.Round(Camera.X) - 320, (int)Math.Round(Camera.Y) - 240);
            Loop.MouseRangeX = MouseRange();
        };
    }

    /// <summary>
    /// <c>sound-kludge</c> as Digital iMUSE takes it: the effects' commands, then the music's. Stop-all stops
    /// every iMUSE sound: the effects, the music and the speech.
    /// </summary>
    public static Func<int[], bool> SoundKludge(SoundEffects effects, VoicePlayer voice, ComiMusic music, IMusicOutput musicOutput) => list =>
    {
        if (list is [SoundEffects.StopAllCommand, ..])
        {
            musicOutput?.FadeOut(0);
            voice?.Stop();
        }
        return effects.Command(list) || music?.Command(list) == true;
    };

    /// <summary>
    /// The scripts' temporary save (the F1 menu's) for the sounds, which live outside the engine. As ScummVM's
    /// iMUSE keeps its tracks in a save, a save keeps the music (its state and the track playing, and where)
    /// and the line's speech (and where); a load stops the effects and puts the music and the speech back
    /// where they were, so the menu's silence in between is a pause.
    /// </summary>
    public static void FollowSaves(FrameLoop loop, SoundEffects effects, VoicePlayer voice, ComiMusic music)
    {
        ComiMusic.Saved savedMusic = null;
        SpeechPlace? savedSpeech = null;
        loop.StateSaved += () =>
        {
            savedMusic = music?.Save();
            savedSpeech = voice?.Place;
        };
        loop.StateLoaded += () =>
        {
            effects?.StopAll();
            voice?.Stop();
            if (savedSpeech is { } speech) voice?.Resume(speech);
            if (savedMusic != null) music?.Restore(savedMusic);
        };
    }

    /// <summary>The scripts quit the game (the F1 menu's Quit): it has stopped, and Play boots it afresh.</summary>
    public event Action Quit;

    /// <summary>
    /// Stops the game and throws its state away: the video and sounds stop, and a fresh engine waits for
    /// <see cref="Play"/> to boot it.
    /// </summary>
    public void Reset()
    {
        if (Vm == null) return;
        Stop();
        _videoVoice?.Stop();
        _videoVoice = null;
        _videoClock = null;
        _videoLoad = null;
        _requestedVideo = null;
        Video = null;
        VideoName = null;
        Effects?.StopAll();
        Voice?.Stop();
        MusicOutput?.FadeOut(0);
        if (_engine.IsParked)
        {
            // A step parked in start-video can't be carried on with a new engine: give it a thread of its own.
            _engine.Dispose();
            _engine = new EngineThread();
        }
        Faults = 0;
        LastFault = null;
        NewEngine();
        Changed?.Invoke();
    }

    /// <summary>After a step: a quit the scripts asked for stops and clears the game.</summary>
    private void QuitIfAsked()
    {
        if (!_quitRequested) return;
        Log.Info("Game: the scripts quit");
        Reset();
        Quit?.Invoke();
    }

    /// <summary>
    /// The boot parameter the boot script gets (ScummVM <c>-b</c>, 0 for a normal start): COMI's boot script
    /// jumps straight to the room it names, without the flags, inventory or music playing there would have set.
    /// </summary>
    public int BootParam { get; set; }

    /// <summary>Boots the game (once; <see cref="Stop"/> pauses it where it is and Play carries on; <see cref="Reset"/> ends it).</summary>
    public void Play()
    {
        if (Vm == null || IsPlaying) return;
        IsPlaying = true;
        if (!_booted)
        {
            _booted = true;
            Step(() => Vm.Boot(BootParam));
        }
        else if (_videoClock != null)
        {
            _videoClock.Play();
            _videoVoice?.Paused = false;
        }
        MusicOutput?.Paused = false;
        Effects?.Paused = false;
        Voice?.Paused = false;
        Changed?.Invoke();
    }

    public void Stop()
    {
        if (!IsPlaying) return;
        IsPlaying = false;
        _videoClock?.Pause();
        _videoVoice?.Paused = true;
        MusicOutput?.Paused = true;
        Effects?.Paused = true;
        Voice?.Paused = true;
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
        Loop?.Mouse = ((int)Math.Floor(CursorPosition.X - page.X), (int)Math.Floor(CursorPosition.Y - page.Y));
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

    /// <summary>
    /// The quick save's file (F6 and F4): in the saves folder under the app's data, one folder per game, so it
    /// outlasts the app.
    /// </summary>
    public string QuickSavePath => Path.Combine(CorePaths.Root, "saves",
        GameFile == null ? "unnamed" : Path.GetFileNameWithoutExtension(GameFile).ToLowerInvariant(), "quick.mmucs-save");

    /// <summary>Whether there's a quick save to load.</summary>
    public bool HasQuickSave => File.Exists(QuickSavePath);

    /// <summary>
    /// A quick save (F6): the whole engine written to <see cref="QuickSavePath"/>, with the music, the line's
    /// speech and the effects playing, and where they are (<see cref="WriteSave"/>). Not during a video (the engine is in
    /// the middle of a frame). Returns whether it was written.
    /// </summary>
    public bool QuickSave()
    {
        if (!_booted || Vm == null || IsVideoPlaying || _engine.IsParked) return false;
        try
        {
            string path = QuickSavePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".new";
            StateFile.Summary summary = null;
            Exception failed = null;
            Step(() =>
            {
                try
                {
                    using var file = File.Create(temp);
                    summary = WriteSave(file, Vm, Music, Voice, Effects);
                }
                catch (Exception ex) { failed = ex; } // a failed save leaves the game running
            });
            if (failed != null) throw failed;
            if (summary == null) return false;
            File.Move(temp, path, overwrite: true);
            Log.Info($"Game: quick save in room {CurrentRoom}: {summary.Objects} objects, {summary.Bytes / 1024} KiB, to {path}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Game: quick save failed: {ex}");
            return false;
        }
    }

    /// <summary>
    /// Loads the quick save (F4): the engine as it was, and the music, speech and effects carried on from where
    /// they were. The file stays, so it can be loaded again. Returns whether it was loaded.
    /// </summary>
    public bool QuickLoad() => HasQuickSave && LoadFile(QuickSavePath);

    /// <summary>
    /// Loads a save file written by <see cref="QuickSave"/> (or <see cref="WriteSave"/>), as <see cref="QuickLoad"/>
    /// does. Not during a video. Returns whether it was loaded.
    /// </summary>
    public bool LoadFile(string path)
    {
        if (Vm == null || IsVideoPlaying || _engine.IsParked) return false;
        try
        {
            var missing = new System.Collections.Generic.List<string>();
            bool loaded = false;
            Exception failed = null;
            Step(() =>
            {
                try
                {
                    using var file = File.OpenRead(path);
                    ReadSave(file, Loop, Effects, Voice, Music, missing);
                    loaded = true;
                }
                catch (Exception ex) { failed = ex; }
            });
            if (failed != null) throw failed;
            if (!loaded) return false;
            foreach (var what in missing.Distinct()) Log.Warn($"Game: loading {path}: {what} isn't there any more");
            _booted = true; // after a Stop, the save goes on in the fresh engine rather than a new boot
            _viewRoom = -1; // the camera shown goes straight to the loaded one
            Log.Info($"Game: loaded {path} into room {CurrentRoom}");
            Changed?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Game: loading {path} failed: {ex}");
            return false;
        }
    }

    /// <summary>A save as a file: the engine, the music's state and track, the line's speech and the effects playing, and where they were.</summary>
    public static StateFile.Summary WriteSave(Stream stream, ScriptVm vm, ComiMusic music, VoicePlayer voice, SoundEffects effects) =>
        StateFile.Write(stream, new object[] { vm, music?.Save(), voice?.Place, effects?.Save() });

    /// <summary>
    /// Loads a save written by <see cref="WriteSave"/> into the loop's engine: the music, speech and effects carry on
    /// from where they were. What the game no longer has goes in <paramref name="missing"/>.
    /// </summary>
    public static void ReadSave(Stream stream, FrameLoop loop, SoundEffects effects, VoicePlayer voice, ComiMusic music,
        System.Collections.Generic.List<string> missing = null)
    {
        if (StateFile.Read(stream, loop.Vm, missing) is not object[] { Length: >= 3 } parts || parts[0] is not ScriptVm saved)
            throw new InvalidDataException("not a game save");
        loop.LoadState(saved);
        if (parts.Length > 3 && parts[3] is System.Collections.Generic.List<SoundEffects.Saved> playing) effects?.Restore(playing);
        else effects?.StopAll();
        voice?.Stop();
        if (parts[2] is SpeechPlace speech) voice?.Resume(speech);
        if (parts[1] is ComiMusic.Saved savedMusic) music?.Restore(savedMusic);
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
        if (_quitRequested)
        {
            QuitIfAsked();
            return;
        }
        var before = Camera;
        EaseCamera();
        if (ran || IsVideoPlaying || Camera != before || (SmoothCamera && ActorsSliding())) Changed?.Invoke();
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
        QuitIfAsked();
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _videoVoice?.Stop();
        MusicOutput?.FadeOut(0);
        Effects?.StopAll();
        Voice?.Stop();
        _engine.Dispose();
    }

    /// <summary>
    /// Where the mouse may go across the screen: the screen itself, and beside it as far as the canvas shows the
    /// room (the camera shown keeps a wide room's ends on the canvas, past the 640 screen), so the exits at a
    /// room's ends can be pointed at; a 640 room keeps 0–639.
    /// </summary>
    private (int Min, int Max) MouseRange()
    {
        int side = (int)Math.Ceiling(GameState.PageOrigin.X); // the canvas shows part of a pixel more on each side
        if (CurrentRoom == 0) return (0, 639);
        int left = Loop.ScreenOrigin.X, width = Vm.State[EngineVars.RoomWidth];
        return (Math.Min(0, Math.Max(-side, -left)), Math.Max(639, Math.Min(639 + side, width - 1 - left)));
    }

    /// <summary>Whether the cursor is over the page (the scripts' screen) rather than the canvas's sides.</summary>
    public bool IsCursorOnPage => _mouseInside;
}
