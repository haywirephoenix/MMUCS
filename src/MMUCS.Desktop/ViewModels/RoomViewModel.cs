using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using MMUCS.Core.Diagnostics;
using MMUCS.Core.Imaging;
using MMUCS.Core.Scumm;
using MMUCS.Desktop.Controls;
using MMUCS.Desktop.Rendering;
using SkiaSharp;

namespace MMUCS.Desktop.ViewModels;

public sealed partial class RoomViewModel : ObservableObject
{
    [ObservableProperty] private RoomModel _room;
    [ObservableProperty] private string _title = "No room selected";
    [ObservableProperty] private SKImage _backgroundImage;
    [ObservableProperty] private IReadOnlyList<RoomObjectLayer> _objectLayers;
    [ObservableProperty] private IReadOnlyList<Walkbox> _walkboxes;
    [ObservableProperty] private Size _roomSize;
    [ObservableProperty] private RoomObject _selectedObject;
    [ObservableProperty] private int _paletteIndex;
    [ObservableProperty] private int _maxPaletteIndex;
    [ObservableProperty] private bool _isBusy;

    [ObservableProperty] private bool _showBackground = true;
    [ObservableProperty] private bool _showObjects = true;
    [ObservableProperty] private bool _showHotspots;
    [ObservableProperty] private bool _showWalkboxes;
    [ObservableProperty] private bool _showLabels = true;
    [ObservableProperty] private bool _showBounds;

    public ObservableCollection<RoomObject> Objects { get; } = new();

    private RgbaImage _backgroundRgba;
    private CancellationTokenSource _cts;

    public bool HasRoom => Room != null;

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
        Objects.Clear();
        foreach (var o in room.Objects) Objects.Add(o);
        SelectedObject = null;

        MaxPaletteIndex = Math.Max(0, room.PaletteCount - 1);
        _suppressPaletteReload = true;
        PaletteIndex = 0;
        _suppressPaletteReload = false;

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

            _backgroundRgba = bg.ExecutedImage;
            BackgroundImage = bg.ExecutedImage?.ToSkImage();

            var layers = new List<RoomObjectLayer>(room.Objects.Count);
            foreach (var obj in room.Objects)
            {
                SKImage image = null;
                if (obj.ImageCount > 0 && ObimDecoders.TryGetObimInfo(obj.Block, out var info))
                {
                    var result = await ObimCache.GetCachedObimAsync(obj.Block, 0, info.DefaultPaletteIndex, bakeImage: true, token);
                    if (token.IsCancellationRequested) return;
                    image = result.ExecutedImage?.ToSkImage();
                }
                layers.Add(new RoomObjectLayer(obj, image));
            }
            ObjectLayers = layers;
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

    public void ExportBackground(string folder)
    {
        if (_backgroundRgba == null || Room == null) return;
        string path = Path.Combine(folder, $"room_{(Room.RoomNo > 0 ? Room.RoomNo.ToString("D3") : Room.Room.Offset.ToString("X8"))}_pal{PaletteIndex}.png");
        _backgroundRgba.SavePng(path);
        Log.Info($"Exported {path}");
    }
}
