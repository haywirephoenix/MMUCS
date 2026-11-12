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
    public string Summary => Object.Summary;
    [ObservableProperty] private bool _isVisible = true;
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

    [ObservableProperty] private bool _showBackground = true;
    [ObservableProperty] private bool _showObjects = true;
    [ObservableProperty] private bool _showHotspots;
    [ObservableProperty] private bool _showWalkboxes;
    [ObservableProperty] private bool _showLabels = true;
    [ObservableProperty] private bool _showBounds;
    [ObservableProperty] private bool _showFullScreenObjects;

    public ObservableCollection<RoomObjectItem> Objects { get; } = new();

    [ObservableProperty] private RoomObjectItem _selectedObjectItem;
    /// <summary>Objects whose image is hidden (a new set on every change, so bindings refresh).</summary>
    [ObservableProperty] private IReadOnlySet<RoomObject> _hiddenObjects = new HashSet<RoomObject>();
    private bool _batchVisibility;

    /// <summary>Header checkbox: true when all are visible, false when none, null (mixed) otherwise.</summary>
    public bool? AllObjectsVisible
    {
        get
        {
            if (Objects.Count == 0) return true;
            int visible = Objects.Count(o => o.IsVisible);
            return visible == Objects.Count ? true : visible == 0 ? false : null;
        }
        set => SetAllVisible(value != false); // clicking the mixed state shows everything
    }

    public void SetAllVisible(bool visible) => SetVisibility(_ => visible);

    public void SetVisible(RoomObject obj, bool visible) => SetVisibility(o => ReferenceEquals(o, obj) ? visible : null);

    /// <summary>Shows only <paramref name="obj"/> and hides every other object.</summary>
    public void ShowOnly(RoomObject obj) => SetVisibility(o => ReferenceEquals(o, obj));

    public bool IsVisible(RoomObject obj) => !HiddenObjects.Contains(obj);

    /// <summary>Applies a visibility per object (null leaves it unchanged), then refreshes once.</summary>
    private void SetVisibility(Func<RoomObject, bool?> rule)
    {
        _batchVisibility = true;
        foreach (var item in Objects)
            if (rule(item.Object) is bool v) item.IsVisible = v;
        _batchVisibility = false;
        RefreshVisibility();
    }

    private void OnItemChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RoomObjectItem.IsVisible) && !_batchVisibility) RefreshVisibility();
    }

    private void RefreshVisibility()
    {
        HiddenObjects = Objects.Where(o => !o.IsVisible).Select(o => o.Object).ToHashSet();
        OnPropertyChanged(nameof(AllObjectsVisible));
    }

    // Keep the list selection and the canvas selection (a RoomObject) in step.
    partial void OnSelectedObjectItemChanged(RoomObjectItem value)
    {
        if (!ReferenceEquals(SelectedObject, value?.Object)) SelectedObject = value?.Object;
    }

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
    [RelayCommand] private void ToggleFullScreenObjects() => ShowFullScreenObjects = !ShowFullScreenObjects;

    private IReadOnlyList<BackgroundChoice> _backgroundChoices = Array.Empty<BackgroundChoice>();
    private bool _resetBackgroundChoice = true;
    private CancellationTokenSource _cts;

    public bool HasRoom => Room != null;
    /// <summary>Spinboxes are only active when there is something to switch between.</summary>
    public bool HasPaletteChoices => MaxPaletteIndex > 0;
    public bool HasBackgroundChoices => MaxBackgroundIndex > 0;

    public async Task LoadAsync(RoomModel room)
    {
        if (room == null || ReferenceEquals(room.Room, Room?.Room)) return;

        Room = room;
        OnPropertyChanged(nameof(HasRoom));
        Title = room.RoomNo > 0
            ? $"Room {room.RoomNo}{(string.IsNullOrEmpty(room.Name) ? "" : $" — {room.Name}")}  ({room.Width}×{room.Height})"
            : $"Room @0x{room.Room.Offset:X8}  ({room.Width}×{room.Height})";

        RoomSize = new Size(room.Width, room.Height);
        Walkboxes = room.Walkboxes;
        foreach (var item in Objects) item.PropertyChanged -= OnItemChanged;
        Objects.Clear();
        foreach (var o in room.Objects)
        {
            var item = new RoomObjectItem(o);
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

        await ReloadImagesAsync();
    }

    private bool _suppressPaletteReload;

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
            // Rooms can appear in more than one file (e.g. per-disc copies), so key by source file too.
            int cacheId = (int)Core.StableHash.Of(room.Room.SourceKey, room.RoomNo > 0 ? room.RoomNo : room.Room.Offset);
            var bg = await ScummBackgroundCache.GetCachedBackgroundAsync(
                cacheId, room.Rmhd, room.Room, cacheDisabled: false, PaletteIndex, token);
            if (token.IsCancellationRequested) return;

            var roomBackground = bg.ExecutedImage;

            var layers = new List<RoomObjectLayer>(room.Objects.Count);
            var fullWidth = new List<BackgroundChoice>();
            foreach (var obj in room.Objects)
            {
                SKImage image = null;
                RgbaImage rgba = null;
                if (obj.ImageCount > 0 && ObimDecoders.TryGetObimInfo(obj.Block, out var info))
                {
                    var result = await ObimCache.GetCachedObimAsync(obj.Block, 0, info.DefaultPaletteIndex, bakeImage: true, token);
                    if (token.IsCancellationRequested) return;
                    rgba = result.ExecutedImage;
                    image = rgba?.ToSkImage();
                }
                layers.Add(new RoomObjectLayer(obj, image));
                if (image != null && image.Width >= FullWidth) fullWidth.Add(new BackgroundChoice(obj.ToString(), image, rgba, obj));
            }

            // Background choices: the room's own image first, then every full-width object image.
            var choices = new List<BackgroundChoice>();
            if (roomBackground != null) choices.Add(new BackgroundChoice("room", roomBackground.ToSkImage(), roomBackground, null));
            choices.AddRange(fullWidth);

            ObjectLayers = layers;
            _backgroundChoices = choices;
            MaxBackgroundIndex = Math.Max(0, choices.Count - 1);
            // A new room starts on its first choice (its own image, or e.g. the inventory backdrop);
            // a palette change keeps the current choice.
            int index = _resetBackgroundChoice ? 0 : Math.Clamp(BackgroundIndex, 0, MaxBackgroundIndex);
            _resetBackgroundChoice = false;
            if (BackgroundIndex != index) BackgroundIndex = index; // applies via OnBackgroundIndexChanged
            else ApplyBackgroundChoice();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error($"Room decode failed: {ex.Message}");
        }
        finally
        {
            if (!token.IsCancellationRequested) IsBusy = false;
        }
    }

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
        if (choice?.Rgba == null || Room == null) return;
        string room = Room.RoomNo > 0 ? Room.RoomNo.ToString("D3") : Room.Room.Offset.ToString("X8");
        string suffix = choice.Object == null ? $"pal{PaletteIndex}" : string.Join("_", choice.Label.Split(Path.GetInvalidFileNameChars()));
        string path = Path.Combine(folder, $"room_{room}_{suffix}.png");
        choice.Rgba.SavePng(path);
        Log.Info($"Exported {path}");
    }
}
