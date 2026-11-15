using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using MMUCS.Core.Diagnostics;
using MMUCS.Core.Scumm;
using MMUCS.Desktop.Rendering;
using SkiaSharp;

namespace MMUCS.Desktop.ViewModels;

/// <summary>One of an object's states in the Object viewer: 0 hides it, N shows its image N.</summary>
public sealed class ObjectStateItem
{
    public int State { get; init; }
    public SKImage Image { get; init; }
    public Avalonia.Media.Imaging.Bitmap Thumbnail { get; init; }
    public string Label { get; init; }
    public string Detail { get; init; }
}

/// <summary>
/// The Object viewer: an object image (OBIM) and its states. Picking a state previews its image here
/// and draws the object in that state in the Room viewer.
/// </summary>
public sealed partial class ObjectImageViewModel : ObservableObject
{
    private readonly RoomViewModel _room;
    private CancellationTokenSource _cts;
    private bool _loading;

    public ObjectImageViewModel(RoomViewModel room) => _room = room;

    [ObservableProperty] private RoomObject _object;
    [ObservableProperty] private string _title = "No object selected";
    [ObservableProperty] private string _info;
    [ObservableProperty] private SKImage _previewImage;
    [ObservableProperty] private ObjectStateItem _selectedState;
    public ObservableCollection<ObjectStateItem> States { get; } = new();

    /// <summary>Shows <paramref name="obj"/>'s states, decoded in the room's current palette.</summary>
    public async Task LoadAsync(RoomObject obj)
    {
        if (ReferenceEquals(obj, Object) && States.Count > 0)
        {
            SelectCurrent();
            return;
        }
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Object = obj;
        _loading = true;
        try
        {
            States.Clear();
            PreviewImage = null;
            SelectedState = null;
            if (obj == null || obj.ImageCount <= 0 || !ObimDecoders.TryGetObimInfo(obj.Block, out var info))
            {
                Title = obj == null ? "No object selected" : $"{obj}: no image";
                Info = null;
                return;
            }
            int frames = Math.Max(1, info.TotalFrames);
            int entry = _room.InitialStateOf(obj);
            Title = obj.ToString();
            Info = $"{obj.X},{obj.Y}   {obj.Width}×{obj.Height}   {frames} image{(frames == 1 ? "" : "s")}   state {entry} on entry";
            States.Add(new ObjectStateItem { State = 0, Label = "State 0", Detail = "hidden" + (entry == 0 ? " · on entry" : "") });
            for (int f = 0; f < frames; f++)
            {
                var result = await ObimCache.GetCachedObimAsync(obj.Block, f, _room.PaletteOf(obj), bakeImage: true, token);
                if (token.IsCancellationRequested) return;
                States.Add(new ObjectStateItem
                {
                    State = f + 1, Image = result.ExecutedImage?.ToSkImage(), Thumbnail = result.ExecutedImage?.ToBitmap(), Label = $"State {f + 1}",
                    Detail = $"image {f + 1}" + (entry == f + 1 ? " · on entry" : ""),
                });
            }
        }
        catch (Exception ex) { Log.Warn($"Object images of {obj}: {ex.Message}"); }
        finally { _loading = false; }
        SelectCurrent();
    }


    /// <summary>Selects the state the room draws the object in now, without changing the room.</summary>
    private void SelectCurrent()
    {
        _loading = true;
        int state = _room.StateOf(Object);
        SelectedState = States.FirstOrDefault(s => s.State == state) ?? States.FirstOrDefault();
        PreviewImage = SelectedState?.Image;
        _loading = false;
    }

    partial void OnSelectedStateChanged(ObjectStateItem value)
    {
        if (value == null) return;
        // State 0 draws nothing in the room; the preview keeps the first image so there's something to see.
        PreviewImage = value.Image ?? States.FirstOrDefault(s => s.Image != null)?.Image;
        if (!_loading && Object != null) _ = _room.ShowObjectStateAsync(Object, value.State);
    }
}
