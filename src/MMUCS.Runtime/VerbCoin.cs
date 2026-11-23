using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MMUCS.Core.Imaging;
using MMUCS.Core.Scumm;

namespace MMUCS.Runtime;

/// <summary>Where the pointer is on the verb coin.</summary>
public enum CoinZone { None, Left, Middle, Right }

/// <summary>
/// The verb coin: costume 1 of the logo room (room 1), shown while the mouse is held
/// on a hotspot. It stays where the press was, looping anim 6, or anim 7 / 8 / 9 while the pointer is
/// over its left (hand), middle (skull) or right (parrot) zone, a band
/// across the coin's middle; moving off the band puts it back on anim 6. Positions are in game units.
/// </summary>
public sealed class VerbCoin
{
    public const int CostumeNumber = 1, RoomNumber = 1;
    public const int IdleAnim = 6, LeftAnim = 7, MiddleAnim = 8, RightAnim = 9;

    /// <summary>
    /// Where the coin's centre goes from the point pressed, in game pixels: up and a little left, so the
    /// cursor opens it below its three zones and has to move up onto one.
    /// </summary>
    public static readonly GamePoint SpawnOffset = new(-10, -35);

    /// <summary>The zones' band: this share of the coin's height, across its middle. Above or below it is off the zones.</summary>
    public const double ZoneHeight = 1.0 / 3;

    /// <summary>Jiffies (1/60 s) per animation frame: 10 frames a second, the Costume viewer's default rate (not yet the engine's).</summary>
    public const int JiffiesPerFrame = 6;

    private readonly Dictionary<(int Anim, int Step), (RgbaImage Image, int OriginX, int OriginY)> _frames = [];
    private readonly Dictionary<int, int> _cycles = [];
    private GameRect _face; // the coin itself (anim 6's first frame), relative to the actor origin
    private GamePoint _origin; // the actor origin, in game units
    private int _jiffies; // since the frame showing came up

    /// <summary>Whether the coin's frames are decoded.</summary>
    public bool IsLoaded { get; private set; }

    public bool IsShown { get; private set; }
    public int Anim { get; private set; } = IdleAnim;
    public int Step { get; private set; }
    public CoinZone Zone { get; private set; }

    /// <summary>The frame showing now, or null when the coin is hidden.</summary>
    public RgbaImage Frame => IsShown && _frames.TryGetValue((Anim, Step), out var f) ? f.Image : null;

    /// <summary>Where <see cref="Frame"/> is drawn, in game units (its pixels are game pixels).</summary>
    public GameRect Bounds => IsShown && _frames.TryGetValue((Anim, Step), out var f) && f.Image != null
        ? new GameRect(_origin.X - f.OriginX, _origin.Y - f.OriginY, f.Image.Width, f.Image.Height)
        : default;

    /// <summary>The coin's actor origin in game units, where its frames are placed from.</summary>
    public GamePoint Origin => _origin;

    /// <summary>The coin's face in game units, where the pointer picks a zone (empty while hidden).</summary>
    public GameRect Face => IsShown ? _face.Translate(_origin) : default;

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
        _face = new GameRect(-first.OriginX, -first.OriginY, first.Image.Width, first.Image.Height);
        IsLoaded = true;
        return true;
    }

    /// <summary>Shows the coin centred <see cref="SpawnOffset"/> from <paramref name="at"/>, kept inside a <paramref name="canvas"/>-sized picture.</summary>
    public void Show(GamePoint at, GameSize canvas)
    {
        if (!IsLoaded) return;
        double x = Math.Clamp(at.X + SpawnOffset.X, _face.Width / 2, Math.Max(_face.Width / 2, canvas.Width - _face.Width / 2));
        double y = Math.Clamp(at.Y + SpawnOffset.Y, _face.Height / 2, Math.Max(_face.Height / 2, canvas.Height - _face.Height / 2));
        ShowAtOrigin(new GamePoint(x - _face.Center.X, y - _face.Center.Y));
    }

    /// <summary>Shows the coin with its actor origin at <paramref name="origin"/> (game units), as the scripts place it (global 43).</summary>
    public void ShowAtOrigin(GamePoint origin)
    {
        if (!IsLoaded) return;
        _origin = origin;
        Zone = CoinZone.None;
        Play(IdleAnim);
        IsShown = true;
    }

    public void Hide()
    {
        IsShown = false;
        Zone = CoinZone.None;
    }

    /// <summary>Starts <paramref name="anim"/> from its first frame (a new chore starts its limbs from the top).</summary>
    private void Play(int anim)
    {
        Anim = anim;
        Step = 0;
        _jiffies = 0;
    }

    /// <summary>A zone's rectangle in game units: a third of the coin's width, <see cref="ZoneHeight"/> of its height
    /// across the middle (empty while hidden or for <see cref="CoinZone.None"/>).</summary>
    public GameRect ZoneRect(CoinZone zone)
    {
        var face = Face;
        if (zone == CoinZone.None || face.Width <= 0) return default;
        double w = face.Width / 3, h = face.Height * ZoneHeight;
        return new GameRect(face.X + w * ((int)zone - 1), face.Center.Y - h / 2, w, h);
    }

    /// <summary>Picks the zone under <paramref name="pointer"/> and plays its animation; off the zones, the coin goes back to anim 6.</summary>
    public void PointAt(GamePoint pointer)
    {
        if (!IsShown) return;
        var zone = CoinZone.None;
        foreach (var z in new[] { CoinZone.Left, CoinZone.Middle, CoinZone.Right })
            if (ZoneRect(z).Contains(pointer)) zone = z;
        if (zone == Zone) return;
        Zone = zone;
        int anim = zone switch { CoinZone.Left => LeftAnim, CoinZone.Middle => MiddleAnim, CoinZone.Right => RightAnim, _ => IdleAnim };
        Play(_cycles.ContainsKey(anim) ? anim : IdleAnim);
    }

    /// <summary>One jiffy (1/60 s) passes: the playing animation steps every <see cref="JiffiesPerFrame"/> (it loops).</summary>
    public void Tick()
    {
        if (!IsShown || !_cycles.TryGetValue(Anim, out int cycle) || cycle <= 0 || ++_jiffies < JiffiesPerFrame) return;
        _jiffies = 0;
        Step = (Step + 1) % cycle;
    }
}
