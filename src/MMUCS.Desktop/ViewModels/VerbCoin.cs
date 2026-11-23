using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using MMUCS.Core.Imaging;
using MMUCS.Core.Scumm;
using MMUCS.Desktop.Rendering;

namespace MMUCS.Desktop.ViewModels;

/// <summary>Where the pointer is on the verb coin.</summary>
public enum CoinZone { None, Left, Middle, Right }

/// <summary>
/// The verb coin for the game window: costume 1 of the logo room (room 1), shown while the mouse is held
/// on a hotspot. It stays where the press was, looping anim 6, or anim 7 / 8 / 9 while the pointer is
/// over its left (hand), middle (skull) or right (parrot) zone, a band
/// across the coin's middle; moving off the band puts it back on anim 6. Positions are in game units.
/// </summary>
public sealed partial class VerbCoin : ObservableObject
{
    public const int CostumeNumber = 1, RoomNumber = 1;
    public const int IdleAnim = 6, LeftAnim = 7, MiddleAnim = 8, RightAnim = 9;

    /// <summary>
    /// Where the coin's centre goes from the point pressed, in game pixels: up and a little left, so the
    /// cursor opens it below its three zones and has to move up onto one.
    /// </summary>
    public static readonly Vector SpawnOffset = new(-10, -35);

    /// <summary>The zones' band: this share of the coin's height, across its middle. Above or below it is off the zones.</summary>
    public const double ZoneHeight = 1.0 / 3;

    /// <summary>How fast its animations step (the Costume viewer's default rate).</summary>
    public const double FramesPerSecond = 10;

    private readonly Dictionary<(int Anim, int Step), (RgbaImage Image, int OriginX, int OriginY)> _frames = [];
    private readonly Dictionary<(int Anim, int Step), Bitmap> _bitmaps = [];
    private readonly Dictionary<int, int> _cycles = [];
    private Rect _face; // the coin itself (anim 6's first frame), relative to the actor origin
    private Point _origin; // the actor origin, in game units

    /// <summary>Whether the coin's frames are decoded.</summary>
    public bool IsLoaded { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Frame), nameof(Image), nameof(Bounds))]
    public partial bool IsShown { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Frame), nameof(Image), nameof(Bounds))]
    public partial int Anim { get; set; } = IdleAnim;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Frame), nameof(Image), nameof(Bounds))]
    public partial int Step { get; set; }

    [ObservableProperty] public partial CoinZone Zone { get; set; }

    /// <summary>The frame showing now, or null when the coin is hidden.</summary>
    public RgbaImage Frame => IsShown && _frames.TryGetValue((Anim, Step), out var f) ? f.Image : null;

    /// <summary><see cref="Frame"/> as a bitmap for the canvas (made on first use, then kept).</summary>
    public Bitmap Image
    {
        get
        {
            if (Frame is not { } frame) return null;
            if (!_bitmaps.TryGetValue((Anim, Step), out var bitmap)) _bitmaps[(Anim, Step)] = bitmap = frame.ToBitmap();
            return bitmap;
        }
    }

    /// <summary>Where <see cref="Frame"/> is drawn, in game units (its pixels are game pixels).</summary>
    public Rect Bounds => IsShown && _frames.TryGetValue((Anim, Step), out var f) && f.Image != null
        ? new Rect(_origin.X - f.OriginX, _origin.Y - f.OriginY, f.Image.Width, f.Image.Height)
        : default;

    /// <summary>The coin's actor origin in game units, where its frames are placed from.</summary>
    public Point Origin => _origin;

    /// <summary>The coin's face in game units, where the pointer picks a zone (empty while hidden).</summary>
    public Rect Face => IsShown ? _face.Translate(new Vector(_origin.X, _origin.Y)) : default;

    /// <summary>Decodes anims 6–9 of the costume (every frame of each loop); false when the game hasn't got it.</summary>
    public async Task<bool> LoadAsync(ScummBlock root)
    {
        var akos = root?.FindChildrenRecursive(ScummTag.AKOS).FirstOrDefault(a =>
            RoomActors.CostumeNumber(a) == CostumeNumber && RoomModel.TryCreate(a, out var room) && room.RoomNo == RoomNumber);
        if (akos == null) return false;
        await Task.Run(() =>
        {
            var data = AkosParser.Parse(akos);
            foreach (int anim in new[] { IdleAnim, LeftAnim, MiddleAnim, RightAnim })
            {
                int chore = Enumerable.Range(0, data.DirectionCount).Select(dir => AkosFrames.ChoreIndex(data, anim, dir))
                    .FirstOrDefault(c => AkosFrames.HasChore(data, c), -1);
                if (chore < 0) continue;
                int cycle = AkosFrames.CycleLength(data, chore);
                _cycles[anim] = cycle;
                for (int step = 0; step < cycle; step++)
                {
                    var image = AkosFrames.Render(data, AkosFrames.Compose(data, chore, step), out int ox, out int oy);
                    _frames[(anim, step)] = (image, ox, oy);
                }
            }
        });
        if (!_frames.TryGetValue((IdleAnim, 0), out var first) || first.Image == null) return false;
        _face = new Rect(-first.OriginX, -first.OriginY, first.Image.Width, first.Image.Height);
        IsLoaded = true;
        return true;
    }

    /// <summary>Shows the coin centred <see cref="SpawnOffset"/> from <paramref name="at"/>, kept inside a <paramref name="canvas"/>-sized picture.</summary>
    public void Show(Point at, Size canvas)
    {
        if (!IsLoaded) return;
        double x = Math.Clamp(at.X + SpawnOffset.X, _face.Width / 2, Math.Max(_face.Width / 2, canvas.Width - _face.Width / 2));
        double y = Math.Clamp(at.Y + SpawnOffset.Y, _face.Height / 2, Math.Max(_face.Height / 2, canvas.Height - _face.Height / 2));
        _origin = new Point(x - _face.Center.X, y - _face.Center.Y);
        Zone = CoinZone.None;
        Anim = IdleAnim;
        Step = 0;
        IsShown = true;
        OnPropertyChanged(nameof(Face));
    }

    /// <summary>Shows the coin with its actor origin at <paramref name="origin"/> (game units), as the scripts place it (global 43).</summary>
    public void ShowAtOrigin(Point origin)
    {
        if (!IsLoaded) return;
        _origin = origin;
        Zone = CoinZone.None;
        Anim = IdleAnim;
        Step = 0;
        IsShown = true;
        OnPropertyChanged(nameof(Face));
    }

    public void Hide()
    {
        IsShown = false;
        Zone = CoinZone.None;
        OnPropertyChanged(nameof(Face));
    }

    /// <summary>A zone's rectangle in game units: a third of the coin's width, <see cref="ZoneHeight"/> of its height
    /// across the middle (empty while hidden or for <see cref="CoinZone.None"/>).</summary>
    public Rect ZoneRect(CoinZone zone)
    {
        var face = Face;
        if (zone == CoinZone.None || face.Width <= 0) return default;
        double w = face.Width / 3, h = face.Height * ZoneHeight;
        return new Rect(face.X + w * ((int)zone - 1), face.Center.Y - h / 2, w, h);
    }

    /// <summary>Picks the zone under <paramref name="pointer"/> and plays its animation; off the zones, the coin goes back to anim 6.</summary>
    public void PointAt(Point pointer)
    {
        if (!IsShown) return;
        var zone = CoinZone.None;
        foreach (var z in new[] { CoinZone.Left, CoinZone.Middle, CoinZone.Right })
            if (ZoneRect(z).Contains(pointer)) zone = z;
        if (zone == Zone) return;
        Zone = zone;
        int anim = zone switch { CoinZone.Left => LeftAnim, CoinZone.Middle => MiddleAnim, CoinZone.Right => RightAnim, _ => IdleAnim };
        if (!_cycles.ContainsKey(anim)) anim = IdleAnim;
        // A new chore starts its limbs from the top.
        Step = 0;
        Anim = anim;
    }

    /// <summary>Steps the playing animation (it loops).</summary>
    public void Tick()
    {
        if (IsShown && _cycles.TryGetValue(Anim, out int cycle) && cycle > 0) Step = (Step + 1) % cycle;
    }
}
