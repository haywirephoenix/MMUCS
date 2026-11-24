using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MMUCS.Core.Diagnostics;
using MMUCS.Core.Imaging;
using MMUCS.Core.Media;
using MMUCS.Core.Scumm;
using MMUCS.Player.Rooms;
using MMUCS.Player.Text;
using MMUCS.Runtime;
using SkiaSharp;

namespace MMUCS.Player;

/// <summary>
/// Shows a <see cref="ScriptedGame"/>: <see cref="Capture"/> takes a <see cref="GameFrame"/> of it on the thread
/// that drives the game. The room is drawn as the engine draws it (<see cref="RoomScene"/>), from the room's
/// object slots as the scripts left them, placed by the camera; a new scene is built in the background when
/// the room or what it shows changes (<see cref="Changed"/> says when one is ready). The cursor is the object
/// image the scripts chose (<c>cursor image</c>), shown while the cursor is on and the game has the mouse.
/// The actors are drawn as the engine drew them in its last frame (<see cref="MMUCS.Runtime.Scripts.ActorTable.Pictures"/>).
/// While a script's video plays, its frames are decoded in the background and shown on the page instead,
/// with their text in <see cref="Text"/>.
/// </summary>
public sealed class ScriptedView
{
    private readonly Dictionary<int, RoomModel> _rooms = [];
    private readonly Dictionary<(int Object, int Image), RgbaImage> _cursors = [];
    private readonly HashSet<(int, int)> _cursorsLoading = [];
    private readonly Dictionary<string, (SKImage Image, int OriginX, int OriginY)?> _actorImages = [];
    private string _sceneKey, _pendingKey;
    private RoomScene _scene;
    private int _sceneRoom = -1;
    private CutsceneFrame _cutscene;
    private SmushPlayer _cutsceneOf;
    private int _wantedFrame = -1, _shownFrame = -1;
    private bool _decoding;

    /// <summary>What a video's text (subtitles, credits) is drawn with; none without.</summary>
    public SubtitleRenderer Text { get; set; }

    public ScriptedView(ScriptedGame game) => Game = game;

    public ScriptedGame Game { get; }

    /// <summary>A room scene or cursor image finished loading: capture again.</summary>
    public event Action Changed;

    /// <summary>The scene on show, or null before the first one is built.</summary>
    public RoomScene Scene => _scene;

    /// <summary>A scene being built for what the game shows now (tests wait on it).</summary>
    public Task Building { get; private set; } = Task.CompletedTask;

    /// <summary>The cursor image being loaded, if any (tests wait on it).</summary>
    public Task CursorLoading { get; private set; } = Task.CompletedTask;

    /// <summary>What the game shows now, ready to draw on any thread.</summary>
    public GameFrame Capture()
    {
        var game = Game;
        if (!game.IsLoaded) return new GameFrame { IsRunning = false };
        if (game.IsVideoPlaying)
        {
            // A video covers everything (black while it loads).
            UpdateCutscene(game.Video);
            _cutscene?.Retain();
            return new GameFrame { IsRunning = game.IsPlaying, Cutscene = _cutscene, Room = RoomScene.Blank };
        }
        UpdateCutscene(null);
        UpdateScene();
        return new GameFrame
        {
            IsRunning = game.IsPlaying,
            Room = _sceneRoom == game.CurrentRoom && game.CurrentRoom != 0 ? _scene.WithActors(SceneActors(_scene)) : null,
            Camera = game.Camera,
            Cursor = CursorFrame(),
        };
    }

    /// <summary>The video frames being decoded (tests wait on it).</summary>
    public Task Decoding { get; private set; } = Task.CompletedTask;

    /// <summary>Keeps decoding the frame the video's clock is at; drops the picture when the video is over.</summary>
    private void UpdateCutscene(SmushPlayer video)
    {
        if (!ReferenceEquals(video, _cutsceneOf))
        {
            _cutscene?.Release();
            _cutscene = null;
            _cutsceneOf = video;
            _shownFrame = -1;
        }
        if (video == null) return;
        _wantedFrame = video.FrameAt(Game.VideoTime);
        if (!_decoding && _wantedFrame != _shownFrame) Decoding = Decode(video);
    }

    /// <summary>Decodes the wanted frame on a worker, one at a time, going on while a newer one is wanted.</summary>
    private async Task Decode(SmushPlayer video)
    {
        _decoding = true;
        try
        {
            while (ReferenceEquals(video, _cutsceneOf) && _wantedFrame >= 0 && _wantedFrame != _shownFrame)
            {
                int index = _wantedFrame;
                bool havePicture = _cutscene != null;
                var text = Text;
                // Null when the picture didn't change (held shots, frames that only play sound).
                var picture = await Task.Run(() =>
                {
                    video.ShowFrame(index);
                    return havePicture && !video.PictureChanged ? null : CutsceneFrame.From(video, text);
                });
                if (!ReferenceEquals(video, _cutsceneOf))
                {
                    picture?.Release();
                    return;
                }
                if (picture != null)
                {
                    _cutscene?.Release();
                    _cutscene = picture;
                }
                _shownFrame = index;
                if (picture != null) Changed?.Invoke();
            }
        }
        catch (Exception ex) { Log.Warn($"Game video frame {_wantedFrame}: {ex.Message}"); }
        finally { _decoding = false; }
    }

    /// <summary>Starts a new scene when the room, or a drawn object or its image, changed.</summary>
    private void UpdateScene()
    {
        var vm = Game.Vm;
        int room = vm.CurrentRoom;
        if (room == 0) return;
        var drawn = new Dictionary<int, int>();
        foreach (var o in vm.RoomObjects)
            if (vm.IsDrawn(o.Id)) drawn[o.Id] = vm.LocalState(o.Id);
        string key = room + ":" + string.Join(",", drawn.Select(kv => $"{kv.Key}/{kv.Value}"));
        if (key == _sceneKey || key == _pendingKey) return;
        if (RoomModelOf(room) is not { } model) return;

        _pendingKey = key;
        var scripts = Game.Scripts;
        int StateOf(RoomObject o) => drawn.GetValueOrDefault(scripts.ObjectId(o.Name));
        Building = Build(model, StateOf, key, room);
    }

    private async Task Build(RoomModel model, Func<RoomObject, int> stateOf, string key, int room)
    {
        try
        {
            var scene = await RoomSceneBuilder.BuildAsync(model, stateOf);
            if (_pendingKey != key) return; // something newer is on its way
            _scene = scene;
            _sceneRoom = room;
            _sceneKey = key;
            _pendingKey = null;
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warn($"Game room {room}: {ex.Message}");
            if (_pendingKey == key) _pendingKey = null;
            _sceneKey = key; // don't try the same again every frame
        }
    }

    /// <summary>The actors as the engine drew them in the last frame, each frame rendered once and kept while it's shown.</summary>
    private List<SceneActor> SceneActors(RoomScene scene)
    {
        if (_actorImages.Count > 512) _actorImages.Clear();
        return RoomSceneBuilder.SceneActors(Game.Vm.Actors.Pictures, scene.ZMasks.Count, _actorImages);
    }

    private RoomModel RoomModelOf(int room)
    {
        if (_rooms.TryGetValue(room, out var model)) return model;
        if (Game.Scripts.RoomBlock(room) is { } lflf && RoomModel.TryCreate(lflf, out model)) return _rooms[room] = model;
        return _rooms[room] = null;
    }

    /// <summary>
    /// The cursor's image: image <i>frame</i> of the object the scripts named, from its room. It's held at the
    /// hotspot the scripts set, or at its centre while they've set none.
    /// </summary>
    private FrameImage? CursorFrame()
    {
        var game = Game;
        var vm = game.Vm;
        if (!game.IsMouseCaptured || vm.CursorLevel <= 0) return null;
        var (obj, room, image) = vm.CursorImage;
        if (obj <= 0) return null;
        var key = (obj, image);
        RgbaImage rgba;
        lock (_cursors)
        {
            if (!_cursors.TryGetValue(key, out rgba))
            {
                if (_cursorsLoading.Add(key)) CursorLoading = LoadCursor(obj, room, image);
                return null;
            }
        }
        if (rgba == null) return null;
        var hotspot = vm.CursorHotspot is { X: 0, Y: 0 } ? new GamePoint(rgba.Width / 2.0, rgba.Height / 2.0) : new GamePoint(vm.CursorHotspot.X, vm.CursorHotspot.Y);
        return new FrameImage(SkiaImages.Of(rgba), new GameRect(game.CursorPosition - hotspot, new GameSize(rgba.Width, rgba.Height)));
    }

    private async Task LoadCursor(int obj, int room, int image)
    {
        RgbaImage rgba = null;
        try
        {
            string name = RoomScripts.ObjectName(Game.Root, obj);
            if (RoomModelOf(room) is { } model && model.Objects.FirstOrDefault(o => o.Name == name) is { } ro)
                rgba = await RoomSceneBuilder.ObjectImageAsync(model, ro, Math.Max(0, image - 1));
        }
        catch (Exception ex) { Log.Warn($"Game cursor {obj}: {ex.Message}"); }
        lock (_cursors) _cursors[(obj, image)] = rgba;
        Changed?.Invoke();
    }
}
