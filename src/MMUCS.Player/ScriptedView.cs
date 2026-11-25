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
using MMUCS.Runtime.Scripts;
using SkiaSharp;

namespace MMUCS.Player;

/// <summary>
/// Shows a <see cref="ScriptedGame"/>: <see cref="Capture"/> takes a <see cref="GameFrame"/> of it on the thread
/// that drives the game. The room is drawn as the engine draws it (<see cref="RoomScene"/>), from the room's
/// object slots as the scripts left them, placed by the camera; a new scene is built in the background when
/// the room or what it shows changes (<see cref="Changed"/> says when one is ready). The cursor is the object
/// image the scripts chose (<c>cursor image</c>), shown while the cursor is on and the game has the mouse.
/// The actors are drawn as the engine drew them in its last frame (<see cref="MMUCS.Runtime.Scripts.ActorTable.Pictures"/>).
/// The room is kept in palette indices (<see cref="IndexedRoom"/>), and it, the actors and the texts are drawn
/// in the colours on the screen (<see cref="ScriptVm.Palette"/>), coloured again when the scripts change them
/// (fades, lights, intensity), as the engine shows everything through its one palette.
/// While a script's video plays, its frames are decoded in the background and shown on the page instead,
/// with their text in <see cref="Text"/>.
/// </summary>
public sealed class ScriptedView(ScriptedGame game)
{
    private readonly Dictionary<int, RoomModel> _rooms = [];
    private readonly Dictionary<(int Object, int Image), RgbaImage> _cursors = [];
    private readonly HashSet<(int, int)> _cursorsLoading = [];
    private readonly Dictionary<string, RoomSceneBuilder.ActorImage> _actorImages = [];
    private readonly Dictionary<ScreenText, SubtitleOverlay> _texts = [];
    private SubtitleRenderer _textsBy;
    private string _sceneKey, _pendingKey;
    private sealed record Built(IndexedRoom Scene, int Room);
    private Built _built; // one reference, so a build on another thread hands both over at once
    private RoomScene _scene;
    private IndexedRoom _sceneOf;
    private int _sceneRoom = -1;
    private byte[] _screen;
    private int _screenVersion = -1;
    private CutsceneFrame _cutscene;
    private SmushPlayer _cutsceneOf;
    private int _wantedFrame = -1, _shownFrame = -1;
    private bool _decoding;

    /// <summary>What a video's text (subtitles, credits) is drawn with; none without.</summary>
    public SubtitleRenderer Text { get; set; }

    public ScriptedGame Game { get; } = game;

    /// <summary>A room scene or cursor image finished loading: capture again.</summary>
    public event Action Changed;

    /// <summary>The scene on show, in the colours on the screen when it was last captured, or null before the first one is built.</summary>
    public RoomScene Scene => _scene;

    /// <summary>The colours the last capture drew in (256 RGB triples), or null before one.</summary>
    public byte[] ScreenColours => _screen;

    /// <summary>A scene being built for what the game shows now (tests wait on it).</summary>
    public Task Building { get; private set; } = Task.CompletedTask;

    /// <summary>The object image being loaded (the cursor's or a blasted object's), if any (tests wait on it).</summary>
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
        UpdateColours();
        return new GameFrame
        {
            IsRunning = game.IsPlaying,
            // Black (not the window's empty stage) in room 0 and while a new room's scene is still being built, as the engine shows it.
            Room = _sceneRoom == game.CurrentRoom && game.CurrentRoom != 0 ? _scene.WithActors(SceneActors(_scene, game.Vm.Actors.Pictures)) : RoomScene.Blank,
            UpperActors = _sceneRoom == game.CurrentRoom && game.CurrentRoom != 0 && game.Vm.Actors.UpperPictures.Count > 0
                ? new RoomScene { Width = _scene.Width, Height = _scene.Height, ZMasks = _scene.ZMasks, Actors = SceneActors(_scene, game.Vm.Actors.UpperPictures) }
                : null,
            Blasts = [.. BlastFrames(), .. StampFrames()],
            Camera = game.Camera,
            TextShift = new(game.EngineCamera.X - game.Camera.X, game.EngineCamera.Y - game.Camera.Y),
            Texts = ScreenTexts(overActor: false),
            RoomTexts = ScreenTexts(overActor: true),
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
                // Voice mode 0 (speech only) shows no subtitles over the videos either (ScummVM's SMUSH channel flag 0).
                var text = Game.Vm?.State[EngineVars.VoiceMode] == 0 ? null : Text;
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
            var scene = await RoomSceneBuilder.BuildIndexedAsync(model, stateOf);
            if (_pendingKey != key) return; // something newer is on its way
            _built = new Built(scene, room);
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

    /// <summary>
    /// Takes the screen's colours when they changed, and colours the room in them when they or the room did.
    /// A change drops the actors' and texts' pictures, which were in the old colours.
    /// </summary>
    private void UpdateColours()
    {
        var palette = Game.Vm.Palette;
        if (_screen == null || palette.Version != _screenVersion)
        {
            _screen = palette.Snapshot();
            _screenVersion = palette.Version;
            _actorImages.Clear();
            _texts.Clear();
            _sceneOf = null;
        }
        if (_built is { } built && !ReferenceEquals(built.Scene, _sceneOf))
        {
            _scene = built.Scene.Colour(_screen);
            _sceneOf = built.Scene;
            _sceneRoom = built.Room;
        }
    }

    /// <summary>
    /// The texts the last frame drew, in the screen's colours and <see cref="Text"/>'s fonts, each made once
    /// while it's shown (old ones are left to the finalizer: a frame may still be drawing one).
    /// </summary>
    private List<SubtitleOverlay> ScreenTexts(bool overActor)
    {
        var texts = Game.Vm.ScreenTexts;
        if (texts.Count == 0 || Text == null) return [];
        if (_screen == null) return [];
        if (!ReferenceEquals(_textsBy, Text) || _texts.Count > 256)
        {
            _texts.Clear();
            _textsBy = Text;
        }
        var list = new List<SubtitleOverlay>(texts.Count);
        foreach (var t in texts)
        {
            if (t.OverActor != overActor) continue;
            if (!_texts.TryGetValue(t, out var overlay))
            {
                var flags = (t.Center ? Core.Smush.SmushTextFlags.Center : 0) | (t.Wrap ? Core.Smush.SmushTextFlags.Wrap : 0);
                overlay = Text.BuildText(new Core.Smush.SmushText { X = t.X, Y = t.Y, Flags = flags, Font = t.Charset, Color = t.Color, Text = t.Text },
                    _screen, (int)GameState.PageSize.Width, (int)GameState.PageSize.Height);
                _texts[t] = overlay;
            }
            if (overlay != null) list.Add(overlay);
        }
        return list;
    }

    /// <summary>The actors as the engine drew them in the last frame, each frame rendered once and kept while it's shown.</summary>
    private List<SceneActor> SceneActors(RoomScene scene, IReadOnlyList<ActorPicture> pictures)
    {
        if (_actorImages.Count > 512) _actorImages.Clear();
        return RoomSceneBuilder.SceneActors(pictures, scene.ZMasks.Count, _actorImages, Game.ActorSlide, _screen, Game.Vm.Palette.ShadowFactors);
    }

    private readonly Dictionary<(int Slot, int Brightness), (object Source, SKImage Image)> _thumbnails = [];

    /// <summary>
    /// The save thumbnails the F1 menu stamped, on the page over its blasted objects: slot 0's from the menu's
    /// snapshot, the others from their save files, dimmed by the stamp's brightness. A slot without one shows
    /// nothing (the menu's frame stays empty).
    /// </summary>
    private List<FrameImage> StampFrames()
    {
        var stamps = Game.Vm.SaveStamps;
        if (stamps.Count == 0) return [];
        var list = new List<FrameImage>(stamps.Count);
        var page = GameState.PageOrigin;
        foreach (var stamp in stamps)
            if (Thumbnail(stamp.Slot, stamp.Brightness) is { } image)
                list.Add(new FrameImage(image, new GameRect(page.X + stamp.X, page.Y + stamp.Y, stamp.Width, stamp.Height)));
        return list;
    }

    /// <summary>A slot's thumbnail at a brightness, decoded once for each save written (its file's time) or snapshot taken.</summary>
    private SKImage Thumbnail(int slot, int brightness)
    {
        object source;
        if (slot == 0) source = Game.Snapshot?.Thumbnail;
        else
        {
            string path = Game.Saves?.SlotPath(slot);
            source = path != null && System.IO.File.Exists(path) ? System.IO.File.GetLastWriteTimeUtc(path) : null;
        }
        var key = (slot, brightness);
        if (source == null) return null;
        if (_thumbnails.TryGetValue(key, out var cached) && Equals(cached.Source, source)) return cached.Image;
        byte[] png = slot == 0 ? (byte[])source : Game.Saves.SlotThumbnail(slot);
        SKImage image = null;
        if (png != null)
        {
            using var decoded = SKImage.FromEncodedData(png);
            if (decoded != null) image = Dimmed(decoded, Math.Clamp(brightness, 0, 255) / 255f);
        }
        // The image replaced isn't disposed: a frame drawn on another thread may still hold it.
        _thumbnails[key] = (source, image);
        return image;
    }

    private static SKImage Dimmed(SKImage image, float brightness)
    {
        using var surface = SKSurface.Create(new SKImageInfo(image.Width, image.Height));
        using var paint = new SKPaint
        {
            ColorFilter = SKColorFilter.CreateColorMatrix([brightness, 0, 0, 0, 0, 0, brightness, 0, 0, 0, 0, 0, brightness, 0, 0, 0, 0, 0, 1, 0]),
        };
        surface.Canvas.DrawImage(image, 0, 0, paint);
        return surface.Snapshot();
    }

    /// <summary>
    /// The objects the last frame blasted, on the page, each image loaded in the background the first time
    /// (from the object's home room, in that room's palette, as the cursor's).
    /// </summary>
    private List<FrameImage> BlastFrames()
    {
        var vm = Game.Vm;
        var blasts = vm.BlastObjects;
        if (blasts.Count == 0) return [];
        var list = new List<FrameImage>(blasts.Count);
        var page = GameState.PageOrigin;
        foreach (var b in blasts)
        {
            int room = vm.State.Objects.IsObject(b.Object) ? vm.State.Objects.Room(b.Object) : 0;
            if (ObjectImage(b.Object, room, b.Image) is not { } rgba) continue;
            list.Add(new FrameImage(SkiaImages.Of(rgba), new GameRect(page.X + b.X, page.Y + b.Y, rgba.Width * b.ScaleX / 255.0, rgba.Height * b.ScaleY / 255.0)));
        }
        return list;
    }

    /// <summary>A blasted object's image, loaded now (for a tool drawing one frame), or null.</summary>
    public static Core.Imaging.RgbaImage BlastImage(ScriptVm vm, BlastObject b)
    {
        int room = vm.State.Objects.IsObject(b.Object) ? vm.State.Objects.Room(b.Object) : 0;
        if (vm.Scripts.RoomBlock(room) is not { } lflf || !RoomModel.TryCreate(lflf, out var model)) return null;
        string name = RoomScripts.ObjectName(lflf, b.Object);
        return model.Objects.FirstOrDefault(o => o.Name == name) is { } ro
            ? RoomSceneBuilder.ObjectImageAsync(model, ro, Math.Max(0, b.Image - 1)).GetAwaiter().GetResult()
            : null;
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
        if (!(game.IsMouseCaptured || game.IsReplaying) || vm.CursorLevel <= 0) return null;
        var (obj, room, image) = vm.CursorImage;
        if (obj <= 0 || ObjectImage(obj, room, image) is not { } rgba) return null;
        var hotspot = vm.CursorHotspot is { X: 0, Y: 0 } ? new GamePoint(rgba.Width / 2.0, rgba.Height / 2.0) : new GamePoint(vm.CursorHotspot.X, vm.CursorHotspot.Y);
        return new FrameImage(SkiaImages.Of(rgba), new GameRect(game.CursorPosition - hotspot, new GameSize(rgba.Width, rgba.Height)));
    }

    /// <summary>Image <paramref name="image"/> (from 1) of an object of <paramref name="room"/>, once loaded; the first ask starts loading it.</summary>
    private RgbaImage ObjectImage(int obj, int room, int image)
    {
        var key = (obj, image);
        lock (_cursors)
        {
            if (_cursors.TryGetValue(key, out var rgba)) return rgba;
            if (_cursorsLoading.Add(key)) CursorLoading = LoadCursor(obj, room, image);
            return null;
        }
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
