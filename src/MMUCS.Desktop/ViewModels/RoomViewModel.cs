using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MMUCS.Core;
using MMUCS.Core.Diagnostics;
using MMUCS.Core.Imaging;
using MMUCS.Core.Scumm;
using MMUCS.Desktop.Controls;
using MMUCS.Desktop.Rendering;
using SkiaSharp;

namespace MMUCS.Desktop.ViewModels;

/// <summary>
/// A background the room can show: the room's own image, or a full-width (640px) object image
/// such as an alternate state of the scene or the inventory backdrop.
/// </summary>
public sealed record BackgroundChoice(string Label, SKImage Image, RgbaImage Rgba, RoomObject Object);

/// <summary>An entry in the Objects panel: the object plus its (UI-only) visibility.</summary>
public sealed partial class RoomObjectItem(RoomObject obj) : ObservableObject
{
    public RoomObject Object { get; } = obj;
    public string Name => Object.ToString();
    /// <summary>A costume no object claims, listed on its own (its object is a stand-in for its frame).</summary>
    public bool IsActorOnly { get; init; }
    public string Summary => IsActorOnly ? ActorSummary(Costume)
        : (Costume != null ? $"{Object.Summary}   costume {Costume.Costume}{(Costume.IsLinked ? " (linked)" : "")}{WhenText(Costume)}" : Object.Summary)
          + (Object.ImageCount > 0 && InitialState is int st ? $"   state {st}{(st == 0 ? " (hidden on entry)" : "")}" : "");
    /// <summary>The object's state when the room is entered (see <see cref="RoomObjectStates"/>), for objects with an image.</summary>
    public int? InitialState { get; init; }
    /// <summary>Whether it's shown when the room opens; Reset puts every entry back to this.</summary>
    public bool InitialVisible { get; set; } = true;
    /// <summary>
    /// Where the scripts put a costume no object claims: its position as the script writes it (with the
    /// elevation that lifts it), or why it has none.
    /// </summary>
    private static string ActorSummary(RoomActor a) => a switch
    {
        null => "",
        { HasPosition: true, Elevation: 0 } => $"placed at {a.X},{a.Y}{WhenText(a)}",
        { HasPosition: true } => $"placed at {a.X},{a.Y}, elevation {a.Elevation} (drawn at {a.X},{a.DrawY}){WhenText(a)}",
        { Actor: > 0 } => "placed at runtime (the script's position isn't a constant)",
        _ => "dressed at runtime (no script sets this costume with a constant)",
    };

    /// <summary>When the game puts a costume in the room, for the panel: "   on entry", "   in a cutscene" ….</summary>
    private static string WhenText(RoomActor a) => (a.When switch
    {
        PlacementTime.OnEntry => "   on entry",
        PlacementTime.OnEntryIf => "   on entry (conditional)",
        PlacementTime.Cutscene => "   in a cutscene",
        PlacementTime.Later => "   later (a verb or another script)",
        _ => "",
    }) + (a.IsPlayer ? ", player" : "") + (a.When is PlacementTime.OnEntry or PlacementTime.OnEntryIf && !a.StartsShown && !a.IsPlayer ? ", another costume starts" : "");

    [ObservableProperty] private bool _isVisible = true;
    /// <summary>The review marks that include it (Mark for review…), alone or with other entries.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsMarked), nameof(MarkText))] private IReadOnlyList<ReviewMark> _reviewMarks = Array.Empty<ReviewMark>();
    public bool IsMarked => ReviewMarks.Count > 0;
    public string MarkText => ReviewMarks.Count == 0 ? null : string.Join("\n\n", ReviewMarks.Select(m =>
        $"Marked for review (#{m.Id}{(m.Targets.Count > 1 ? $", with {m.Targets.Count - 1} more" : "")})"
        + (m.Tags.Count > 0 ? ": " + string.Join(", ", m.Tags) : "")
        + (string.IsNullOrWhiteSpace(m.Note) ? "" : "\n" + m.Note)));
    /// <summary>False for objects with no image (or a fully transparent one).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasToggle))] private bool _hasImage;
    /// <summary>The costumed actor drawn in this object's hotspot (image-less objects only).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasCostume), nameof(HasToggle), nameof(Summary))] private RoomActor _costume;
    public bool HasCostume => Costume != null;
    /// <summary>Whether there is anything to show or hide: an image or a costume frame.</summary>
    public bool HasToggle => HasImage || Costume is { CanDraw: true };
}

public sealed partial class RoomViewModel : ObservableObject
{
    /// <summary>Object images at least this wide are treated as alternative backgrounds.</summary>
    public const int FullWidth = 640;

    [ObservableProperty] private RoomModel _room;
    [ObservableProperty] private string _title = "No room selected";
    [ObservableProperty] private SKImage _backgroundImage;
    [ObservableProperty] private IReadOnlyList<RoomObjectLayer> _objectLayers;
    [ObservableProperty] private IReadOnlyList<Walkbox> _walkboxes;
    [ObservableProperty] private Size _roomSize;
    [ObservableProperty] private RoomObject _selectedObject;
    [ObservableProperty] private int _paletteIndex;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasPaletteChoices))] private int _maxPaletteIndex;
    [ObservableProperty] private int _backgroundIndex;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasBackgroundChoices))] private int _maxBackgroundIndex;
    [ObservableProperty] private string _backgroundLabel;
    [ObservableProperty] private RoomObject _backgroundObject;
    [ObservableProperty] private bool _isBusy;
    /// <summary>The Costume viewer's playing frame at its costume's place in this room (Play in place), or null.</summary>
    [ObservableProperty] private LiveCostume _liveCostume;
    /// <summary>A new room is loading: the canvas keeps the old picture until its images are ready.</summary>
    [ObservableProperty] private bool _isSwitchingRoom;

    [ObservableProperty] private bool _showBackground = true;
    [ObservableProperty] private bool _showObjects = true;
    [ObservableProperty] private bool _showHotspots;
    [ObservableProperty] private bool _showWalkboxes;
    [ObservableProperty] private bool _showLabels = true;
    [ObservableProperty] private bool _showBounds;
    /// <summary>Hide the parts of actors behind the room's z-planes, as the game does.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ZPlaneOverlay))] private bool _zPlaneMasking = true;
    /// <summary>Tint the room's z-planes over the scene (each plane its own colour) to see what hides actors.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ZPlaneOverlay))] private bool _showZPlanes;
    /// <summary>Which plane the overlay draws: 0 for all, else plane N (the index in <see cref="ZPlaneChoices"/>).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ZPlaneOverlay))] private int _zPlaneShown;
    /// <summary>"All planes", "Plane 1" … for the room's planes.</summary>
    [ObservableProperty] private IReadOnlyList<string> _zPlaneChoices = new[] { "All planes" };
    /// <summary>For the canvas: -1 draws no planes, 0 all of them, N plane N.</summary>
    public int ZPlaneOverlay => ShowZPlanes ? Math.Max(0, ZPlaneShown) : -1;
    /// <summary>
    /// The room's z-plane masks as drawn now (index 0 = plane 1): the background's planes with the shown
    /// objects' planes over them, as alpha images. Null when masking is off or the room has none.
    /// </summary>
    [ObservableProperty] private IReadOnlyList<SKImage> _zMasks;
    [ObservableProperty] private bool _showFullScreenObjects;
    /// <summary>List (and draw) costumes no object claims, e.g. the player in a cutscene costume.</summary>
    [ObservableProperty] private bool _showUnmatchedCostumes;
    /// <summary>Whether the Objects side panel is shown (toolbar toggle).</summary>
    [ObservableProperty] private bool _showObjectsPanel = true;

    public ObservableCollection<RoomObjectItem> Objects { get; } = new();

    [ObservableProperty] private RoomObjectItem _selectedObjectItem;
    /// <summary>
    /// Objects whose image is drawn (a new set on every change, so bindings refresh). Listing the
    /// visible ones rather than the hidden ones means layers still on screen from the previous room,
    /// or from before the costumes were re-analysed, aren't drawn while the new ones decode.
    /// </summary>
    [ObservableProperty] private IReadOnlySet<RoomObject> _visibleObjects = new HashSet<RoomObject>();
    private bool _batchVisibility;

    /// <summary>Header checkbox: true when all are visible, false when none, null (mixed) otherwise.</summary>
    public bool? AllObjectsVisible
    {
        get
        {
            // Only objects with an image or costume have a toggle, so only they count.
            int total = Objects.Count(o => o.HasToggle);
            if (total == 0) return true;
            int visible = Objects.Count(o => o.HasToggle && o.IsVisible);
            return visible == total ? true : visible == 0 ? false : null;
        }
        set => SetAllVisible(value != false); // clicking the mixed state shows everything
    }

    public void SetAllVisible(bool visible) => SetVisibility(_ => visible);

    private Dictionary<RoomObject, int> _initialStates = new();
    private bool _startVisibilityApplied;

    /// <summary>Puts every entry's visibility back to how the room opened: objects by their state on entry.</summary>
    [RelayCommand] private void ResetVisibility() => SetVisibility(o => o.InitialVisible);

    public void SetVisible(RoomObject obj, bool visible) => SetVisibility(o => ReferenceEquals(o.Object, obj) ? visible : null);

    /// <summary>Shows only <paramref name="obj"/> and hides every other object.</summary>
    public void ShowOnly(RoomObject obj) => SetVisibility(o => ReferenceEquals(o.Object, obj));

    public bool IsVisible(RoomObject obj) => VisibleObjects.Contains(obj);

    /// <summary>Applies a visibility per object (null leaves it unchanged), then refreshes once.</summary>
    private void SetVisibility(Func<RoomObjectItem, bool?> rule)
    {
        _batchVisibility = true;
        foreach (var item in Objects)
            if (rule(item) is bool v) item.IsVisible = v;
        _batchVisibility = false;
        RefreshVisibility();
    }

    private void OnItemChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RoomObjectItem.IsVisible) && !_batchVisibility) RefreshVisibility();
    }

    private void RefreshVisibility()
    {
        VisibleObjects = Objects.Where(o => o.IsVisible).Select(o => o.Object).ToHashSet();
        UpdateZMasks();
        SyncToggles();
        OnPropertyChanged(nameof(AllObjectsVisible));
    }

    // Keep the list selection and the canvas selection (a RoomObject) in step.
    partial void OnSelectedObjectItemChanged(RoomObjectItem value)
    {
        if (!ReferenceEquals(SelectedObject, value?.Object)) SelectedObject = value?.Object;
        // Focus the hierarchy on the object's costume (which loads it in the Costume viewer) or its OBIM.
        if (value != null) BlockFocusRequested?.Invoke(value.Costume?.Akos ?? value.Object.Block);
    }

    /// <summary>Raised when the selected object changes: the block the hierarchy should show.</summary>
    public event Action<ScummBlock> BlockFocusRequested;

    partial void OnSelectedObjectChanged(RoomObject value)
    {
        if (!ReferenceEquals(SelectedObjectItem?.Object, value))
            SelectedObjectItem = Objects.FirstOrDefault(o => ReferenceEquals(o.Object, value));
    }

    // View menu items invoke these; their IsChecked is bound one-way, so the state lives here only.
    [RelayCommand] private void ToggleBackground() => ShowBackground = !ShowBackground;
    [RelayCommand] private void ToggleObjects() => ShowObjects = !ShowObjects;
    [RelayCommand] private void ToggleHotspots() => ShowHotspots = !ShowHotspots;
    [RelayCommand] private void ToggleWalkboxes() => ShowWalkboxes = !ShowWalkboxes;
    [RelayCommand] private void ToggleLabels() => ShowLabels = !ShowLabels;
    [RelayCommand] private void ToggleBounds() => ShowBounds = !ShowBounds;
    [RelayCommand] private void ToggleZPlaneMasking() => ZPlaneMasking = !ZPlaneMasking;
    [RelayCommand] private void ToggleZPlanes() => ShowZPlanes = !ShowZPlanes;

    partial void OnZPlaneMaskingChanged(bool value) => UpdateZMasks();
    partial void OnShowZPlanesChanged(bool value) => UpdateZMasks();

    private ZPlane[] _roomPlanes = Array.Empty<ZPlane>();
    private int _planeCount;
    private readonly Dictionary<RoomObject, ZPlane[]> _objectPlanes = new();
    private string _zMaskKey;

    /// <summary>Rebuilds <see cref="ZMasks"/> from the room's planes and the objects shown now.</summary>
    private void UpdateZMasks()
    {
        var room = Room;
        if (!(ZPlaneMasking || ShowZPlanes) || room == null || _planeCount == 0 || room.Width <= 0)
        {
            _zMaskKey = null;
            ZMasks = null;
            return;
        }
        // Objects write their planes over the room's while they're drawn (shown, with an image).
        var shown = Objects.Where(o => !o.IsActorOnly && o.IsVisible && o.Object.ImageCount > 0).Select(o => o.Object).ToList();
        string key = room.Room.Offset + ":" + string.Join(",", shown.Select(o => o.Index));
        if (key == _zMaskKey && ZMasks != null) return; // a costume or hotspot toggled: the masks are the same
        _zMaskKey = key;
        var planes = ZPlanes.Compose(_roomPlanes, _planeCount, room.Width, room.Height, shown.Select(o =>
        {
            if (!_objectPlanes.TryGetValue(o, out var p))
            {
                try { p = ZPlanes.Object(o, Math.Max(0, _initialStates.GetValueOrDefault(o, 1) - 1)); }
                catch (Exception ex) { Log.Warn($"Z-planes of {o}: {ex.Message}"); p = Array.Empty<ZPlane>(); }
                _objectPlanes[o] = p;
            }
            return (o, p);
        }));
        var masks = new SKImage[planes.Length];
        for (int k = 0; k < planes.Length; k++) masks[k] = MaskImage(planes[k], room.Width, room.Height);
        ZMasks = masks;
    }

    /// <summary>A plane as an alpha image: opaque where it hides actors.</summary>
    private static SKImage MaskImage(ZPlane plane, int width, int height)
    {
        var alpha = new byte[width * height];
        for (int y = 0; y < height && y < plane.Height; y++)
        {
            int row = y * plane.Strips, o = y * width;
            for (int s = 0; s < plane.Strips; s++)
            {
                byte b = plane.Data[row + s];
                if (b == 0) continue;
                for (int bit = 0, x = s * 8; bit < 8 && x < width; bit++, x++)
                    if ((b & (0x80 >> bit)) != 0) alpha[o + x] = 255;
            }
        }
        var info = new SKImageInfo(width, height, SKColorType.Alpha8, SKAlphaType.Premul);
        return SKImage.FromPixelCopy(info, alpha, width);
    }
    [RelayCommand] private void ToggleFullScreenObjects() => ShowFullScreenObjects = !ShowFullScreenObjects;
    [RelayCommand] private void ToggleUnmatchedCostumes() => ShowUnmatchedCostumes = !ShowUnmatchedCostumes;

    // The View menu's Objects / Unmatched costumes toggles switch the Objects panel checkboxes of
    // their group; the checkboxes in turn decide what is drawn, and the toggles follow them.
    private bool _syncingToggles;

    partial void OnShowObjectsChanged(bool value)
    {
        if (!_syncingToggles) SetVisibility(o => !o.IsActorOnly && o.HasToggle ? value : null);
    }

    partial void OnShowUnmatchedCostumesChanged(bool value)
    {
        if (!_syncingToggles) SetVisibility(o => o.IsActorOnly && o.HasToggle ? value : null);
    }

    /// <summary>Each toggle is on while any checkbox of its group is (unchanged when the group is empty).</summary>
    private void SyncToggles()
    {
        _syncingToggles = true;
        var objects = Objects.Where(o => !o.IsActorOnly && o.HasToggle).ToList();
        var costumes = Objects.Where(o => o.IsActorOnly && o.HasToggle).ToList();
        if (objects.Count > 0) ShowObjects = objects.Any(o => o.IsVisible);
        if (costumes.Count > 0) ShowUnmatchedCostumes = costumes.Any(o => o.IsVisible);
        _syncingToggles = false;
    }

    private IReadOnlyList<BackgroundChoice> _backgroundChoices = Array.Empty<BackgroundChoice>();
    /// <summary>The room's costumed actors (from its scripts), with their parsed costumes and default frames.</summary>
    private List<RoomActor> _actors = new();
    /// <summary>Where each drawable actor is shown: its object's entry, or its own stand-in entry.</summary>
    private Dictionary<RoomActor, RoomObject> _actorTargets = new();
    private readonly AkosParser _akosParser = new();

    /// <summary>The room's costumes (one per AKOS), for the Link costume menu.</summary>
    public IReadOnlyList<RoomActor> RoomCostumes => _actors
        .GroupBy(a => a.Akos).Select(g => g.OrderByDescending(a => a.CanDraw).First())
        .OrderBy(a => a.Costume).ToList();

    /// <summary>Raised after a manual link changes (so it can be saved and the hierarchy relabelled).</summary>
    public event Action LinksChanged;

    /// <summary>Links <paramref name="obj"/> to a costume (0 = none) or back to automatic matching (null).</summary>
    public async Task SetCostumeLinkAsync(RoomObject obj, int? costume)
    {
        var room = Room;
        if (room == null || obj == null || obj.Index < 0) return;
        RoomActors.SetLink(room, obj, costume);
        await RefreshActorsAsync(room);
        LinksChanged?.Invoke(); // after the re-analysis, so the hierarchy picks up the new names
        await ReloadImagesAsync();
    }

    public int? GetCostumeLink(RoomObject obj) => Room != null && obj != null && obj.Index >= 0 ? RoomActors.GetLink(Room, obj) : null;

    /// <summary>The entry an object belongs to (the actor whose stand-in it is, for costume-only entries).</summary>
    public RoomObjectItem ItemFor(RoomObject obj) => Objects.FirstOrDefault(o => ReferenceEquals(o.Object, obj));
    private bool _resetBackgroundChoice = true;
    private CancellationTokenSource _cts;

    public bool HasRoom => Room != null;
    /// <summary>Spinboxes are only active when there is something to switch between.</summary>
    public bool HasPaletteChoices => MaxPaletteIndex > 0;
    public bool HasBackgroundChoices => MaxBackgroundIndex > 0;

    public async Task LoadAsync(RoomModel room)
    {
        if (room == null || ReferenceEquals(room.Room, Room?.Room)) return;

        IsSwitchingRoom = true;
        LiveCostume = null;
        Room = room;
        OnPropertyChanged(nameof(HasRoom));
        Title = room.RoomNo > 0
            ? $"Room {room.RoomNo}{(string.IsNullOrEmpty(room.Name) ? "" : $" — {room.Name}")}  ({room.Width}×{room.Height})"
            : $"Room @0x{room.Room.Offset:X8}  ({room.Width}×{room.Height})";

        RoomSize = new Size(room.Width, room.Height);
        Walkboxes = room.Walkboxes;
        foreach (var item in Objects) item.PropertyChanged -= OnItemChanged;
        Objects.Clear();
        // Object images start as the game shows them on entry: hidden while their state is 0.
        Dictionary<RoomObject, int> states;
        try { states = RoomObjectStates.Initial(room); }
        catch (Exception ex) { Log.Warn($"Object states for room {room.RoomNo}: {ex.Message}"); states = new(); }
        _initialStates = states;
        _startVisibilityApplied = false;
        _objectPlanes.Clear();
        _zMaskKey = null;
        try
        {
            _roomPlanes = ZPlanes.Room(room);
            _planeCount = ZPlanes.Count(room);
            ZPlaneChoices = new[] { "All planes" }.Concat(Enumerable.Range(1, _planeCount).Select(n => $"Plane {n}")).ToList();
            if (ZPlaneShown > _planeCount) ZPlaneShown = 0;
        }
        catch (Exception ex)
        {
            Log.Warn($"Z-planes for room {room.RoomNo}: {ex.Message}");
            _roomPlanes = Array.Empty<ZPlane>();
            _planeCount = 0;
        }
        foreach (var o in room.Objects)
        {
            int? state = states.TryGetValue(o, out int st) ? st : null;
            // Image-less hotspots have nothing to hide (their costume shows), and a room with no background
            // is a library of images (room 3's inventory icons, all in state 0), not a scene.
            bool shown = o.ImageCount <= 0 || room.Width <= 0 || state != 0;
            var item = new RoomObjectItem(o) { IsVisible = ShowObjects && shown, InitialState = state, InitialVisible = shown };
            item.PropertyChanged += OnItemChanged;
            Objects.Add(item);
        }
        SelectedObject = null;
        RefreshVisibility();

        MaxPaletteIndex = Math.Max(0, room.PaletteCount - 1);
        _suppressPaletteReload = true;
        PaletteIndex = 0;
        _suppressPaletteReload = false;
        _resetBackgroundChoice = true;

        // With objects and costumes hidden nothing waits on the costume analysis: show the new
        // background as soon as it's decoded and let the analysis finish behind it.
        if (!ShowObjects && !ShowUnmatchedCostumes)
        {
            await ShowBackgroundFirstAsync(room);
            if (!ReferenceEquals(Room, room)) return;
        }

        await RefreshActorsAsync(room);
        if (!ReferenceEquals(Room, room)) return;

        await ReloadImagesAsync();
    }

    private bool _suppressPaletteReload;

    /// <summary>
    /// Shows <paramref name="frame"/> of costume <paramref name="akos"/> where this room places that
    /// costume, in place of its usual frame; null (or a costume this room doesn't place) clears it.
    /// </summary>
    public void ShowLiveFrame(ScummBlock akos, AkosFrame frame)
    {
        var placed = frame == null || akos == null ? default
            : _actorTargets.FirstOrDefault(kv => ReferenceEquals(kv.Key.Akos, akos) && kv.Key.HasPosition);
        if (placed.Key is not { } actor)
        {
            LiveCostume = null;
            return;
        }
        try
        {
            RgbaImage rgba;
            int ox, oy;
            // The room's own parse of the costume, in the room's current palette.
            lock (actor.Data) rgba = AkosFrames.Render(actor.Data, frame, out ox, out oy);
            float f = actor.ScaleFactor;
            LiveCostume = new LiveCostume(placed.Value, rgba?.ToSkImage(), actor.X - (int)(ox * f), actor.DrawY - (int)(oy * f), f, actor.ZPlane);
        }
        catch (Exception ex)
        {
            Log.Error($"Play in place failed: {ex.Message}");
            LiveCostume = null;
        }
    }

    private static int BackgroundCacheId(RoomModel room) =>
        // Rooms can appear in more than one file (e.g. per-disc copies), so key by source file too.
        (int)Core.StableHash.Of(room.Room.SourceKey, room.RoomNo > 0 ? room.RoomNo : room.Room.Offset);

    /// <summary>Shows the room's own background (with no object layers yet) and ends the room switch.</summary>
    private async Task ShowBackgroundFirstAsync(RoomModel room)
    {
        try
        {
            var bg = await ScummBackgroundCache.GetCachedBackgroundAsync(
                BackgroundCacheId(room), room.Rmhd, room.Room, cacheDisabled: false, PaletteIndex, CancellationToken.None);
            if (!ReferenceEquals(Room, room) || bg.ExecutedImage == null) return; // no image of its own: wait for the full load
            ObjectLayers = Array.Empty<RoomObjectLayer>(); // the old room's labels and hotspots go too
            BackgroundObject = null;
            BackgroundImage = bg.ExecutedImage.ToSkImage();
            BackgroundLabel = "room";
            IsSwitchingRoom = false;
        }
        catch (Exception ex)
        {
            Log.Error($"Room decode failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Analyses the room's costumes (scripts give costume and position, the frame decides which
    /// hotspot; manual links override) and applies them to the Objects list.
    /// </summary>
    private async Task RefreshActorsAsync(RoomModel room)
    {
        List<RoomActor> actors;
        try
        {
            actors = await Task.Run(() => RoomActors.Analyze(room, _akosParser));
        }
        catch (Exception ex)
        {
            actors = new();
            Log.Error($"Costume analysis failed: {ex.Message}");
        }
        if (!ReferenceEquals(Room, room)) return;
        _actors = actors;
        ApplyActors();
    }

    /// <summary>Gives objects their costumes and lists unclaimed costumes when asked for.</summary>
    private void ApplyActors()
    {
        _batchVisibility = true;
        foreach (var item in Objects.Where(o => o.IsActorOnly).ToList())
        {
            item.PropertyChanged -= OnItemChanged;
            Objects.Remove(item);
        }

        // The analysis may be cached from another RoomModel of the same room, so match by OBIM block.
        _actorTargets = new Dictionary<RoomActor, RoomObject>();
        // The first analysis of a room sets how its costumes start (later ones, after a link change, keep
        // what the user toggled): a hotspot's costume shows only if it's there as the room opens.
        bool applyStart = !_startVisibilityApplied;
        _startVisibilityApplied = true;
        foreach (var item in Objects)
        {
            item.Costume = _actors.FirstOrDefault(a => a.Object != null && ReferenceEquals(a.Object.Block, item.Object.Block));
            if (item.Costume is { CanDraw: true } a) _actorTargets[a] = item.Object;
            if (applyStart && item.Object.ImageCount <= 0 && item.Costume is { CanDraw: true } c)
            {
                item.InitialVisible = c.StartsShown;
                item.IsVisible = ShowObjects && c.StartsShown;
            }
        }

        {
            foreach (var actor in _actors.Where(a => a.Object == null).OrderBy(a => a.Costume))
            {
                var b = actor.RoomBounds;
                var standIn = new RoomObject
                {
                    Index = -1,
                    Name = actor.Label,
                    X = b?.Left ?? 0,
                    Y = b?.Top ?? 0,
                    Width = b is { } r1 ? r1.Right - r1.Left : 0,
                    Height = b is { } r2 ? r2.Bottom - r2.Top : 0,
                    Block = actor.Akos,
                };
                // Shown as the room opens only when the game has it there then (not the player, not a
                // cutscene's or a verb's costume); Unmatched costumes in the View menu shows or hides them all.
                var item = new RoomObjectItem(standIn) { IsActorOnly = true, Costume = actor, IsVisible = actor.StartsShown, InitialVisible = actor.StartsShown };
                item.PropertyChanged += OnItemChanged;
                Objects.Add(item);
                if (actor.CanDraw) _actorTargets[actor] = standIn;
            }
        }
        _batchVisibility = false;
        ApplyReviewMarks();
        RefreshVisibility();
    }

    // ── Review marks (Mark for review…, read by tools/mroom's review command) ──

    /// <summary>Shows on each entry whether a mark includes it; reads the marks again (mroom may have cleared some).</summary>
    public void ApplyReviewMarks()
    {
        var room = Room;
        var marks = room == null ? new List<ReviewMark>() : ReviewMarks.Load().Where(m => m.Room == room.RoomNo).ToList();
        foreach (var item in Objects)
        {
            var target = TargetOf(item);
            var mine = target == null ? new List<ReviewMark>() : marks.Where(m => m.Includes(room.RoomNo, target)).ToList();
            item.ReviewMarks = mine;
        }
    }

    /// <summary>
    /// An entry as a review target, with what MMUCS works out for it now: a costume (its placement,
    /// scale, z-plane, owner) when it has one, else the object image (its box and state on entry).
    /// </summary>
    public ReviewTarget TargetOf(RoomObjectItem item)
    {
        if (Room == null || item == null) return null;
        if (item.Costume is { } a)
            return new ReviewTarget
            {
                Kind = "costume", Costume = a.Costume, Akos = $"0x{a.Akos.Offset:X8}",
                Snapshot = new ReviewSnapshot
                {
                    Placed = a.HasPosition,
                    Actor = a.Actor > 0 ? a.Actor : null,
                    X = a.HasPosition ? a.X : null,
                    Y = a.HasPosition ? a.Y : null,
                    Elevation = a.HasPosition && a.Elevation != 0 ? a.Elevation : null,
                    Scale = a.HasPosition ? a.Scale : null,
                    ZPlane = a.HasPosition ? a.ZPlane : null,
                    DressedBy = a.DressedBy >= 0 ? a.DressedBy : null,
                    Owner = a.Owner?.Name,
                    StartsShown = a.StartsShown,
                    When = RoomDataExport.WhenText(a.When),
                },
            };
        if (item.IsActorOnly) return null;
        var o = item.Object;
        return new ReviewTarget
        {
            Kind = "object", Object = o.Name,
            Snapshot = new ReviewSnapshot { Placed = o.ImageCount > 0, X = o.X, Y = o.Y, Width = o.Width, Height = o.Height, EntryState = item.InitialState },
        };
    }

    /// <summary>The mark for exactly these entries, if there is one (what "Edit review mark…" edits).</summary>
    public ReviewMark MarkFor(IReadOnlyList<RoomObjectItem> items)
    {
        var probe = NewMark(items);
        return probe == null ? null : ReviewMarks.Load().FirstOrDefault(m => m.SameTargets(probe));
    }

    private ReviewMark NewMark(IReadOnlyList<RoomObjectItem> items)
    {
        var room = Room;
        var targets = items.Select(TargetOf).Where(t => t != null).ToList();
        if (room == null || targets.Count == 0) return null;
        return new ReviewMark { Room = room.RoomNo, RoomName = room.Name, Targets = targets };
    }

    /// <summary>Marks these entries together (one mark: a pair drawn in the wrong order, say).</summary>
    public void MarkForReview(IReadOnlyList<RoomObjectItem> items, IEnumerable<string> tags, string note)
    {
        var mark = NewMark(items);
        if (mark == null) return;
        mark.Tags = tags.ToList();
        mark.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        ReviewMarks.Add(mark);
        ApplyReviewMarks();
    }

    /// <summary>Clears every mark that includes any of these entries.</summary>
    public void ClearReviewMarks(IReadOnlyList<RoomObjectItem> items)
    {
        foreach (int id in items.SelectMany(i => i.ReviewMarks).Select(m => m.Id).Distinct().ToList())
            ReviewMarks.Remove(id);
        ApplyReviewMarks();
    }

    // ── Selection: several entries can be selected; the last one is the current one ──

    /// <summary>The entries selected in the Objects panel (the current one, SelectedObjectItem, among them).</summary>
    public IReadOnlyList<RoomObjectItem> SelectedItems
    {
        get => _selectedItems;
        set
        {
            _selectedItems = value ?? Array.Empty<RoomObjectItem>();
            SelectedObjects = _selectedItems.Select(i => i.Object).ToHashSet();
        }
    }
    private IReadOnlyList<RoomObjectItem> _selectedItems = Array.Empty<RoomObjectItem>();

    /// <summary>The selected entries' objects, for the canvas to outline.</summary>
    [ObservableProperty] private IReadOnlySet<RoomObject> _selectedObjects = new HashSet<RoomObject>();

    /// <summary>The entries an action on <paramref name="item"/> applies to: the selection when it holds it, else just it.</summary>
    public IReadOnlyList<RoomObjectItem> ActionItems(RoomObjectItem item) =>
        item != null && SelectedItems.Count > 1 && SelectedItems.Contains(item) ? SelectedItems : item == null ? Array.Empty<RoomObjectItem>() : new[] { item };

    /// <summary>Shows or hides several entries at once.</summary>
    public void SetVisible(IReadOnlyList<RoomObjectItem> items, bool visible) =>
        SetVisibility(o => items.Contains(o) ? visible : null);

    /// <summary>Shows these entries and hides every other one.</summary>
    public void ShowOnly(IReadOnlyList<RoomObjectItem> items) => SetVisibility(o => items.Contains(o));

    /// <summary>Renders the actors' frames in palette <paramref name="palette"/>, keyed by where they're shown.</summary>
    private Dictionary<RoomObject, (SKImage Image, int X, int Y, int Depth, int Actor, float Scale, int ZPlane)> RenderActors(
        Dictionary<RoomActor, RoomObject> targets, int palette, CancellationToken token)
    {
        var result = new Dictionary<RoomObject, (SKImage, int, int, int, int, float, int)>();
        foreach (var (actor, target) in targets)
        {
            if (token.IsCancellationRequested) break;
            lock (actor.Data)
            {
                _akosParser.UpdatePaletteOnly(actor.Data, actor.Akos, palette);
                var rgba = AkosFrames.Render(actor.Data, actor.Frame, out int ox, out int oy, token);
                // Scaled about the actor's origin: the frame's offset from it shrinks with the frame.
                float f = actor.ScaleFactor;
                if (rgba != null) result[target] = (rgba.ToSkImage(), actor.X - (int)(ox * f), actor.DrawY - (int)(oy * f), actor.SortY, actor.Actor, f, actor.ZPlane);
            }
        }
        return result;
    }

    partial void OnPaletteIndexChanged(int value)
    {
        if (!_suppressPaletteReload) _ = ReloadImagesAsync();
    }

    private async Task ReloadImagesAsync()
    {
        var room = Room;
        if (room == null) return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        IsBusy = true;

        try
        {
            var bg = await ScummBackgroundCache.GetCachedBackgroundAsync(
                BackgroundCacheId(room), room.Rmhd, room.Room, cacheDisabled: false, PaletteIndex, token);
            if (token.IsCancellationRequested) return;

            var roomBackground = bg.ExecutedImage;

            int palette = PaletteIndex;
            var targets = _actorTargets;
            var costumes = await Task.Run(() => RenderActors(targets, palette, token), token);
            if (token.IsCancellationRequested) return;

            var layers = new List<RoomObjectLayer>(room.Objects.Count + targets.Count);
            var fullWidth = new List<BackgroundChoice>();
            var hasImage = new Dictionary<RoomObject, bool>();
            foreach (var obj in room.Objects)
            {
                SKImage image = null;
                RgbaImage rgba = null;
                if (obj.ImageCount > 0 && ObimDecoders.TryGetObimInfo(obj.Block, out var info))
                {
                    // Objects use the room's current palette: SCUMM v8 has no per-object palette, the
                    // game switches the room palette from scripts.
                    // The image for its state on entry (state N shows image N); the first when it starts hidden.
                    int frame = RoomObjectStates.FrameFor(_initialStates.GetValueOrDefault(obj, 1), info.TotalFrames > 0 ? info.TotalFrames : 1);
                    var result = await ObimCache.GetCachedObimAsync(obj.Block, frame, PaletteIndex, bakeImage: true, token);
                    if (token.IsCancellationRequested) return;
                    rgba = result.ExecutedImage;
                    image = rgba?.ToSkImage();
                }
                costumes.TryGetValue(obj, out var costume);
                layers.Add(new RoomObjectLayer(obj, image, costume.Image, costume.X, costume.Y, CostumeDepth: costume.Depth, CostumeActor: costume.Actor, CostumeScale: costume.Scale == 0 ? 1 : costume.Scale, CostumeZPlane: costume.ZPlane));
                hasImage[obj] = rgba != null && !IsFullyTransparent(rgba);
                if (image != null && image.Width >= FullWidth) fullWidth.Add(new BackgroundChoice(obj.ToString(), image, rgba, obj));
            }

            // Stand-ins for costumes no object claims.
            foreach (var item in Objects.Where(o => o.IsActorOnly))
                if (costumes.TryGetValue(item.Object, out var c))
                    layers.Add(new RoomObjectLayer(item.Object, null, c.Image, c.X, c.Y, IsActorOnly: true, CostumeDepth: c.Depth, CostumeActor: c.Actor, CostumeScale: c.Scale, CostumeZPlane: c.ZPlane));

            // Background choices: the room's own image first, then every full-width object image.
            var choices = new List<BackgroundChoice>();
            if (roomBackground != null) choices.Add(new BackgroundChoice("room", roomBackground.ToSkImage(), roomBackground, null));
            choices.AddRange(fullWidth);

            ObjectLayers = layers;
            foreach (var item in Objects.Where(o => !o.IsActorOnly)) item.HasImage = hasImage.TryGetValue(item.Object, out var h) && h;
            OnPropertyChanged(nameof(AllObjectsVisible));
            _backgroundChoices = choices;
            MaxBackgroundIndex = Math.Max(0, choices.Count - 1);
            // A new room starts on its first choice (its own image, or e.g. the inventory backdrop);
            // a palette change keeps the current choice.
            int index = _resetBackgroundChoice ? 0 : Math.Clamp(BackgroundIndex, 0, MaxBackgroundIndex);
            _resetBackgroundChoice = false;
            _restoringBackground = true;
            if (BackgroundIndex != index) BackgroundIndex = index; // applies via OnBackgroundIndexChanged
            else ApplyBackgroundChoice();
            _restoringBackground = false;
            IsSwitchingRoom = false; // background, objects and overlays all show together
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error($"Room decode failed: {ex.Message}");
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsBusy = false;
                IsSwitchingRoom = false;
            }
        }
    }

    private bool _restoringBackground;

    /// <summary>
    /// Choosing background k switches to palette k (clamped to the room's palettes). In COMI each
    /// alternate background is drawn for the matching palette (e.g. stancrpt's office and palette 1,
    /// the credits pages and palettes 1–5). The Palette spinbox can still override it.
    /// </summary>
    private static bool IsFullyTransparent(RgbaImage image)
    {
        var data = image.Data;
        for (int i = 3; i < data.Length; i += 4)
            if (data[i] != 0) return false;
        return true;
    }

    /// <summary>Toggles the visibility of the selected object (Space in the Objects list).</summary>
    public void ToggleSelectedVisibility()
    {
        // Several selected: all follow the current one's new state.
        var items = ActionItems(SelectedObjectItem).Where(o => o.HasToggle).ToList();
        if (items.Count > 1) { SetVisible(items, !SelectedObjectItem.IsVisible); return; }
        if (SelectedObjectItem is { HasToggle: true } item) item.IsVisible = !item.IsVisible;
    }

    partial void OnBackgroundIndexChanged(int value)
    {
        int palette = Math.Clamp(value, 0, MaxPaletteIndex);
        if (!_restoringBackground && palette != PaletteIndex)
        {
            // Keep showing the current background until the reload has decoded the new one with its
            // palette; showing it now would flash it in the old palette for a frame. The reload
            // restores this index and applies background and objects together.
            PaletteIndex = palette;
            return;
        }
        ApplyBackgroundChoice();
    }

    private void ApplyBackgroundChoice()
    {
        var choice = BackgroundIndex >= 0 && BackgroundIndex < _backgroundChoices.Count ? _backgroundChoices[BackgroundIndex] : null;
        BackgroundObject = choice?.Object;
        BackgroundImage = choice?.Image;
        BackgroundLabel = choice == null ? "none" : choice.Label;
    }

    /// <summary>Exports the background currently shown (the room's image or the chosen full-width one).</summary>
    public void ExportBackground(string folder)
    {
        var choice = BackgroundIndex >= 0 && BackgroundIndex < _backgroundChoices.Count ? _backgroundChoices[BackgroundIndex] : null;
        if (choice?.Rgba == null || Room == null) return;
        string room = Room.RoomNo > 0 ? Room.RoomNo.ToString("D3") : Room.Room.Offset.ToString("X8");
        string suffix = choice.Object == null ? $"pal{PaletteIndex}" : string.Join("_", choice.Label.Split(Path.GetInvalidFileNameChars()));
        string path = Path.Combine(folder, $"room_{room}_{suffix}.png");
        choice.Rgba.SavePng(path);
        Log.Info($"Exported {path}");
    }
}
