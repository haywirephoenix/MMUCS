using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MMUCS.Core.Diagnostics;
using MMUCS.Core.Imaging;
using MMUCS.Core.Scumm;
using MMUCS.Desktop.Rendering;

namespace MMUCS.Desktop.ViewModels;

/// <summary>
/// The game window (testing ground): a 16:9 canvas with Play / Stop. While playing, a click captures
/// the mouse and the game draws its own cursor, the system-cursor-icon object (the one var290 names
/// when nothing is held) from the Icons room: state 1 normally, state 2 over a simulated hotspot.
/// </summary>
public sealed partial class GameViewModel : ObservableObject
{
    /// <summary>The object image the game uses as its cursor when nothing is held (object 105).</summary>
    public const string CursorObjectName = "system-cursor-icon";

    /// <summary>The canvas in game units: the game's 480 lines high, 16:9 wide.</summary>
    public const double GameHeight = 480, GameWidth = GameHeight * 16 / 9;

    /// <summary><see cref="GameWidth"/> × <see cref="GameHeight"/>, for the canvas.</summary>
    public static Size CanvasSize { get; } = new(GameWidth, GameHeight);

    private readonly ScummBlock _root;
    private readonly List<RgbaImage> _cursorStates = [];
    private readonly Dictionary<int, Bitmap> _cursorBitmaps = [];

    public GameViewModel(ScummBlock root)
    {
        _root = root;
        Status = root == null ? "Open the game first: the cursor comes from its Icons room." : "Loading the cursor…";
        _ = LoadCursorAsync();
    }

    /// <summary>A stand-in hotspot, so hovering can be tried before rooms and their objects are playable.</summary>
    public Rect Hotspot { get; } = new(GameWidth / 2 - 110, GameHeight / 2 - 70, 220, 140);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Hint))]
    [NotifyCanExecuteChangedFor(nameof(PlayCommand), nameof(StopCommand))]
    public partial bool IsPlaying { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Hint))]
    public partial bool IsMouseCaptured { get; set; }

    /// <summary>The cursor's state: 1 normally, 2 over a hotspot.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CursorFrame), nameof(CursorImage), nameof(CursorHotspot))]
    public partial int CursorState { get; set; } = 1;

    /// <summary>The game cursor's position in game units, on the canvas.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOverHotspot))]
    public partial Point CursorPosition { get; set; } = new(GameWidth / 2, GameHeight / 2);

    [ObservableProperty] public partial string Status { get; set; }

    public bool IsOverHotspot => Hotspot.Contains(CursorPosition);

    /// <summary>How many states (images) the cursor object has; 0 before it loads.</summary>
    public int CursorStateCount => _cursorStates.Count;

    /// <summary>The cursor object's decoded image for the current state, or null before it loads.</summary>
    public RgbaImage CursorFrame => _cursorStates.Count == 0 ? null : _cursorStates[Math.Clamp(CursorState, 1, _cursorStates.Count) - 1];

    /// <summary><see cref="CursorFrame"/> as a bitmap for the canvas (made on first use, then kept).</summary>
    public Bitmap CursorImage
    {
        get
        {
            if (CursorFrame is not { } frame) return null;
            int state = Math.Clamp(CursorState, 1, _cursorStates.Count);
            if (!_cursorBitmaps.TryGetValue(state, out var bitmap)) _cursorBitmaps[state] = bitmap = frame.ToBitmap();
            return bitmap;
        }
    }

    /// <summary>Where the cursor image is held, from its top left: its centre (a stand-in until the scripts' hotspot is read).</summary>
    public Point CursorHotspot => CursorFrame is { } i ? new Point(i.Width / 2.0, i.Height / 2.0) : default;

    /// <summary>The line drawn over the canvas: what to do next.</summary>
    public string Hint => !IsPlaying ? "Press Play to start."
        : IsMouseCaptured ? null
        : "Click the canvas to capture the mouse (Esc lets it go).";

    [RelayCommand(CanExecute = nameof(CanPlay))]
    private void Play() => IsPlaying = true;
    private bool CanPlay() => !IsPlaying;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        IsPlaying = false;
        IsMouseCaptured = false;
        CursorPosition = new Point(GameWidth / 2, GameHeight / 2);
    }
    private bool CanStop() => IsPlaying;

    /// <summary>Moves the game cursor (game units, clamped to the canvas) and picks its state from what's under it.</summary>
    public void MoveCursor(Point position)
    {
        CursorPosition = new Point(Math.Clamp(position.X, 0, GameWidth), Math.Clamp(position.Y, 0, GameHeight));
        CursorState = IsOverHotspot ? 2 : 1;
    }

    partial void OnIsMouseCapturedChanged(bool value)
    {
        if (!value) CursorState = 1;
    }

    private async Task LoadCursorAsync()
    {
        if (_root == null) return;
        try
        {
            var obim = _root.FindChildrenRecursive(ScummTag.OBIM).FirstOrDefault(b => NameOf(b) == CursorObjectName);
            if (obim == null || !RoomModel.TryCreate(obim, out var room))
            {
                Status = $"No {CursorObjectName} object image in this game.";
                return;
            }
            var obj = room.Objects.First(o => ReferenceEquals(o.Block, obim));
            int palette = Math.Clamp(RoomPalettes.For(room.Lflf).Object(room, obj), 0, Math.Max(0, room.PaletteCount - 1));
            for (int f = 0; f < Math.Max(1, obj.ImageCount); f++)
            {
                var result = await ObimCache.GetCachedObimAsync(obim, f, palette, bakeImage: true, CancellationToken.None);
                if (result.ExecutedImage is { } image) _cursorStates.Add(image);
            }
            OnPropertyChanged(nameof(CursorStateCount));
            OnPropertyChanged(nameof(CursorFrame));
            OnPropertyChanged(nameof(CursorImage));
            OnPropertyChanged(nameof(CursorHotspot));
            Status = $"{CursorObjectName} from room {room.RoomNo} ({room.Name}): {_cursorStates.Count} state{(_cursorStates.Count == 1 ? "" : "s")}, "
                     + $"{obj.Width}×{obj.Height}";
        }
        catch (Exception ex)
        {
            Log.Warn($"Game window cursor: {ex.Message}");
            Status = $"The cursor didn't load: {ex.Message}";
        }
    }

    /// <summary>An OBIM's name, from its IMHD (null when it has none).</summary>
    private static string NameOf(ScummBlock obim)
    {
        if (obim.FindChild(ScummTag.IMHD) is not { } imhd) return null;
        var s = imhd.DataSpan;
        int len = 0;
        while (len < 32 && len < s.Length && s[len] != 0) len++;
        return System.Text.Encoding.ASCII.GetString(s[..len]);
    }
}
