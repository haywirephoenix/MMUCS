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
    private string _askedMessage;
    private int _messageAnswer;

    /// <summary>A system message waiting for a key (the F1 menu's "overwrite this game?"), or null; the game stops meanwhile.</summary>
    public string SystemMessage { get; private set; }
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

    private volatile bool _musicMods = true;

    /// <summary>
    /// Whether the music mods beside the game play in place of its tracks (on by default). Switching it reloads
    /// the track playing, so a mod's gives way to the game's own (or the other way) at once.
    /// </summary>
    public bool MusicModsEnabled
    {
        get => _musicMods;
        set
        {
            if (_musicMods == value) return;
            _musicMods = value;
            MusicOutput?.Reload();
        }
    }
    private bool _mouseInside, _booted, _quitRequested;

    /// <summary>Something that's shown changed.</summary>
    public event Action Changed;

    public ScummBlock Root { get; private set; }
    public ScriptStore Scripts { get; private set; }
    public ScriptVm Vm { get; private set; }
    public FrameLoop Loop { get; private set; }

    public bool IsLoaded => Vm != null;

    /// <summary>
    /// Where the inputs that reach the engine are written (the game window's <c>--record</c>), or null. Set it
    /// before the game boots, so its jiffies count from boot as mprobe's do.
    /// </summary>
    public InputRecording Recorder { get; set; }

    /// <summary>Logs more as it happens (the game window's <c>--verbose</c>): rooms, videos, sounds, temporary saves, inputs, each with the game's time.</summary>
    public bool Verbose { get; set; }

    private System.Collections.Generic.List<InputRecording.Event> _replay;
    private System.Collections.Generic.Queue<InputRecording.Event> _answers;
    private int _replayAt;

    /// <summary>
    /// Whether a recording is playing (<see cref="StartReplay"/>): its inputs drive the game and the host's are
    /// ignored until it ends.
    /// </summary>
    public bool IsReplaying => _replay != null;

    /// <summary>The replay reached its end (or the game was reset); the host has the game again.</summary>
    public event Action ReplayEnded;

    /// <summary>
    /// Plays a recording's inputs (<see cref="InputRecording.Read"/>) into the game at the jiffies they were
    /// recorded at, as mprobe boot's <c>--replay</c> does: the pointer, buttons, keys, answers to system messages,
    /// skipped videos and loaded saves. Start it before the game boots, so the jiffies count from boot.
    /// </summary>
    public void StartReplay(System.Collections.Generic.IEnumerable<InputRecording.Event> events)
    {
        var all = events.ToList();
        _answers = new(all.Where(e => e.Kind == "answer"));
        _replay = [.. all.Where(e => e.Kind != "answer")];
        _replayAt = 0;
    }

    private void EndReplay(string why)
    {
        if (_replay == null) return;
        int left = _replay.Count - _replayAt;
        Log.Info($"Game: replay {why} at {When.Trim()}" +
                 (left > 1 || _answers.Count > 0 ? $", with {left} inputs and {_answers.Count} answers not played (it went its own way)" : "") +
                 ": the game is yours");
        _replay = null;
        _answers = null;
        ReplayEnded?.Invoke();
        Changed?.Invoke();
    }

    /// <summary>The replay's inputs due by now, before the jiffy's frame (mprobe's order).</summary>
    private void PlayReplay()
    {
        for (; _replay != null && _replayAt < _replay.Count && _replay[_replayAt].Jiffy <= Loop.Jiffies; _replayAt++)
        {
            var e = _replay[_replayAt];
            switch (e.Kind)
            {
                case "move":
                    Loop.Mouse = (e.X, e.Y);
                    var page = GameState.PageOrigin;
                    CursorPosition = new GamePoint(page.X + e.X + 0.5, page.Y + e.Y + 0.5);
                    _mouseInside = e.X >= 0 && e.X < GameState.PageSize.Width;
                    break;
                case "press":
                    Trace($"REPLAY press {(e.Button == MouseButton.Left ? "left" : "right")} {Loop.Mouse.X},{Loop.Mouse.Y}");
                    Loop.Press(e.Button);
                    break;
                case "release":
                    Loop.Release(e.Button);
                    break;
                case "key":
                    Trace($"REPLAY key {e.X}");
                    Loop.PressKey(e.X);
                    break;
                case "key-up":
                    Loop.ReleaseKey(e.X);
                    break;
                case "skip-video":
                    Log.Warn($"Game: replay: a video was skipped at {e.Jiffy / 60.0:0.000} s, but none is playing");
                    break;
                case "load":
                    if (!LoadFile(e.File)) Log.Warn($"Game: replay: couldn't load {e.File}");
                    break;
                case "end":
                    _replayAt++;
                    EndReplay("finished");
                    return;
            }
        }
        if (_replay != null && _replayAt >= _replay.Count) EndReplay("finished (no end mark: the recording was cut short)");
    }

    /// <summary>The game's time and frame, as mprobe boot shows them.</summary>
    public string When => Loop == null ? "" : $"{Loop.Jiffies / 60.0,7:0.000} s f{Loop.Frames,-4}";

    private void Trace(string text)
    {
        if (Verbose) Log.Info($"{When} {text}");
    }
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
        double view = IsPageOnly ? GameState.PageSize.Width : GameState.CanvasWidth;
        return new(Within(at.X, width, view), Within(at.Y, height, GameState.CanvasHeight));

        static double Within(double centre, int room, double view) =>
            room <= view ? room / 2.0 : Math.Clamp(centre, view / 2, room - view / 2);
    }

    /// <summary>
    /// Whether the room in play is seen only through the 640 page (<see cref="ScriptVm.PageOnlyRooms"/>: the
    /// banjo duel's two halves): the camera shown keeps just the page in the room, the picture is cut to the page
    /// and the mouse stays on it.
    /// </summary>
    public bool IsPageOnly => Vm != null && Vm.IsPageOnly;

    /// <summary>Where the engine's camera is (ScummVM's stepping, what the scripts see).</summary>
    public GamePoint EngineCamera => Vm == null ? new(320, 240) : new(Vm.CameraX, Vm.CameraY);

    /// <summary>
    /// A smooth camera instead of the engine's steps. The engine's camera (<see cref="CameraTarget"/>) slides
    /// from where the frame before last had it to where the last frame did, through the wait for the next frame,
    /// and what's shown eases towards that (half the way in <see cref="SmoothCameraHalfLife"/> jiffies), which
    /// softens its starts and stops. Both are worked out for the moment the picture is drawn
    /// (<see cref="Present"/>), between ticks too, so a picture drawn off the 60 Hz of the jiffies still moves
    /// evenly. It snaps on a room change or a jump of more than <see cref="SmoothCameraSnap"/> pixels in a frame.
    /// Only the picture and the mouse follow it; the scripts still see the engine's camera.
    /// </summary>
    public bool SmoothCamera { get; set; }

    /// <summary>
    /// With the smooth camera, walking actors glide between frames all the time, not only while the camera
    /// moves (their feet slide a little, as the walk only changes cels each frame). Without the smooth camera
    /// they step either way, as gliding over a stepping camera would wobble against the room.
    /// </summary>
    public bool SmoothWalk { get; set; }

    /// <summary>
    /// How far to slide actors from where the last frame drew them to where this one does: with the smooth
    /// camera while the camera moves (or all the time with <see cref="SmoothWalk"/>), the way through the wait
    /// for the next frame, so walking glides with the camera instead of stepping against it; otherwise 1, where
    /// the engine has them, so feet stay planted while the room holds still.
    /// </summary>
    public double ActorSlide => SmoothCamera && _slideActors && Loop != null ? Loop.ProgressAt(_sinceTick) : 1;

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
    /// <summary>How far (room pixels) the camera shown may be from where it slides from and still count as still.</summary>
    public const double SlideThreshold = 0.5;
    public const int SmoothCameraSnap = 100;

    private double _viewX = 320, _viewY = 240;
    private int _viewRoom = -1;
    /// <summary>The engine's camera (kept in the room) after the frame before last and after the last frame: what slides.</summary>
    private GamePoint _slideFrom = new(320, 240), _slideTo = new(320, 240);
    /// <summary>Whether the actors slide through this frame: <see cref="SmoothWalk"/>, or the camera shown was moving when it ran.</summary>
    private bool _slideActors;
    /// <summary>How far into the jiffy after the last tick the picture is drawn, and the game time it was last drawn at.</summary>
    private double _sinceTick, _presentedAt;

    /// <summary>After a tick: a frame that ran moves on what slides; a room change, a jump or no smoothing snaps.</summary>
    private void SlideCamera(bool frameRan)
    {
        var target = CameraTarget;
        bool snap = !SmoothCamera || _viewRoom != CurrentRoom;
        if (!frameRan && !snap) return;
        snap |= Math.Abs(target.X - _slideTo.X) > SmoothCameraSnap || Math.Abs(target.Y - _slideTo.Y) > SmoothCameraSnap;
        _slideFrom = snap ? target : _slideTo;
        _slideTo = target;
        _slideActors = !snap && (SmoothWalk || _slideFrom != _slideTo || Math.Abs(_viewX - _slideFrom.X) > SlideThreshold || Math.Abs(_viewY - _slideFrom.Y) > SlideThreshold);
        _sinceTick = 0;
        if (snap) (_viewX, _viewY, _viewRoom) = (target.X, target.Y, CurrentRoom);
    }

    /// <summary>
    /// The host is about to draw, <paramref name="sinceTick"/> of a jiffy after the last tick: the camera shown
    /// eases on to that moment. True when the picture moves (the camera, or sliding actors), so it's worth drawing.
    /// </summary>
    public bool Present(double sinceTick)
    {
        if (Vm == null || Loop == null) return false;
        _sinceTick = Math.Clamp(sinceTick, 0, 1);
        double now = Loop.Jiffies + _sinceTick, dt = Math.Clamp(now - _presentedAt, 0, 6);
        _presentedAt = now;
        if (!SmoothCamera) return false;
        double t = Loop.ProgressAt(_sinceTick);
        double x = _slideFrom.X + (_slideTo.X - _slideFrom.X) * t, y = _slideFrom.Y + (_slideTo.Y - _slideFrom.Y) * t;
        double oldX = _viewX, oldY = _viewY;
        double f = 1 - Math.Pow(0.5, dt / SmoothCameraHalfLife);
        _viewX += (x - _viewX) * f;
        _viewY += (y - _viewY) * f;
        if (Math.Abs(x - _viewX) < 0.05) _viewX = x;
        if (Math.Abs(y - _viewY) < 0.05) _viewY = y;
        return _viewX != oldX || _viewY != oldY || (_slideActors && ActorsSliding());
    }

    public int CurrentRoom => Vm?.CurrentRoom ?? 0;

    /// <summary>What's happening, for a status line.</summary>
    public string Status => Vm == null ? "Open the game first."
        // : !IsPlaying ? "Press Play to boot the game."
        : $"room {CurrentRoom} {Scripts.RoomName(CurrentRoom)} · {Loop.Frames} frames · {Running} scripts" +
          (IsVideoPlaying ? $" · video {VideoName}{(Video == null ? " (loading)" : $" {VideoTime:m\\:ss} / {Video.Duration:m\\:ss}")}" : "") +
          (MusicOutput?.Current is { } track ? $" · music {track.File}{(MusicOutput.Replacement is { } mod ? $" ({System.IO.Path.GetFileName(mod)})" : "")}" : "") +
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
        Effects.Started += id => Trace($"SOUND {id} starts");
        Voice = new VoicePlayer(VoicePlayer.FromBundles(gameFile)) { Mixer = _mixer };
        // The game's fonts, for get-string-width (the scripts centre and fit text with it).
        Fonts = gameFile == null ? [] : MMUCS.Core.Fonts.NutFont.LoadAll(gameFile);
        if (MusicTable.Load(gameFile) is { } table)
        {
            MusicOutput = new MixerMusic(MixerMusic.WithMods(gameFile, MixerMusic.FromBundles(gameFile), () => _musicMods)) { Mixer = _mixer };
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
        // A system message: the engine thread waits here for the key that answers it.
        Vm.AskSystem = text =>
        {
            if (!_engine.IsEngineThread) return 0;
            _askedMessage = text;
            _engine.Park();
            return _messageAnswer;
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
        Vm.RoomEntered += room => Trace(room == 0 ? "ROOM 0 (no room)" : $"ROOM {room} {Scripts.RoomName(room)}");
        Vm.VideoStarted += name => Trace($"VIDEO {name}");
        Vm.Warned += w => Trace($"warning: {w}");
        Loop.StateSaved += () => Trace("SAVE temporary");
        Loop.StateLoaded += () => Trace($"LOAD temporary: room {Vm.CurrentRoom}");
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
        var snapshot = Snapshot = FollowSaves(Loop, effects, voice, music);
        Loop.StateSaved += () => snapshot.Thumbnail = Thumbnail();
        Saves = GameFile == null ? null : new GameSaves(GameSaves.FolderFor(GameFile));
        Saves?.Attach(Loop, snapshot, effects, voice, music);
        if (Loop.LoadSlot is { } loadSlot)
            Loop.LoadSlot = slot =>
            {
                bool loaded = loadSlot(slot);
                if (loaded) _viewRoom = -1; // the camera shown goes straight to the loaded one
                return loaded;
            };
        Loop.StateLoaded += () => _viewRoom = -1; // the camera shown goes straight to the loaded one
        // Quitting waits for the engine's step to end (it's in the middle of a script).
        Vm.QuitRequested += () => _quitRequested = true;
        // cursor put: the frame loop has moved the engine's mouse; the cursor follows, and the host's pointer after the step.
        Vm.MouseWarped += (_, _) =>
        {
            var page = GameState.PageOrigin;
            CursorPosition = new GamePoint(page.X + Loop.Mouse.X + 0.5, page.Y + Loop.Mouse.Y + 0.5);
            _mouseInside = true;
            _warped = true;
        };
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
    /// What the scripts' temporary save (the F1 menu's) keeps beside the engine, for a save slot to write with
    /// it: the sounds as they were, and the thumbnail of the last frame before the menu.
    /// </summary>
    public sealed class SnapshotExtras
    {
        public ComiMusic.Saved Music { get; internal set; }
        public SpeechPlace? Speech { get; internal set; }
        public System.Collections.Generic.List<SoundEffects.Saved> Effects { get; internal set; }

        /// <summary>The page as it looked, as a PNG of <see cref="ThumbnailSize"/>, or null when the host draws none.</summary>
        public byte[] Thumbnail { get; set; }
    }

    /// <summary>A save's thumbnail, as the F1 menu draws it.</summary>
    public const int ThumbnailWidth = 140, ThumbnailHeight = 105;

    /// <summary>
    /// The host's thumbnail of the page as last drawn (a PNG of <see cref="ThumbnailWidth"/>×<see cref="ThumbnailHeight"/>),
    /// taken when the F1 menu saves (before it draws itself) and by <see cref="QuickSave"/>. Null: saves have none.
    /// </summary>
    public Func<byte[]> TakeThumbnail { get; set; }

    /// <summary>What the F1 menu's snapshot keeps beside the engine (its thumbnail is slot 0's, the game about to be saved).</summary>
    public SnapshotExtras Snapshot { get; private set; }

    /// <summary>
    /// The scripts' temporary save (the F1 menu's) for the sounds, which live outside the engine. As ScummVM's
    /// iMUSE keeps its tracks in a save, a save keeps the music (its state and the track playing, and where)
    /// and the line's speech (and where); a load stops the effects and puts the music and the speech back
    /// where they were, so the menu's silence in between is a pause. The effects playing are kept too, for a
    /// save slot written from the menu (<see cref="GameSaves"/>).
    /// </summary>
    public static SnapshotExtras FollowSaves(FrameLoop loop, SoundEffects effects, VoicePlayer voice, ComiMusic music)
    {
        var saved = new SnapshotExtras();
        loop.StateSaved += () =>
        {
            saved.Music = music?.Save();
            saved.Speech = voice?.Place;
            saved.Effects = effects?.Save();
        };
        loop.StateLoaded += () =>
        {
            effects?.StopAll();
            voice?.Stop();
            if (saved.Speech is { } speech) voice?.Resume(speech);
            if (saved.Music != null) music?.Restore(saved.Music);
        };
        return saved;
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
        _askedMessage = null;
        SystemMessage = null;
        Video = null;
        VideoName = null;
        Effects?.StopAll();
        Voice?.Stop();
        MusicOutput?.FadeOut(0);
        if (_engine.IsParked)
        {
            // A step parked in start-video or a system message can't be carried on with a new engine: give it a thread of its own.
            _engine.Dispose();
            _engine = new EngineThread();
        }
        Faults = 0;
        LastFault = null;
        EndReplay("stopped (the game was reset)");
        if (Recorder != null && Loop != null)
        {
            // The recording counts jiffies from one boot; the next boot starts a new run.
            Recorder.Comment("the game was reset here: the replay ends");
            Recorder.End(Loop.Jiffies);
            Log.Info($"Game: recording ended (the game was reset): {Recorder.Path}");
            Recorder = null;
        }
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
            Release(MouseButton.Left);
            Release(MouseButton.Right);
        }
        Changed?.Invoke();
    }

    /// <summary>Moves the cursor (clamped to the canvas); the scripts see it on the page.</summary>
    public void MoveCursor(GamePoint position)
    {
        if (IsReplaying) return; // the replay has the pointer
        CursorPosition = new GamePoint(Math.Clamp(position.X, 0, GameState.CanvasWidth), Math.Clamp(position.Y, 0, GameState.CanvasHeight));
        var page = GameState.PageOrigin;
        _mouseInside = CursorPosition.X >= page.X && CursorPosition.X < page.X + GameState.PageSize.Width;
        Loop?.Mouse = ((int)Math.Floor(CursorPosition.X - page.X), (int)Math.Floor(CursorPosition.Y - page.Y));
        Changed?.Invoke();
    }

    public void Press(MouseButton button)
    {
        if (!IsMouseCaptured || IsReplaying) return;
        if (SystemMessage != null) Answer(0);
        else if (Loop != null)
        {
            Recorder?.Press(Loop.Jiffies, Loop.Mouse, button);
            Loop.Press(button);
        }
    }

    public void Release(MouseButton button)
    {
        if (Loop == null || IsReplaying) return;
        Recorder?.Release(Loop.Jiffies, Loop.Mouse, button);
        Loop.Release(button);
    }

    /// <summary>A key went down, as the engine numbers it (ASCII; F1–F9 are 315–323); it's held until <see cref="ReleaseKey"/>.</summary>
    public void PressKey(int code)
    {
        if (!IsPlaying || IsReplaying) return;
        if (SystemMessage != null) Answer(code);
        else if (Loop != null)
        {
            Recorder?.Key(Loop.Jiffies, code);
            Loop.PressKey(code);
        }
    }

    /// <summary>A key came up (the scripts ask whether one is held: the cannon's and the ship's Tab and arrows).</summary>
    public void ReleaseKey(int code)
    {
        if (Loop == null || IsReplaying) return;
        Recorder?.KeyUp(Loop.Jiffies, code);
        Loop.ReleaseKey(code);
    }

    /// <summary>Every key lets go (the window lost the keyboard).</summary>
    public void ReleaseKeys()
    {
        if (Loop == null || IsReplaying) return;
        Loop.ReleaseKeys();
    }

    /// <summary>Whether <paramref name="code"/> is the game's pause key now (space, but not where the scripts turned it off, as under water).</summary>
    public bool IsPauseKey(int code) => Vm != null && code > 0 && code == Vm.State[EngineVars.PauseKey];

    /// <summary>
    /// The next voice mode (ScummVM's Ctrl+T): speech only, speech and subtitles, subtitles only, round again. Kept in
    /// the registry as the F1 menu keeps it. Returns the new mode, or -1 before the game is running.
    /// </summary>
    public int CycleVoiceMode()
    {
        if (Vm == null || !_booted) return -1;
        int mode = (Math.Clamp(Vm.State[EngineVars.VoiceMode], 0, 2) + 1) % 3;
        Vm.State[EngineVars.VoiceMode] = mode;
        Vm.WriteRegistry?.Invoke("Text Status", mode);
        Changed?.Invoke();
        return mode;
    }

    /// <summary>A script moved the mouse (<c>cursor put</c>; the cannon keeps it in the middle): the host puts the system pointer at <see cref="CursorPosition"/>.</summary>
    public event Action MouseWarped;
    private bool _warped;

    /// <summary>
    /// The quick save's file (F6 and F4): in the saves folder under the app's data, one folder per game, so it
    /// outlasts the app.
    /// </summary>
    public string QuickSavePath => Path.Combine(GameFile == null ? Path.Combine(CorePaths.Root, "saves", "unnamed") : GameSaves.FolderFor(GameFile),
        "quick.mmucs-save");

    /// <summary>The game's save slots and registry settings (the F1 menu's), or null without a game file.</summary>
    public GameSaves Saves { get; private set; }

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
            var thumbnail = Thumbnail();
            Step(() =>
            {
                try
                {
                    using var file = File.Create(temp);
                    summary = WriteSave(file, Vm, Music, Voice, Effects, "Quick save", thumbnail);
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
            Recorder?.Load(Loop.Jiffies, path);
            Changed?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Game: loading {path} failed: {ex}");
            return false;
        }
    }

    /// <summary>The host's thumbnail, or null when it has none or it fails (a save goes on without one).</summary>
    private byte[] Thumbnail()
    {
        try { return TakeThumbnail?.Invoke(); }
        catch (Exception ex)
        {
            Log.Warn($"Game: thumbnail: {ex.Message}");
            return null;
        }
    }

    /// <summary>A save as a file: the engine, the music's state and track, the line's speech and the effects playing, and where they were.</summary>
    public static StateFile.Summary WriteSave(Stream stream, ScriptVm vm, ComiMusic music, VoicePlayer voice, SoundEffects effects, string name = null,
        byte[] thumbnail = null) =>
        WriteSave(stream, vm, music?.Save(), voice?.Place, effects?.Save(), name, thumbnail);

    private const string SaveMagic = "MMUCS-SAVE";

    /// <summary>
    /// A save as a file, from the sounds as they were saved: a header that a slot list reads without the rest
    /// (the name, when, the room and the thumbnail, a PNG), then the state.
    /// </summary>
    public static StateFile.Summary WriteSave(Stream stream, ScriptVm vm, ComiMusic.Saved music, SpeechPlace? speech,
        System.Collections.Generic.List<SoundEffects.Saved> effects, string name, byte[] thumbnail = null)
    {
        using (var header = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            header.Write(SaveMagic);
            header.Write(SaveVersion);
            header.Write(name ?? "");
            header.Write(DateTime.UtcNow.Ticks);
            header.Write(vm.CurrentRoom);
            header.Write(thumbnail?.Length ?? 0);
            if (thumbnail != null) header.Write(thumbnail);
        }
        return StateFile.Write(stream, new object[] { vm, music, speech, effects });
    }

    /// <summary>A save file's header: version 1 has no thumbnail, version 2 adds it.</summary>
    private const int SaveVersion = 2;

    /// <summary>A save file's header, read up to where the state starts (null when it isn't a save; a quick save from before the header has an empty one).</summary>
    private static (string Name, byte[] Thumbnail)? ReadHeader(BinaryReader r, Stream stream)
    {
        long start = stream.Position;
        string magic = r.ReadString();
        if (magic == "MMUCS-STATE")
        {
            stream.Position = start;
            return ("", null);
        }
        if (magic != SaveMagic) return null;
        int version = r.ReadInt32();
        string name = r.ReadString();
        r.ReadInt64();
        r.ReadInt32();
        byte[] thumbnail = null;
        if (version >= 2 && r.ReadInt32() is int length and > 0) thumbnail = r.ReadBytes(length);
        return (name, thumbnail);
    }

    /// <summary>A save file's name and thumbnail (a PNG, or null), or null when it isn't a save.</summary>
    public static (string Name, byte[] Thumbnail)? ReadSaveHeader(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            using var r = new BinaryReader(file);
            return ReadHeader(r, file);
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A save file's name, or null when it isn't one.</summary>
    public static string ReadSaveName(string path) => ReadSaveHeader(path)?.Name;

    /// <summary>
    /// Loads a save written by <see cref="WriteSave"/> into the loop's engine: the music, speech and effects carry on
    /// from where they were. What the game no longer has goes in <paramref name="missing"/>.
    /// </summary>
    public static void ReadSave(Stream stream, FrameLoop loop, SoundEffects effects, VoicePlayer voice, ComiMusic music,
        System.Collections.Generic.List<string> missing = null)
    {
        using (var header = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            if (ReadHeader(header, stream) == null) throw new InvalidDataException("not a game save");
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
        if (SystemMessage != null)
        {
            // A system message stops the game until it's answered: the replay answers as it was answered.
            if (_answers != null && _answers.TryDequeue(out var answer)) Answer(answer.X);
            return;
        }
        if (IsVideoPlaying)
        {
            if (_replay != null && _replayAt < _replay.Count && _replay[_replayAt] is { Kind: "skip-video" } skip && skip.Jiffy <= Loop.Jiffies)
            {
                _replayAt++;
                Trace($"REPLAY skip-video {VideoName}");
                EndVideo(skipped: true);
                return;
            }
            AdvanceVideo();
            return;
        }
        if (_replay != null) PlayReplay();
        bool ran = false;
        Recorder?.Move(Loop.Jiffies, Loop.Mouse);
        Step(() => ran = Loop.Tick());
        if (_quitRequested)
        {
            QuitIfAsked();
            return;
        }
        if (_warped)
        {
            _warped = false;
            if (IsMouseCaptured && !IsReplaying) MouseWarped?.Invoke();
        }
        SlideCamera(ran);
        Present(0); // at the tick; a host drawing between ticks presents again
        if (ran || IsVideoPlaying) Changed?.Invoke();
    }

    /// <summary>Ends the video now; the cutscene it was in is skipped on the next frame.</summary>
    public void SkipVideo()
    {
        if (!IsVideoPlaying || IsReplaying) return;
        Recorder?.SkipVideo(Loop.Jiffies);
        Trace($"VIDEO {VideoName} skipped");
        EndVideo(skipped: true);
    }

    /// <summary>Runs a step on the engine thread; one that parks has started a video.</summary>
    private void Step(Action work)
    {
        try
        {
            if (!_engine.Run(work)) Parked();
        }
        catch (Exception ex)
        {
            Log.Error($"Game engine: {ex}");
            Faults++;
            LastFault = ex.Message;
            IsPlaying = false;
        }
    }

    /// <summary>The engine stopped in the middle of a step: for a system message's key, or to play a video.</summary>
    private void Parked()
    {
        if (_askedMessage != null)
        {
            SystemMessage = _askedMessage;
            _askedMessage = null;
            Log.Info($"Game: system message: {SystemMessage}");
            Changed?.Invoke();
            return;
        }
        BeginVideo();
    }

    /// <summary>The key (or a click, 0) answers the system message, and the engine carries on from it.</summary>
    private void Answer(int key)
    {
        SystemMessage = null;
        _messageAnswer = key;
        if (Loop != null) Recorder?.Answer(Loop.Jiffies, key);
        try
        {
            if (!_engine.Resume()) Parked();
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
            if (!_engine.Resume()) Parked();
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
    /// room's ends can be pointed at; a 640 room keeps 0–639, as does a room seen only through the page.
    /// </summary>
    private (int Min, int Max) MouseRange()
    {
        int side = (int)Math.Ceiling(GameState.PageOrigin.X); // the canvas shows part of a pixel more on each side
        if (CurrentRoom == 0 || IsPageOnly) return (0, 639);
        int left = Loop.ScreenOrigin.X, width = Vm.State[EngineVars.RoomWidth];
        return (Math.Min(0, Math.Max(-side, -left)), Math.Max(639, Math.Min(639 + side, width - 1 - left)));
    }

    /// <summary>Whether the cursor is over the page (the scripts' screen) rather than the canvas's sides.</summary>
    public bool IsCursorOnPage => _mouseInside;
}
