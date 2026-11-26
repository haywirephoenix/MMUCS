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
using MMUCS.Player.Rooms;
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
          + (Object.ImageCount > 0 && InitialState is int st ? $"   state {st}{(st == 0 ? " (hidden on entry)" : "")}" : "")
          + (Unused != null ? $"   unused{(string.IsNullOrEmpty(Unused.Note) ? "" : $": {Unused.Note}")}" : "");
    /// <summary>The object's state when the room is entered (see <see cref="RoomObjectStates"/>), for objects with an image.</summary>
    public int? InitialState { get; init; }
    /// <summary>Whether it's shown when the room opens; Reset puts every entry back to this.</summary>
    public bool InitialVisible { get; set; } = true;
    /// <summary>Art the game never shows (<see cref="UnusedArt"/>): left out unless Unused is on.</summary>
    public UnusedArt.Entry Unused { get; init; }
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

    [ObservableProperty] public partial bool IsVisible { get; set; } = true;
    /// <summary>
    /// Shown because it was toggled on (its eye, the menu, Space), not by the Show mode: then it's drawn
    /// even where the view hides its kind (a full-screen image with Full-screen objects off).
    /// </summary>
    public bool ShownByToggle { get; set; }
    /// <summary>The review marks that include it (Mark for review…), alone or with other entries.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsMarked), nameof(MarkText))] public partial IReadOnlyList<ReviewMark> ReviewMarks { get; set; } = Array.Empty<ReviewMark>();
    public bool IsMarked => ReviewMarks.Count > 0;
    public string MarkText => ReviewMarks.Count == 0 ? null : string.Join("\n\n", ReviewMarks.Select(m =>
        $"Marked for review (#{m.Id}{(m.Targets.Count > 1 ? $", with {m.Targets.Count - 1} more" : "")})"
        + (m.Tags.Count > 0 ? ": " + string.Join(", ", m.Tags) : "")
        + (string.IsNullOrWhiteSpace(m.Note) ? "" : "\n" + m.Note)));
    /// <summary>False for objects with no image (or a fully transparent one).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasToggle))] public partial bool HasImage { get; set; }
    /// <summary>The costumed actor drawn in this object's hotspot (image-less objects only).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasCostume), nameof(HasToggle), nameof(Summary))] public partial RoomActor Costume { get; set; }
    public bool HasCostume => Costume != null;
    /// <summary>Whether there is anything to show or hide: an image or a costume frame.</summary>
    public bool HasToggle => HasImage || Costume is { CanDraw: true };
}

public sealed partial class RoomViewModel : ObservableObject
{
    /// <summary>Object images at least this wide are treated as alternative backgrounds.</summary>
    public const int FullWidth = 640;

    [ObservableProperty] public partial RoomModel Room { get; set; }
    /// <summary>Objects shown by the user's own toggle: drawn even when Full-screen objects is off.</summary>
    [ObservableProperty] public partial IReadOnlySet<RoomObject> ToggledObjects { get; set; }
    [ObservableProperty] public partial string Title { get; set; } = "No room selected";
    [ObservableProperty] public partial SKImage BackgroundImage { get; set; }
    [ObservableProperty] public partial IReadOnlyList<RoomObjectLayer> ObjectLayers { get; set; }
    [ObservableProperty] public partial IReadOnlyList<Walkbox> Walkboxes { get; set; }
    [ObservableProperty] public partial Size RoomSize { get; set; }
    [ObservableProperty] public partial RoomObject SelectedObject { get; set; }
    [ObservableProperty] public partial int BackgroundIndex { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasBackgroundChoices))] public partial int MaxBackgroundIndex { get; set; }
    [ObservableProperty] public partial string BackgroundLabel { get; set; }
    [ObservableProperty] public partial RoomObject BackgroundObject { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    /// <summary>The Costume viewer's playing frame at its costume's place in this room (Play in place), or null.</summary>
    [ObservableProperty] public partial LiveCostume LiveCostume { get; set; }
    /// <summary>A new room is loading: the canvas keeps the old picture until its images are ready.</summary>
    [ObservableProperty] public partial bool IsSwitchingRoom { get; set; }

    [ObservableProperty] public partial bool ShowBackground { get; set; } = true;
    [ObservableProperty] public partial bool ShowHotspots { get; set; }
    [ObservableProperty] public partial bool ShowWalkboxes { get; set; }
    [ObservableProperty] public partial bool ShowLabels { get; set; } = true;
    [ObservableProperty] public partial bool ShowBounds { get; set; }
    /// <summary>Outline the selected object(s) on the canvas.</summary>
    [ObservableProperty] public partial bool ShowSelectionOutline { get; set; } = true;
    /// <summary>Hide the parts of actors behind the room's z-planes, as the game does.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ZPlaneOverlay))] public partial bool ZPlaneMasking { get; set; } = true;
    /// <summary>Tint the room's z-planes over the scene (each plane its own colour) to see what hides actors.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ZPlaneOverlay))] public partial bool ShowZPlanes { get; set; }
    /// <summary>Which plane the overlay draws: 0 for all, else plane N (the index in <see cref="ZPlaneChoices"/>).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ZPlaneOverlay))] public partial int ZPlaneShown { get; set; }
    /// <summary>"All planes", "Plane 1" … for the room's planes.</summary>
    [ObservableProperty] public partial IReadOnlyList<string> ZPlaneChoices { get; set; } = new[] { "All planes" };
    /// <summary>For the canvas: -1 draws no planes, 0 all of them, N plane N.</summary>
    public int ZPlaneOverlay => ShowZPlanes ? Math.Max(0, ZPlaneShown) : -1;
    /// <summary>
    /// The sets every image shown here belongs to, for the canvas: it holds them while it may still draw
    /// them, so each set is freed as soon as neither this view model nor a pending draw uses it.
    /// </summary>
    [ObservableProperty] public partial IReadOnlyList<ImageSet> ImageSets { get; set; }

    // Every image this view model shows belongs to one of these sets.
    /// <summary>The room's own background, decoded once per room and kept for reloads of the same room.</summary>
    private (RoomModel Room, ImageSet Set, SKImage Image, RgbaImage Rgba) _background;
    /// <summary>Object and costume images from the last finished load.</summary>
    private ImageSet _loadImages;
    private ImageSet _maskImages;
    private ImageSet _liveImages;
    /// <summary>Placed costumes redrawn since that load while a mod cel's offset is edited (<see cref="PreviewCelOffset"/>).</summary>
    private ImageSet _editImages;
    /// <summary>Object images swapped in since that load by a state change.</summary>
    private readonly Dictionary<RoomObject, ImageSet> _stateImages = [];
    /// <summary>
    /// Sets from an earlier room that the background choices (or the layers) may still show until the new
    /// room's load finishes and replaces them all.
    /// </summary>
    private readonly List<ImageSet> _retiredImages = [];

    /// <summary>Tells the canvas which sets are in use now. Call after changing the properties that show them.</summary>
    private void PublishImages()
    {
        var sets = new List<ImageSet>();
        foreach (var set in new[] { _background.Set, _loadImages, _maskImages, _liveImages, _editImages })
        {
            if (set != null) sets.Add(set);
        }
        sets.AddRange(_stateImages.Values);
        sets.AddRange(_retiredImages);
        ImageSets = sets;
    }

    /// <summary>Puts <paramref name="next"/> in <paramref name="field"/>'s place and frees the set it replaces.</summary>
    private void ReplaceImages(ref ImageSet field, ImageSet next)
    {
        var old = field;
        field = next;
        PublishImages();
        if (!ReferenceEquals(old, next)) old?.Release();
    }

    /// <summary>The room's own background (decoded and converted once per room), or null when it has none.</summary>
    private async Task<(SKImage Image, RgbaImage Rgba)> RoomBackgroundAsync(RoomModel room, CancellationToken token)
    {
        if (ReferenceEquals(_background.Room, room))
        {
            return (_background.Image, _background.Rgba);
        }

        var bg = await ScummBackgroundCache.GetCachedBackgroundAsync(
            BackgroundCacheId(room), room.Rmhd, room.Room, cacheDisabled: false, 0, token);
        if (token.IsCancellationRequested || bg.ExecutedImage == null)
        {
            return (null, bg.ExecutedImage);
        }

        if (ReferenceEquals(_background.Room, room))
        {
            return (_background.Image, _background.Rgba); // made meanwhile by another load of this room
        }

        // The old room's background may still be on screen (or a background choice) until this room's
        // load finishes.
        if (_background.Set != null) _retiredImages.Add(_background.Set);
        var set = new ImageSet();
        _background = (room, set, set.Add(bg.ExecutedImage.ToSkImage()), bg.ExecutedImage);
        PublishImages();
        return (_background.Image, _background.Rgba);
    }

    /// <summary>
    /// The room's z-plane masks as drawn now (index 0 = plane 1): the background's planes with the shown
    /// objects' planes over them, as alpha images. Null when masking is off or the room has none.
    /// </summary>
    [ObservableProperty] public partial IReadOnlyList<SKImage> ZMasks { get; set; }
    [ObservableProperty] public partial bool ShowFullScreenObjects { get; set; }
    /// <summary>
    /// What each room starts with, and what Reset goes back to: <see cref="ShowModes"/> by index. Room start
    /// is what the game shows on entry; Everything every object image and placed costume, ignoring the
    /// entry scripts (for checking positions and scales); Hidden at start the reverse of Room start; None
    /// nothing. The Objects panel's checkboxes still change single entries.
    /// </summary>
    [ObservableProperty] public partial int ShowMode { get; set; }
    public static IReadOnlyList<string> ShowModes { get; } = ["Room start", "Everything", "Hidden at start", "None"];
    public const int ShowRoomStart = 0, ShowEverything = 1, ShowHiddenAtStart = 2, ShowNone = 3;

    /// <summary>Whether an entry is shown under the current <see cref="ShowMode"/>. Entries with nothing to
    /// draw (a hotspot without a costume) keep their room-start value.</summary>
    private bool ShownByMode(RoomObjectItem o)
    {
        bool drawable = o.Object.ImageCount > 0 || o.Costume is { CanDraw: true };
        if (!drawable)
        {
            return o.InitialVisible;
        }
        // Unused art only when asked for; then the mode decides, as for anything else.
        if (o.Unused != null && !ShowUnused)
        {
            return false;
        }

        return ShowMode switch
        {
            ShowEverything => true,
            ShowHiddenAtStart => !o.InitialVisible,
            ShowNone => false,
            _ => o.InitialVisible,
        };
    }

    partial void OnShowModeChanged(int value) => SetVisibility(o => ShownByMode(o));

    /// <summary>Include the art the game never shows (<see cref="UnusedArt"/>) in what the Show mode shows.</summary>
    [ObservableProperty] public partial bool ShowUnused { get; set; }

    partial void OnShowUnusedChanged(bool value) => SetVisibility(o => o.Unused != null ? ShownByMode(o) : null);
    [RelayCommand] private void ToggleUnused() => ShowUnused = !ShowUnused;
    /// <summary>Whether the Objects side panel is shown (toolbar toggle).</summary>
    [ObservableProperty] public partial bool ShowObjectsPanel { get; set; } = true;

    partial void OnShowObjectsPanelChanged(bool value) => ViewerLayout?.SetPanelShown(Docking.RoomDockFactory.ObjectsId, value);

    /// <summary>The viewer's own panels (Preview, Objects). A new layout keeps the Objects toggle's state.</summary>
    public Docking.ViewerDock ViewerLayout { get; }

    public RoomViewModel()
    {
        ShowModImages = true; // the saved choice is applied after
        ViewerLayout = new Docking.ViewerDock(new Docking.RoomDockFactory(this));
        ViewerLayout.LayoutReplaced += () => ViewerLayout.SetPanelShown(Docking.RoomDockFactory.ObjectsId, ShowObjectsPanel);
    }

    /// <summary>
    /// Ctrl / Shift click in the room: the Objects list adds the entry to its selection (Shift) or toggles
    /// it (Ctrl). The list owns the multi-selection, so the canvas asks it.
    /// </summary>
    public event Action<RoomObjectItem, Avalonia.Input.KeyModifiers> ModifierSelectRequested;

    public void RequestModifierSelect(RoomObject obj, Avalonia.Input.KeyModifiers mods)
    {
        if (ItemFor(obj) is { } item)
        {
            ModifierSelectRequested?.Invoke(item, mods);
        }
    }

    public ObservableCollection<RoomObjectItem> Objects { get; } = [];

    [ObservableProperty] public partial RoomObjectItem SelectedObjectItem { get; set; }
    /// <summary>
    /// Objects whose image is drawn (a new set on every change, so bindings refresh). Listing the
    /// visible ones rather than the hidden ones means layers still on screen from the previous room,
    /// or from before the costumes were re-analysed, aren't drawn while the new ones decode.
    /// </summary>
    [ObservableProperty] public partial IReadOnlySet<RoomObject> VisibleObjects { get; set; } = new HashSet<RoomObject>();
    private bool _batchVisibility;

    /// <summary>Header checkbox: true when all are visible, false when none, null (mixed) otherwise.</summary>
    public bool? AllObjectsVisible
    {
        get
        {
            // Only objects with an image or costume have a toggle, so only they count.
            int total = Objects.Count(o => o.HasToggle);
            if (total == 0)
            {
                return true;
            }

            int visible = Objects.Count(o => o.HasToggle && o.IsVisible);
            return visible == total ? true : visible == 0 ? false : null;
        }
        set => SetAllVisible(value != false); // clicking the mixed state shows everything
    }

    public void SetAllVisible(bool visible) => SetVisibility(_ => visible);

    private Dictionary<RoomObject, int> _initialStates = [];
    /// <summary>States picked in the Object viewer, drawn instead of the state on entry.</summary>
    private readonly Dictionary<RoomObject, int> _shownStates = [];

    /// <summary>The state an object is drawn in: the one picked in the Object viewer, else its state on entry.</summary>
    public int StateOf(RoomObject obj) =>
        obj == null ? 0 : _shownStates.TryGetValue(obj, out int s) ? s : _initialStates.GetValueOrDefault(obj, 1);

    /// <summary>The state an object is in when the room opens.</summary>
    public int InitialStateOf(RoomObject obj) => obj == null ? 0 : _initialStates.GetValueOrDefault(obj, 1);

    /// <summary>
    /// Draws an object in another state (the Object viewer's state list): its image for that state, and
    /// its z-planes; state 0 hides it, any other shows it.
    /// </summary>
    public async Task ShowObjectStateAsync(RoomObject obj, int state)
    {
        if (obj == null || Room == null || !Room.Objects.Contains(obj))
        {
            return;
        }

        _shownStates[obj] = state;
        ObjectStateShown?.Invoke(obj);
        _objectPlanes.Remove(obj);
        _zMaskKey = null;
        if (state > 0 && obj.ImageCount > 0 && ObimDecoders.TryGetObimInfo(obj.Block, out var info))
        {
            int frame = RoomObjectStates.FrameFor(state, info.TotalFrames > 0 ? info.TotalFrames : 1);
            var loaded = await ObjectImageAsync(Room, obj, frame, CancellationToken.None);
            if (Room?.Objects.Contains(obj) != true)
            {
                return; // another room opened meanwhile
            }

            var set = new ImageSet();
            var image = set.Add(loaded.Image?.ToSkImage());
            if (ObjectLayers != null)
            {
                ObjectLayers = [.. ObjectLayers.Select(l => ReferenceEquals(l.Object, obj) && !l.IsActorOnly ? l with { Image = image, ImageX = loaded.X, ImageY = loaded.Y } : l)];
                // The image this one replaces goes with its set when it came from an earlier state change.
                _stateImages.Remove(obj, out var old);
                _stateImages[obj] = set;
                PublishImages();
                old?.Release();
            }
            else
            {
                set.Release();
            }
        }
        if (Objects.FirstOrDefault(o => ReferenceEquals(o.Object, obj)) is { HasToggle: true } item && item.IsVisible != state > 0)
        {
            item.IsVisible = state > 0; // refreshes the masks too
        }
        else
        {
            UpdateZMasks();
        }
    }
    private bool _startVisibilityApplied;

    /// <summary>Puts every entry's visibility back to what the Show mode starts a room with.</summary>
    [RelayCommand] private void ResetVisibility() => SetVisibility(o => ShownByMode(o));

    public void SetVisible(RoomObject obj, bool visible) => SetVisibility(o => ReferenceEquals(o.Object, obj) ? visible : null, byToggle: true);

    /// <summary>Shows only <paramref name="obj"/> and hides every other object.</summary>
    public void ShowOnly(RoomObject obj) => SetVisibility(o => ReferenceEquals(o.Object, obj), byToggle: true);

    public bool IsVisible(RoomObject obj) => VisibleObjects.Contains(obj);

    /// <summary>Applies a visibility per object (null leaves it unchanged), then refreshes once.</summary>
    /// <param name="byToggle">The user asked for it (a toggle, Show only…), not the Show mode: what it shows is drawn even where its kind is hidden.</param>
    private void SetVisibility(Func<RoomObjectItem, bool?> rule, bool byToggle = false)
    {
        _batchVisibility = true;
        foreach (var item in Objects)
        {
            if (rule(item) is bool v)
            {
                item.IsVisible = v;
                item.ShownByToggle = byToggle && v;
            }
        }

        _batchVisibility = false;
        RefreshVisibility();
    }

    private void OnItemChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RoomObjectItem.IsVisible) && !_batchVisibility)
        {
            // One entry's eye (or Space): the user's own toggle.
            if (sender is RoomObjectItem item) item.ShownByToggle = item.IsVisible;
            RefreshVisibility();
        }
    }

    private void RefreshVisibility()
    {
        VisibleObjects = Objects.Where(o => o.IsVisible).Select(o => o.Object).ToHashSet();
        ToggledObjects = Objects.Where(o => o.IsVisible && o.ShownByToggle).Select(o => o.Object).ToHashSet();
        UpdateZMasks();
        OnPropertyChanged(nameof(AllObjectsVisible));
        LoadShownLibraryImages();
    }

    /// <summary>
    /// A room with no background of its own: a library of images (room 3's inventory icons), not a
    /// scene. Its images start hidden and are loaded only once shown.
    /// </summary>
    private static bool IsImageLibrary(RoomModel room) => room.Width <= 0;

    private bool _loadingLibraryImages;

    /// <summary>
    /// In an image library, loads the images of objects shown since the room loaded without them, and
    /// puts them in the layers all at once.
    /// </summary>
    private async void LoadShownLibraryImages()
    {
        var room = Room;
        if (room == null || !IsImageLibrary(room) || _loadingLibraryImages || ObjectLayers == null || VisibleObjects == null)
        {
            return;
        }

        var missing = ObjectLayers.Where(l => !l.IsActorOnly && l.Image == null && l.Object.ImageCount > 0
                                              && VisibleObjects.Contains(l.Object) && !_stateImages.ContainsKey(l.Object)
                                              && room.Objects.Contains(l.Object))
            .Select(l => l.Object).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        _loadingLibraryImages = true;
        var loaded = new List<(RoomObject Object, ImageSet Set, SKImage Image, int? X, int? Y)>();
        try
        {
            foreach (var obj in missing)
            {
                SKImage image = null;
                int? x = null, y = null;
                var set = new ImageSet();
                if (ObimDecoders.TryGetObimInfo(obj.Block, out var info))
                {
                    int frame = RoomObjectStates.FrameFor(StateOf(obj), info.TotalFrames > 0 ? info.TotalFrames : 1);
                    var result = await ObjectImageAsync(room, obj, frame, CancellationToken.None);
                    image = set.Add(result.Image?.ToSkImage());
                    (x, y) = (result.X, result.Y);
                }
                loaded.Add((obj, set, image, x, y));
                if (!ReferenceEquals(Room, room))
                {
                    break; // another room opened meanwhile
                }
            }

            if (!ReferenceEquals(Room, room) || ObjectLayers == null)
            {
                foreach (var l in loaded) l.Set.Release();
                return;
            }

            var byObject = loaded.ToDictionary(l => l.Object);
            ObjectLayers = [.. ObjectLayers.Select(l => !l.IsActorOnly && byObject.TryGetValue(l.Object, out var x) ? l with { Image = x.Image, ImageX = x.X, ImageY = x.Y } : l)];
            // Owned like a state change's image: kept until the room reloads (an image that failed stays
            // recorded, so it isn't tried again on every visibility change).
            foreach (var l in loaded) _stateImages[l.Object] = l.Set;
            PublishImages();
        }
        catch (Exception ex)
        {
            Log.Error($"Library images failed: {ex.Message}");
            foreach (var l in loaded) l.Set.Release();
        }
        finally
        {
            _loadingLibraryImages = false;
        }

        LoadShownLibraryImages(); // any shown while these loaded
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
    [RelayCommand] private void ToggleHotspots() => ShowHotspots = !ShowHotspots;
    [RelayCommand] private void ToggleWalkboxes() => ShowWalkboxes = !ShowWalkboxes;
    [RelayCommand] private void ToggleLabels() => ShowLabels = !ShowLabels;
    [RelayCommand] private void ToggleBounds() => ShowBounds = !ShowBounds;
    [RelayCommand] private void ToggleSelectionOutline() => ShowSelectionOutline = !ShowSelectionOutline;
    [RelayCommand] private void ToggleZPlaneMasking() => ZPlaneMasking = !ZPlaneMasking;
    [RelayCommand] private void ToggleZPlanes() => ShowZPlanes = !ShowZPlanes;

    partial void OnZPlaneMaskingChanged(bool value) => UpdateZMasks();
    partial void OnShowZPlanesChanged(bool value) => UpdateZMasks();
    partial void OnShowFullScreenObjectsChanged(bool value) => UpdateZMasks();
    partial void OnBackgroundObjectChanged(RoomObject value) => UpdateZMasks();

    private ZPlane[] _roomPlanes = [];
    private int _planeCount;
    private readonly Dictionary<RoomObject, ZPlane[]> _objectPlanes = [];
    private string _zMaskKey;

    /// <summary>Rebuilds <see cref="ZMasks"/> from the room's planes and the objects shown now.</summary>
    private void UpdateZMasks()
    {
        var room = Room;
        if (!(ZPlaneMasking || ShowZPlanes) || room == null || _planeCount == 0 || room.Width <= 0)
        {
            _zMaskKey = null;
            ZMasks = null;
            ReplaceImages(ref _maskImages, null);
            return;
        }
        // Objects write their planes over the room's while they're drawn (shown, with an image), blank
        // parts included. A full-screen image the canvas doesn't draw (Full-screen objects off) mustn't
        // count: shown by the Show mode's Everything, its empty planes would wipe the room's. The image
        // picked as the background is drawn, so its planes do count.
        var shown = Objects.Where(o => !o.IsActorOnly && o.Object.ImageCount > 0
                                       && (ReferenceEquals(o.Object, BackgroundObject)
                                           || (o.IsVisible && (ShowFullScreenObjects || o.ShownByToggle || o.Object.Width < 640))))
            .Select(o => o.Object).ToList();
        shown = [.. room.InDrawOrder(shown)]; // an object drawn later writes its planes over an earlier one's
        string key = room.Room.Offset + ":" + string.Join(",", shown.Select(o => $"{o.Index}/{StateOf(o)}"));
        if (key == _zMaskKey && ZMasks != null)
        {
            return; // a costume or hotspot toggled: the masks are the same
        }

        _zMaskKey = key;
        var planes = RoomScene.Planes(room, shown.Select(o => (o, Math.Max(0, StateOf(o) - 1))), (o, image) =>
        {
            if (!_objectPlanes.TryGetValue(o, out var p))
            {
                try { p = ZPlanes.Object(o, image); }
                catch (Exception ex) { Log.Warn($"Z-planes of {o}: {ex.Message}"); p = []; }
                _objectPlanes[o] = p;
            }
            return p;
        }, _roomPlanes);
        var maskSet = new ImageSet();
        var masks = new SKImage[planes.Length];
        for (int k = 0; k < planes.Length; k++)
        {
            masks[k] = maskSet.Add(RoomScene.MaskImage(planes[k], room.Width, room.Height));
        }

        ZMasks = masks;
        ReplaceImages(ref _maskImages, maskSet);
    }

    [RelayCommand] private void ToggleFullScreenObjects() => ShowFullScreenObjects = !ShowFullScreenObjects;

    private List<BackgroundChoice> _backgroundChoices = [];
    /// <summary>The room's costumed actors (from its scripts), with their parsed costumes and default frames.</summary>
    private List<RoomActor> _actors = [];
    /// <summary>Where each drawable actor is shown: its object's entry, or its own stand-in entry.</summary>
    private Dictionary<RoomActor, RoomObject> _actorTargets = [];
    private readonly AkosParser _akosParser = new();

    /// <summary>The room's costumes (one per AKOS), for the Link costume menu.</summary>
    public IReadOnlyList<RoomActor> RoomCostumes => [.. _actors
        .GroupBy(a => a.Akos).Select(g => g.OrderByDescending(a => a.CanDraw).First())
        .OrderBy(a => a.Costume)];

    /// <summary>Raised after a manual link changes (so it can be saved and the hierarchy relabelled).</summary>
    public event Action LinksChanged;

    /// <summary>Links <paramref name="obj"/> to a costume (0 = none) or back to automatic matching (null).</summary>
    public async Task SetCostumeLinkAsync(RoomObject obj, int? costume)
    {
        var room = Room;
        if (room == null || obj == null || obj.Index < 0)
        {
            return;
        }

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
    public bool HasBackgroundChoices => MaxBackgroundIndex > 0;

    /// <summary>
    /// A room switch this soon after the last one is scrolling: a held key (key repeat is every 25–50 ms;
    /// X11's default 40 ms). Pressing keys or clicking by hand, however quickly, is slower than this.
    /// </summary>
    private static readonly TimeSpan ScrollingGap = TimeSpan.FromMilliseconds(90);
    /// <summary>
    /// While scrolling, a room loads once it has stayed selected for 1.5 times the gap between the last two
    /// switches (the key repeat), within these limits: a held key never waits for a load, and stopping
    /// shows the room soon after.
    /// </summary>
    private static readonly TimeSpan MinSettle = TimeSpan.FromMilliseconds(50), MaxSettle = TimeSpan.FromMilliseconds(150);
    private long _lastSwitch;

    /// <summary>A switch that takes longer than this covers the old room with black until the new one is ready.</summary>
    private static readonly TimeSpan SlowSwitch = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// The room is drawn as black: scrolled to, or slow to load, and not ready yet. It then appears whole,
    /// so nothing pops in later (the cut through black the game makes between rooms).
    /// </summary>
    [ObservableProperty] public partial bool IsCovered { get; set; }

    /// <summary>
    /// Opens a room. The old room stays on screen until the new one is ready, then everything (background,
    /// objects, costumes) appears at once, as entering a room in the game. Scrolling through rooms (a held
    /// key) would never get there, so then each room shows its background straight away and the rest
    /// waits until the selection settles.
    /// </summary>
    public async Task LoadAsync(RoomModel room)
    {
        if (room == null || ReferenceEquals(room.Room, Room?.Room))
        {
            return;
        }

        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        var gap = _lastSwitch != 0 ? System.Diagnostics.Stopwatch.GetElapsedTime(_lastSwitch, now) : TimeSpan.MaxValue;
        // Only the gap counts: a press that lands while the last room is still loading is a normal switch
        // (it goes black only if it's slow, or stays black if the last one was).
        bool scrolling = gap < ScrollingGap;
        _lastSwitch = now;
        // The last room's images mustn't arrive over this one (it may wait to settle before loading its own).
        _cts?.Cancel();

        IsSwitchingRoom = true;
        LiveCostume = null;
        ReplaceImages(ref _liveImages, null);
        // The old room's state-changed images stay on screen until this room's images are ready.
        _retiredImages.AddRange(_stateImages.Values);
        _stateImages.Clear();
        Room = room;
        OnPropertyChanged(nameof(HasRoom));
        Title = room.RoomNo > 0
            ? $"Room {room.RoomNo}{(string.IsNullOrEmpty(room.Name) ? "" : $" — {room.Name}")}  ({room.Width}×{room.Height})"
            : $"Room @0x{room.Room.Offset:X8}  ({room.Width}×{room.Height})";

        RoomSize = new Size(room.Width, room.Height);
        Walkboxes = room.Walkboxes;
        ClearObjects();
        if (!scrolling)
        {
            FillObjects(room);
            CoverIfSlow(room);
        }

        _resetBackgroundChoice = true;

        // With objects and costumes hidden nothing waits on the costume analysis: show the new
        // background as soon as it's decoded and let the analysis finish behind it.
        if (ShowMode == ShowNone)
        {
            await ShowBackgroundFirstAsync(room);
            if (!ReferenceEquals(Room, room))
            {
                return;
            }
        }
        else if (scrolling)
        {
            // Scrolling: the room shows as black at once, and loads only if the selection stays on it.
            // The black stays until the room is ready, then it all appears together.
            ObjectLayers = [];
            BackgroundObject = null;
            BackgroundImage = null;
            IsCovered = true;
            IsSwitchingRoom = false;
        }

        if (scrolling)
        {
            // The Objects list (and the visibility it sets up) waits for the selection to settle too.
            var settle = gap < ScrollingGap ? gap * 1.5 : MaxSettle;
            await Task.Delay(settle < MinSettle ? MinSettle : settle > MaxSettle ? MaxSettle : settle);
            if (!ReferenceEquals(Room, room))
            {
                return; // scrolled on
            }

            if (ShowMode != ShowNone)
            {
                IsSwitchingRoom = true; // hold the black until the room is ready
            }

            FillObjects(room);
        }

        await RefreshActorsAsync(room);
        if (!ReferenceEquals(Room, room))
        {
            return;
        }

        await ReloadImagesAsync();
    }

    /// <summary>
    /// Covers the old room with black if the switch to <paramref name="room"/> is still going after
    /// <see cref="SlowSwitch"/>. A UI-thread timer, so a busy thread pool (the load itself) can't hold it up.
    /// </summary>
    private void CoverIfSlow(RoomModel room)
    {
        Avalonia.Threading.DispatcherTimer.RunOnce(() =>
        {
            if (ReferenceEquals(Room, room) && IsSwitchingRoom)
            {
                IsCovered = true; // the switch ends it when the room is ready
            }
        }, SlowSwitch);
    }

    /// <summary>Empties the Objects list and forgets the last room's planes and object states.</summary>
    private void ClearObjects()
    {
        foreach (var item in Objects)
        {
            item.PropertyChanged -= OnItemChanged;
        }

        Objects.Clear();
        _initialStates = [];
        _shownStates.Clear();
        _startVisibilityApplied = false;
        _objectPlanes.Clear();
        _zMaskKey = null;
        _roomPlanes = [];
        _planeCount = 0;
        SelectedObject = null;
        RefreshVisibility();
    }

    /// <summary>Lists the room's objects as it starts (their states, visibility and z-planes).</summary>
    private void FillObjects(RoomModel room)
    {
        // Object images start as the game shows them on entry: hidden while their state is 0.
        Dictionary<RoomObject, int> states;
        try { states = RoomObjectStates.Initial(room); }
        catch (Exception ex) { Log.Warn($"Object states for room {room.RoomNo}: {ex.Message}"); states = []; }
        _initialStates = states;
        _shownStates.Clear();
        _startVisibilityApplied = false;
        _objectPlanes.Clear();
        _zMaskKey = null;
        try
        {
            _roomPlanes = ZPlanes.Room(room);
            _planeCount = ZPlanes.Count(room);
            ZPlaneChoices = ["All planes", .. Enumerable.Range(1, _planeCount).Select(n => $"Plane {n}")];
            if (ZPlaneShown > _planeCount)
            {
                ZPlaneShown = 0;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Z-planes for room {room.RoomNo}: {ex.Message}");
            _roomPlanes = [];
            _planeCount = 0;
        }
        foreach (var o in room.Objects)
        {
            int? state = states.TryGetValue(o, out int st) ? st : null;
            // Image-less hotspots have nothing to hide (their costume shows). A room with no background is a
            // library of images, not a scene (room 3's 170 inventory icons, all in state 0, at one spot):
            // they start hidden, and only the ones shown are loaded (see IsImageLibrary). Its full-screen
            // images start shown, so it opens on its backdrop (room 3's treasure chest) rather than empty.
            bool shown = o.ImageCount <= 0 || (IsImageLibrary(room) ? o.Width >= FullWidth : state != 0);
            // A room starts as the Show mode says (Room start: as the game shows it).
            var item = new RoomObjectItem(o) { InitialState = state, InitialVisible = shown, Unused = UnusedArt.Find(room.RoomNo, o.Name) };
            item.IsVisible = ShownByMode(item);
            item.PropertyChanged += OnItemChanged;
            Objects.Add(item);
        }
        // An object of this room selected before the list was filled (while scrolling) stays selected.
        var keep = SelectedObject != null && room.Objects.Contains(SelectedObject) ? SelectedObject : null;
        SelectedObject = keep;
        SelectedObjectItem = Objects.FirstOrDefault(o => ReferenceEquals(o.Object, keep));
        RefreshVisibility();
    }

    /// <summary>
    /// The palette an object image is drawn in: the one the game shows it in (stancrpt's office in
    /// palette 1), else the room's own palette 0 (<see cref="RoomPalettes"/>).
    /// </summary>
    public int PaletteOf(RoomObject obj) => RoomSceneBuilder.PaletteOf(Room, obj);

    /// <summary>
    /// The mods beside the game (set when a game is opened): an object image a mod has (<see cref="ObjectImageMods"/>)
    /// is shown in place of the game's, where the mod puts it, as the game window shows it.
    /// </summary>
    public ObjectImageMods ObjectMods { get; set; }

    /// <summary>The mods' costume cels (set when a game is opened), drawn in the placed costumes while <see cref="ShowModImages"/> is on.</summary>
    public CostumeCelMods CostumeMods { get; set; }

    /// <summary>
    /// Whether the mods' object images and costume cels are shown (<see cref="ObjectMods"/>, <see cref="CostumeMods"/>);
    /// the game's when off. A change reloads the room's images.
    /// </summary>
    [ObservableProperty] public partial bool ShowModImages { get; set; }

    partial void OnShowModImagesChanged(bool value)
    {
        lock (_moddedCostumes) _moddedCostumes.Clear(); // read the mods' PNGs again
        ReloadForModImages();
    }

    /// <summary>
    /// The mod image shown for <paramref name="obj"/>, while mod images are shown; else null. It's the image the
    /// viewer draws for it: its state's, or the first for one hidden in state 0 (shown from the Objects list).
    /// </summary>
    public ObjectImageMods.Image ModImageOf(RoomObject obj)
    {
        if (!ShowModImages || ObjectMods is not { } mods || Room is not { } room || obj == null || obj.ImageCount <= 0) return null;
        if (!ObimDecoders.TryGetObimInfo(obj.Block, out var info)) return null;
        return mods.Find(room, obj, RoomObjectStates.FrameFor(StateOf(obj), info.TotalFrames > 0 ? info.TotalFrames : 1));
    }

    /// <summary>Raised when an object is shown in another state (the Object viewer's state list), so its image may be another.</summary>
    public event Action<RoomObject> ObjectStateShown;

    /// <summary>Shows <paramref name="obj"/>'s image with its top-left at <paramref name="x"/>,<paramref name="y"/> (unsaved: the next reload puts it back).</summary>
    public void PreviewObjectPlace(RoomObject obj, int x, int y)
    {
        if (ObjectLayers == null || obj == null) return;
        ObjectLayers = [.. ObjectLayers.Select(l => ReferenceEquals(l.Object, obj) && !l.IsActorOnly ? l with { ImageX = x, ImageY = y } : l)];
    }

    /// <summary>
    /// Moves cel <paramref name="cel"/>'s mod cel to offset <paramref name="x"/>,<paramref name="y"/> in the room's own
    /// copy of the costume in <paramref name="akos"/>, and draws again every place the room puts that costume
    /// (unsaved: the next reload reads meta.json).
    /// </summary>
    public void PreviewCelOffset(ScummBlock akos, int cel, int x, int y)
    {
        if (ObjectLayers == null || ModdedCostume(akos) is not { } modded) return;
        lock (modded)
        {
            if (modded.ModCels?.GetValueOrDefault(cel) is not { } mod) return;
            modded.ModCels = new Dictionary<int, AkosModCel>(modded.ModCels) { [cel] = mod with { OffsetX = x, OffsetY = y } };
        }

        var set = new ImageSet();
        var redrawn = new Dictionary<RoomObject, (SKImage Image, int X, int Y)>();
        foreach (var (actor, target) in _actorTargets)
        {
            if (!ReferenceEquals(actor.Akos, akos) || !actor.CanDraw) continue;
            try
            {
                if (ModdedFrame(actor, modded, CancellationToken.None) is var (image, fx, fy))
                    redrawn[target] = (set.Add(image.ToSkImage()), fx, fy);
            }
            catch (Exception ex) { Log.Warn($"Costume {actor.Costume}: {ex.Message}"); }
        }
        if (redrawn.Count == 0)
        {
            set.Release();
            return;
        }

        ObjectLayers = [.. ObjectLayers.Select(l => l.CostumeImage != null && redrawn.TryGetValue(l.Object, out var r)
            ? l with { CostumeImage = r.Image, CostumeX = r.X, CostumeY = r.Y } : l)];
        ReplaceImages(ref _editImages, set);
    }

    /// <summary>Reads the mods again and reloads the room's images (after a mod's place is saved).</summary>
    public void ReloadModImages()
    {
        lock (_moddedCostumes) _moddedCostumes.Clear();
        ReloadForModImages();
    }

    /// <summary>Each placed costume with the mods' cels, a parse of its own (the room's analysis keeps the game's), or null without any.</summary>
    private readonly Dictionary<ScummBlock, AkosData> _moddedCostumes = [];

    /// <summary>The costume in <paramref name="akos"/> with the mods' cels, while they're shown and it has some; else null.</summary>
    private AkosData ModdedCostume(ScummBlock akos)
    {
        if (!ShowModImages || CostumeMods is not { } mods || akos == null) return null;
        lock (_moddedCostumes)
        {
            if (_moddedCostumes.TryGetValue(akos, out var cached)) return cached;
            AkosData d = null;
            if (mods.Find(akos).Count > 0)
            {
                d = AkosParser.Parse(akos);
                mods.Apply(d, akos);
                lock (mods.Problems)
                {
                    foreach (var problem in mods.Problems) Log.Warn($"Costume mods: {problem}");
                    mods.Problems.Clear();
                }
                if (d.ModCels == null) d = null;
            }
            return _moddedCostumes[akos] = d;
        }
    }

    [RelayCommand] private void ToggleModImages() => ShowModImages = !ShowModImages;

    private async void ReloadForModImages()
    {
        try { await ReloadImagesAsync(); }
        catch (Exception ex) { Log.Warn($"Room images: {ex.Message}"); }
    }

    /// <summary>Image <paramref name="frame"/> of an object: a mod's, with where it goes, or the game's (no place: the object's).</summary>
    private async Task<(RgbaImage Image, int? X, int? Y)> ObjectImageAsync(RoomModel room, RoomObject obj, int frame, CancellationToken token)
    {
        int palette = RoomSceneBuilder.PaletteOf(room, obj);
        if (ShowModImages && ObjectMods is { } mods && mods.Find(room, obj, frame) != null)
        {
            // A mod's see-through pixels (shadows) are blended over the room's background, as the game window does.
            var background = room.Width > 0
                ? (await ScummBackgroundCache.GetCachedBackgroundAsync(RoomSceneBuilder.BackgroundCacheId(room), room.Rmhd, room.Room, cacheDisabled: false, 0, token)).Surface
                : null;
            var modded = await Task.Run(() => mods.Rgba(room, obj, frame, palette, background), token);
            lock (mods.Problems)
            {
                foreach (var problem in mods.Problems) Log.Warn($"Object mods: {problem}");
                mods.Problems.Clear();
            }
            if (modded is var (image, x, y)) return (image, x, y);
        }
        var result = await ObimCache.GetCachedObimAsync(obj.Block, frame, palette, bakeImage: true, token);
        return (result.ExecutedImage, null, null);
    }

    /// <summary>The costume and palette the frames played in place are resolved in.</summary>
    private (AkosData, int) _livePalette;

    /// <summary>
    /// Shows <paramref name="frame"/> of costume <paramref name="akos"/> where this room places that
    /// costume, in place of its usual frame and in palette <paramref name="palette"/> (the one the Costume
    /// viewer shows it in); null (or a costume this room doesn't place) clears it.
    /// </summary>
    public void ShowLiveFrame(ScummBlock akos, AkosFrame frame, int palette)
    {
        var placed = frame == null || akos == null ? default
            : _actorTargets.FirstOrDefault(kv => ReferenceEquals(kv.Key.Akos, akos) && kv.Key.HasPosition);
        if (placed.Key is not { } actor)
        {
            LiveCostume = null;
            ReplaceImages(ref _liveImages, null);
            _livePalette = default;
            return;
        }
        try
        {
            RgbaImage rgba;
            int ox, oy;
            // The room's own parse of the costume; the next reload puts it back in the room's palette.
            // With the mods' cels when they're shown: the Costume viewer composes its frames from the costume with them.
            var data = ModdedCostume(actor.Akos) ?? actor.Data;
            lock (data)
            {
                if (_livePalette != (data, palette))
                {
                    AkosParser.UpdatePaletteOnly(data, actor.Akos, palette);
                    _livePalette = (data, palette);
                }
                // Scaled as the engine scales it, so it's drawn at full size.
                rgba = AkosFrames.RenderScaled(data, frame, actor.Scale, out ox, out oy, mirror: actor.Mirrored);
            }
            var set = new ImageSet();
            LiveCostume = new LiveCostume(placed.Value, set.Add(rgba?.ToSkImage()), actor.X - ox, actor.DrawY - oy, 1, actor.ZPlane);
            ReplaceImages(ref _liveImages, set);
        }
        catch (Exception ex)
        {
            Log.Error($"Play in place failed: {ex.Message}");
            LiveCostume = null;
            ReplaceImages(ref _liveImages, null);
        }
    }

    private static int BackgroundCacheId(RoomModel room) => RoomSceneBuilder.BackgroundCacheId(room);

    /// <summary>Shows the room's own background (with no object layers yet) and ends the room switch.</summary>
    private async Task ShowBackgroundFirstAsync(RoomModel room)
    {
        try
        {
            var (image, _) = await RoomBackgroundAsync(room, CancellationToken.None);
            if (!ReferenceEquals(Room, room) || image == null)
            {
                return; // no image of its own: wait for the full load
            }

            ObjectLayers = []; // the old room's labels and hotspots go too
            BackgroundObject = null;
            BackgroundImage = image;
            BackgroundLabel = "room";
            IsCovered = false;
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
            actors = [];
            Log.Error($"Costume analysis failed: {ex.Message}");
        }
        if (!ReferenceEquals(Room, room))
        {
            return;
        }

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
        _actorTargets = [];
        // The first analysis of a room sets how its costumes start (later ones, after a link change, keep
        // what the user toggled): a hotspot's costume shows only if it's there as the room opens.
        bool applyStart = !_startVisibilityApplied;
        _startVisibilityApplied = true;
        foreach (var item in Objects)
        {
            item.Costume = _actors.FirstOrDefault(a => a.Object != null && ReferenceEquals(a.Object.Block, item.Object.Block));
            if (item.Costume is { CanDraw: true } a)
            {
                _actorTargets[a] = item.Object;
            }

            if (applyStart && item.Object.ImageCount <= 0 && item.Costume is { CanDraw: true } c)
            {
                item.InitialVisible = c.StartsShown;
                item.IsVisible = ShownByMode(item);
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
                // There as the room opens only when the game has it there then (not the player, not a
                // cutscene's or a verb's costume); the Show mode decides whether it starts shown.
                var item = new RoomObjectItem(standIn) { IsActorOnly = true, Costume = actor, InitialVisible = actor.StartsShown };
                item.IsVisible = ShownByMode(item);
                item.PropertyChanged += OnItemChanged;
                Objects.Add(item);
                if (actor.CanDraw)
                {
                    _actorTargets[actor] = standIn;
                }
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
        var marks = room == null ? [] : ReviewMarks.Load().Where(m => m.Room == room.RoomNo).ToList();
        foreach (var item in Objects)
        {
            var target = TargetOf(item);
            var mine = target == null ? [] : marks.Where(m => m.Includes(room.RoomNo, target)).ToList();
            item.ReviewMarks = mine;
        }
    }

    /// <summary>
    /// An entry as a review target, with what MMUCS works out for it now: a costume (its placement,
    /// scale, z-plane, owner) when it has one, else the object image (its box and state on entry).
    /// </summary>
    public ReviewTarget TargetOf(RoomObjectItem item)
    {
        if (Room == null || item == null)
        {
            return null;
        }

        if (item.Costume is { } a)
        {
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
        }

        if (item.IsActorOnly)
        {
            return null;
        }

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
        if (room == null || targets.Count == 0)
        {
            return null;
        }

        return new ReviewMark { Room = room.RoomNo, RoomName = room.Name, Targets = targets };
    }

    /// <summary>Marks these entries together (one mark: a pair drawn in the wrong order, say).</summary>
    public void MarkForReview(IReadOnlyList<RoomObjectItem> items, IEnumerable<string> tags, string note)
    {
        var mark = NewMark(items);
        if (mark == null)
        {
            return;
        }

        mark.Tags = [.. tags];
        mark.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        ReviewMarks.Add(mark);
        ApplyReviewMarks();
    }

    /// <summary>Clears every mark that includes any of these entries.</summary>
    public void ClearReviewMarks(IReadOnlyList<RoomObjectItem> items)
    {
        foreach (int id in items.SelectMany(i => i.ReviewMarks).Select(m => m.Id).Distinct().ToList())
        {
            ReviewMarks.Remove(id);
        }

        ApplyReviewMarks();
    }

    // ── Selection: several entries can be selected; the last one is the current one ──

    /// <summary>The entries selected in the Objects panel (the current one, SelectedObjectItem, among them).</summary>
    public IReadOnlyList<RoomObjectItem> SelectedItems
    {
        get => _selectedItems;
        set
        {
            _selectedItems = value ?? [];
            SelectedObjects = _selectedItems.Select(i => i.Object).ToHashSet();
        }
    }
    private IReadOnlyList<RoomObjectItem> _selectedItems = [];

    /// <summary>The selected entries' objects, for the canvas to outline.</summary>
    [ObservableProperty] public partial IReadOnlySet<RoomObject> SelectedObjects { get; set; } = new HashSet<RoomObject>();

    /// <summary>The entries an action on <paramref name="item"/> applies to: the selection when it holds it, else just it.</summary>
    public IReadOnlyList<RoomObjectItem> ActionItems(RoomObjectItem item) =>
        item != null && SelectedItems.Count > 1 && SelectedItems.Contains(item) ? SelectedItems : item == null ? Array.Empty<RoomObjectItem>() : [item];

    /// <summary>Shows or hides several entries at once.</summary>
    public void SetVisible(IReadOnlyList<RoomObjectItem> items, bool visible) =>
        SetVisibility(o => items.Contains(o) ? visible : null, byToggle: true);

    /// <summary>Shows these entries and hides every other one.</summary>
    public void ShowOnly(IReadOnlyList<RoomObjectItem> items) => SetVisibility(o => items.Contains(o), byToggle: true);

    /// <summary>Renders the actors' frames, each in the palette the game shows its costume in, keyed by where they're shown.</summary>
    private Dictionary<RoomObject, (SKImage Image, int X, int Y, int Depth, int Actor, float Scale, int ZPlane)> RenderActors(
        Dictionary<RoomActor, RoomObject> targets, ImageSet images, CancellationToken token)
    {
        var result = new Dictionary<RoomObject, (SKImage, int, int, int, int, float, int)>();
        foreach (var (actor, target) in targets)
        {
            if (token.IsCancellationRequested)
            {
                break;
            }

            // As the player draws it: scaled as the engine scales it, each cel on its own with SCUMM's scale
            // table, so it's drawn at full size (stretching the composed frame left a seam between cannon's
            // gunner pieces), in the costume's default palette.
            var frame = ModdedCostume(actor.Akos) is { } modded && actor.CanDraw
                ? ModdedFrame(actor, modded, token)
                : RoomSceneBuilder.ActorFrame(actor, token);
            lock (actor.Data)
            {
                if (ReferenceEquals(_livePalette.Item1, actor.Data))
                {
                    _livePalette = default;
                }
            }
            if (frame is { } f)
            {
                result[target] = (images.Add(f.Image.ToSkImage()), f.X, f.Y, actor.SortY, actor.Actor, 1f, actor.ZPlane);
            }
        }
        return result;
    }

    /// <summary>A placed costume's frame drawn with the mods' cels, each moved by its offset; as <see cref="RoomSceneBuilder.ActorFrame"/>.</summary>
    private static (RgbaImage Image, int X, int Y)? ModdedFrame(RoomActor actor, AkosData modded, CancellationToken token)
    {
        lock (modded)
        {
            AkosParser.UpdatePaletteOnly(modded, actor.Akos, modded.DefaultPaletteIndex);
            var rgba = AkosFrames.RenderScaled(modded, AkosFrames.WithModCels(modded, actor.Frame), actor.Scale, out int ox, out int oy, actor.Mirrored, token);
            return rgba == null ? null : (rgba, actor.X - ox, actor.DrawY - oy);
        }
    }

    private async Task ReloadImagesAsync()
    {
        var room = Room;
        if (room == null)
        {
            return;
        }

        lock (_moddedCostumes) _moddedCostumes.Clear(); // the mods' cels are read again with the room's images

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        IsBusy = true;
        // This load's object and costume images; freed again unless the load finishes and shows them.
        var images = new ImageSet();
        bool shown = false;

        try
        {
            var (roomBackgroundImage, roomBackground) = await RoomBackgroundAsync(room, token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            var targets = _actorTargets;
            var costumes = await Task.Run(() => RenderActors(targets, images, token), token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            var layers = new List<RoomObjectLayer>(room.Objects.Count + targets.Count);
            var fullWidth = new List<BackgroundChoice>();
            var hasImage = new Dictionary<RoomObject, bool>();
            // In the engine's draw order: the canvas draws the layers in list order, later ones on top.
            var shownNow = VisibleObjects;
            foreach (var obj in room.InDrawOrder(room.Objects))
            {
                SKImage image = null;
                RgbaImage rgba = null;
                int? imageX = null, imageY = null;
                // An image library loads only what's shown; the rest load when shown (LoadShownLibraryImages).
                bool skip = IsImageLibrary(room) && shownNow?.Contains(obj) != true;
                if (skip)
                {
                    hasImage[obj] = obj.ImageCount > 0; // so it can be toggled on
                }
                else if (obj.ImageCount > 0 && ObimDecoders.TryGetObimInfo(obj.Block, out var info))
                {
                    // In the palette the game shows it in: SCUMM v8 has no per-object palette, the game
                    // switches the room's palette from scripts before it shows one (RoomPalettes).
                    // The image for its state on entry (state N shows image N); the first when it starts hidden.
                    int frame = RoomObjectStates.FrameFor(StateOf(obj), info.TotalFrames > 0 ? info.TotalFrames : 1);
                    var result = await ObjectImageAsync(room, obj, frame, token);
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    rgba = result.Image;
                    (imageX, imageY) = (result.X, result.Y);
                    image = images.Add(rgba?.ToSkImage());
                }
                costumes.TryGetValue(obj, out var costume);
                layers.Add(new RoomObjectLayer(obj, image, costume.Image, costume.X, costume.Y, CostumeDepth: costume.Depth, CostumeActor: costume.Actor, CostumeScale: costume.Scale == 0 ? 1 : costume.Scale, CostumeZPlane: costume.ZPlane,
                    ImageX: imageX, ImageY: imageY));
                if (!skip)
                {
                    hasImage[obj] = rgba != null && !IsFullyTransparent(rgba);
                }
                if (image != null && image.Width >= FullWidth)
                {
                    fullWidth.Add(new BackgroundChoice(obj.ToString(), image, rgba, obj));
                }
            }

            // Stand-ins for costumes no object claims.
            foreach (var item in Objects.Where(o => o.IsActorOnly))
            {
                if (costumes.TryGetValue(item.Object, out var c))
                {
                    layers.Add(new RoomObjectLayer(item.Object, null, c.Image, c.X, c.Y, IsActorOnly: true, CostumeDepth: c.Depth, CostumeActor: c.Actor, CostumeScale: c.Scale, CostumeZPlane: c.ZPlane));
                }
            }

            // Background choices: the room's own image first, then every full-width object image.
            var choices = new List<BackgroundChoice>();
            if (roomBackgroundImage != null)
            {
                choices.Add(new BackgroundChoice("room", roomBackgroundImage, roomBackground, null));
            }

            choices.AddRange(fullWidth);

            ObjectLayers = layers;
            foreach (var item in Objects.Where(o => !o.IsActorOnly))
            {
                item.HasImage = hasImage.TryGetValue(item.Object, out var h) && h;
            }

            OnPropertyChanged(nameof(AllObjectsVisible));
            _backgroundChoices = choices;
            MaxBackgroundIndex = Math.Max(0, choices.Count - 1);
            // A new room starts on its first choice (its own image, or e.g. the inventory backdrop);
            // a reload keeps the current choice.
            int index = _resetBackgroundChoice ? 0 : Math.Clamp(BackgroundIndex, 0, MaxBackgroundIndex);
            _resetBackgroundChoice = false;
            if (BackgroundIndex != index)
            {
                BackgroundIndex = index; // applies via OnBackgroundIndexChanged
            }
            else
            {
                ApplyBackgroundChoice();
            }

            // Everything shown now is this load's (or the room's background): the earlier load's images,
            // state changes and the previous room's sets can go.
            var stale = new List<ImageSet>(_retiredImages) { _loadImages, _editImages };
            _editImages = null;
            stale.AddRange(_stateImages.Values);
            if (!ReferenceEquals(_background.Room, room))
            {
                stale.Add(_background.Set); // this room has no background of its own
                _background = default;
            }
            _retiredImages.Clear();
            _stateImages.Clear();
            _loadImages = images;
            shown = true;
            PublishImages();
            foreach (var set in stale) set?.Release();

            IsCovered = false;
            IsSwitchingRoom = false; // background, objects and overlays all show together
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error($"Room decode failed: {ex.Message}");
        }
        finally
        {
            if (!shown)
            {
                images.Release();
            }

            if (!token.IsCancellationRequested)
            {
                IsBusy = false;
                IsCovered = false;
                IsSwitchingRoom = false;
            }
        }
    }

    /// <summary>Whether an image has no visible pixel.</summary>
    private static bool IsFullyTransparent(RgbaImage image)
    {
        var data = image.Data;
        for (int i = 3; i < data.Length; i += 4)
        {
            if (data[i] != 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Toggles the visibility of the selected object (Space in the Objects list).</summary>
    public void ToggleSelectedVisibility()
    {
        // Several selected: all follow the current one's new state.
        var items = ActionItems(SelectedObjectItem).Where(o => o.HasToggle).ToList();
        if (items.Count > 1) { SetVisible(items, !SelectedObjectItem.IsVisible); return; }
        if (SelectedObjectItem is { HasToggle: true } item)
        {
            item.IsVisible = !item.IsVisible;
        }
    }

    /// <summary>Each background is already in its own palette (the room's 0, stancrpt's office 1).</summary>
    partial void OnBackgroundIndexChanged(int value) => ApplyBackgroundChoice();

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
        if (choice?.Rgba == null || Room == null)
        {
            return;
        }

        string room = Room.RoomNo > 0 ? Room.RoomNo.ToString("D3") : Room.Room.Offset.ToString("X8");
        string suffix = choice.Object == null ? "room" : string.Join("_", choice.Label.Split(Path.GetInvalidFileNameChars()));
        string path = Path.Combine(folder, $"room_{room}_{suffix}.png");
        choice.Rgba.SavePng(path);
        Log.Info($"Exported {path}");
    }
}
