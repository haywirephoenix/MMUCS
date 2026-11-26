using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MMUCS.Core;
using MMUCS.Core.Imaging;
using MMUCS.Core.Scumm;
using SkiaSharp;
using MMUCS.Runtime.Scripts;

namespace MMUCS.Player.Rooms;

/// <summary>
/// Builds <see cref="RoomScene"/>s from a room and its objects' states, as the engine draws it: an object is
/// drawn while its state isn't 0 and shows image <i>state</i> (ScummVM <c>drawRoomObjects</c>), in the palette
/// the game shows it in, in the room's draw order; the z-planes are the room's with the drawn objects' over
/// them. The images come from Core's caches and are shared through <see cref="SkiaImages"/>, so the garbage
/// collector frees them; the Room viewer, which frees its own, uses the same rules through the static helpers.
/// </summary>
public static class RoomSceneBuilder
{
    /// <summary>
    /// The room with each object in <paramref name="stateOf"/>'s state, and <paramref name="actors"/>.
    /// <paramref name="include"/> can leave objects out (null draws every object whose state isn't 0).
    /// </summary>
    public static async Task<RoomScene> BuildAsync(RoomModel room, Func<RoomObject, int> stateOf, IEnumerable<SceneActor> actors = null,
        Func<RoomObject, bool> include = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(room);
        SKImage background = null;
        if (room.Width > 0)
        {
            var bg = await ScummBackgroundCache.GetCachedBackgroundAsync(BackgroundCacheId(room), room.Rmhd, room.Room, cacheDisabled: false, 0, token);
            background = SkiaImages.Of(bg.ExecutedImage);
        }

        var shown = Shown(room, stateOf, include);
        var objects = new List<SceneObject>(shown.Count);
        foreach (var (obj, image) in shown)
        {
            token.ThrowIfCancellationRequested();
            var rgba = await ObjectImageAsync(room, obj, image, token);
            if (rgba != null) objects.Add(new SceneObject(obj, SkiaImages.Of(rgba), obj.X, obj.Y));
        }

        var planes = RoomScene.Planes(room, shown);
        return new RoomScene
        {
            Width = room.Width, Height = room.Height,
            Background = background,
            Objects = objects,
            Actors = actors?.ToList() ?? [],
            ZMasks = [.. planes.Select(p => RoomScene.MaskImage(p, room.Width, room.Height))],
        };
    }

    /// <summary>
    /// The room with each object in <paramref name="stateOf"/>'s state, in palette indices, to be coloured with
    /// the screen's palette (<see cref="IndexedRoom.Colour"/>), as the game shows it. An image <paramref name="mods"/>
    /// has for an object is drawn in place of the game's, where the mod puts it.
    /// </summary>
    public static async Task<IndexedRoom> BuildIndexedAsync(RoomModel room, Func<RoomObject, int> stateOf,
        Func<RoomObject, bool> include = null, CancellationToken token = default, ObjectImageMods mods = null)
    {
        ArgumentNullException.ThrowIfNull(room);
        IndexedSurface background = null;
        if (room.Width > 0)
            background = (await ScummBackgroundCache.GetCachedBackgroundAsync(BackgroundCacheId(room), room.Rmhd, room.Room, cacheDisabled: false, 0, token)).Surface;

        var shown = Shown(room, stateOf, include);
        var objects = new List<IndexedObject>(shown.Count);
        foreach (var (obj, image) in shown)
        {
            token.ThrowIfCancellationRequested();
            if (obj.ImageCount <= 0 || !ObimDecoders.TryGetObimInfo(obj.Block, out var info)) continue;
            int frame = Math.Clamp(image, 0, Math.Max(0, (info.TotalFrames > 0 ? info.TotalFrames : 1) - 1));
            var result = await ObimCache.GetCachedObimAsync(obj.Block, frame, PaletteOf(room, obj), bakeImage: false, token);
            if (result.Surface == null) continue;
            int transparent = ObimCache.GetTransparentIndex(room.Room, obj.Block, frame);
            if (mods?.Find(room, obj, frame) is { } modded &&
                mods.Indexed(modded, ObimCache.ExtractPaletteContext(room.Room, PaletteOf(room, obj)).rawBytes, transparent,
                    background, modded.X ?? obj.X, modded.Y ?? obj.Y) is { } surface)
                objects.Add(new IndexedObject(obj, surface, transparent, modded.X ?? obj.X, modded.Y ?? obj.Y));
            else
                objects.Add(new IndexedObject(obj, result.Surface, transparent, obj.X, obj.Y));
        }

        var planes = RoomScene.Planes(room, shown);
        return new IndexedRoom
        {
            Width = room.Width, Height = room.Height,
            Background = background,
            Objects = objects,
            ZMasks = [.. planes.Select(p => RoomScene.MaskImage(p, room.Width, room.Height))],
        };
    }

    /// <summary>The objects drawn, in draw order, each with the image (from 0) its state shows: state 0 isn't drawn.</summary>
    public static List<(RoomObject Object, int Image)> Shown(RoomModel room, Func<RoomObject, int> stateOf, Func<RoomObject, bool> include = null)
    {
        var shown = new List<(RoomObject, int)>();
        foreach (var obj in room.InDrawOrder(room.Objects))
        {
            int state = stateOf(obj);
            if (state == 0 || obj.ImageCount <= 0 || include?.Invoke(obj) == false) continue;
            shown.Add((obj, RoomObjectStates.FrameFor(state, obj.ImageCount)));
        }
        return shown;
    }

    /// <summary>An object's image <paramref name="image"/> (from 0) in the palette the game shows it in, or null.</summary>
    public static async Task<RgbaImage> ObjectImageAsync(RoomModel room, RoomObject obj, int image, CancellationToken token = default)
    {
        if (obj.ImageCount <= 0 || !ObimDecoders.TryGetObimInfo(obj.Block, out var info)) return null;
        int frame = Math.Clamp(image, 0, Math.Max(0, (info.TotalFrames > 0 ? info.TotalFrames : 1) - 1));
        var result = await ObimCache.GetCachedObimAsync(obj.Block, frame, PaletteOf(room, obj), bakeImage: true, token);
        return result.ExecutedImage;
    }

    /// <summary>
    /// The palette an object is shown in: SCUMM v8 has no per-object palette, the game switches the room's
    /// palette from scripts before it shows one (<see cref="RoomPalettes"/>).
    /// </summary>
    public static int PaletteOf(RoomModel room, RoomObject obj) =>
        room == null ? 0 : Math.Clamp(RoomPalettes.For(room.Lflf).Object(room, obj), 0, Math.Max(0, room.PaletteCount - 1));

    /// <summary>The background cache's key: rooms can be in more than one file (per-disc copies), so the file counts too.</summary>
    public static int BackgroundCacheId(RoomModel room) =>
        (int)StableHash.Of(room.Room.SourceKey, room.RoomNo > 0 ? room.RoomNo : room.Room.Offset);

    /// <summary>
    /// An actor's frame as the engine draws it (each cel scaled on its own with SCUMM's scale table, so it's
    /// drawn at full size), placed by its position and origin, or null when it draws nothing.
    /// </summary>
    public static (RgbaImage Image, int X, int Y)? ActorFrame(RoomActor actor, CancellationToken token = default)
    {
        if (!actor.CanDraw) return null;
        lock (actor.Data)
        {
            AkosParser.UpdatePaletteOnly(actor.Data, actor.Akos, actor.Data.DefaultPaletteIndex);
            var rgba = AkosFrames.RenderScaled(actor.Data, actor.Frame, actor.Scale, out int ox, out int oy, actor.Mirrored, token);
            return rgba == null ? null : (rgba, actor.X - ox, actor.DrawY - oy);
        }
    }

    /// <summary>
    /// A costume frame as the engine draws it (each cel scaled on its own with SCUMM's scale table), with
    /// where the actor's origin is in it, or null when it draws nothing. With <paramref name="screen"/> (the
    /// colours on the screen) it's in those, through the actor's <paramref name="remap"/>, as the game draws
    /// it; without, in the costume's room's palette. With <paramref name="shadow"/> its shadow colours draw
    /// as that says (in mode 3, into its layer).
    /// </summary>
    public static (RgbaImage Image, int OriginX, int OriginY)? CostumeFrame(AkosData data, ScummBlock akos, AkosFrame frame, int scale, bool mirrored,
        byte[] screen = null, int[] remap = null, AkosShadow shadow = null, CancellationToken token = default)
    {
        if (frame is not { IsEmpty: false }) return null;
        lock (data)
        {
            if (screen == null) AkosParser.UpdatePaletteOnly(data, akos, data.DefaultPaletteIndex);
            var rgba = AkosFrames.RenderScaled(data, frame, scale, out int ox, out int oy, mirrored, token, screen, remap, shadow);
            return rgba == null ? null : (rgba, ox, oy);
        }
    }

    /// <summary>
    /// The actors the interpreter drew (<see cref="ActorTable.Pictures"/>, back to front) as scene actors, in
    /// a room with <paramref name="planes"/> z-planes (the walk box's plane stays within them, as the engine
    /// keeps it). <paramref name="cache"/> keeps rendered frames by <see cref="ActorPicture.Key"/>, so it's for one
    /// <paramref name="screen"/> palette (the colours on the screen; null: each costume's room's palette).
    /// With <paramref name="shadowFactors"/> (<see cref="ScreenPalette.ShadowFactors"/>) an actor's shadow
    /// colours draw by its shadow mode: in mode 3, as a layer the scene multiplies onto what's under it.
    /// </summary>
    public static List<SceneActor> SceneActors(IReadOnlyList<ActorPicture> pictures, int planes,
        Dictionary<string, ActorImage> cache = null, double slide = 1, byte[] screen = null, byte[] shadowFactors = null)
    {
        var list = new List<SceneActor>(pictures.Count);
        for (int i = 0; i < pictures.Count; i++)
        {
            var p = pictures[i];
            var shadow = shadowFactors != null ? new AkosShadow(p.ShadowMode, shadowFactors) : null;
            string key = p.ShadowMode == 3 && shadowFactors != null ? p.Key + ":" + Convert.ToHexString(shadowFactors) : p.Key;
            ActorImage image = null;
            if (cache == null || !cache.TryGetValue(key, out image))
            {
                try
                {
                    image = CostumeFrame(p.Costume.Data, p.Costume.Block, p.Frame, p.Scale, p.Mirrored, screen, screen == null ? null : p.Remap, shadow, default) is var (rgba, ox, oy)
                        ? new ActorImage(SkiaImages.Of(rgba), shadow?.Image is { } layer ? SkiaImages.Of(layer) : null, ox, oy) : null;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    MMUCS.Core.Diagnostics.Log.Warn($"Game actor {p.Actor} costume {p.Costume.Number}: {ex.Message}");
                }
                cache?[key] = image;
            }
            if (image == null) continue;
            int plane = p.FromBox ? Math.Min(p.ZPlane, planes) : p.ZPlane;
            var (x, y) = Slid(p, slide);
            list.Add(new SceneActor(image.Image, x - image.OriginX, y - image.OriginY, 1, plane, i, p.Actor, image.Shadow));
        }
        return list;
    }

    /// <summary>A rendered actor frame: its picture, its shadow layer (or null) and where the actor's origin is in them.</summary>
    public sealed record ActorImage(SKImage Image, SKImage Shadow, int OriginX, int OriginY);

    /// <summary>The most an actor moves in a frame and still slides there; further is a jump, drawn where it lands.</summary>
    public const int MaxSlide = 32;

    /// <summary>
    /// Where to draw an actor <paramref name="slide"/> of the way (0–1) from the last frame's place to this
    /// one's; a jump of more than <see cref="MaxSlide"/> pixels isn't slid.
    /// </summary>
    public static (float X, float Y) Slid(ActorPicture p, double slide)
    {
        int dx = p.X - p.FromX, dy = p.Y - p.FromY;
        if (slide >= 1 || Math.Abs(dx) > MaxSlide || Math.Abs(dy) > MaxSlide) return (p.X, p.Y);
        double t = Math.Max(0, slide);
        return ((float)(p.FromX + dx * t), (float)(p.FromY + dy * t));
    }

    /// <summary>The room's actors as its scripts place them (<see cref="RoomActors.Analyze"/>), drawn as the engine draws them.</summary>
    public static List<SceneActor> Actors(IEnumerable<RoomActor> actors, CancellationToken token = default)
    {
        var list = new List<SceneActor>();
        foreach (var actor in actors)
            if (ActorFrame(actor, token) is { } f)
                list.Add(new SceneActor(SkiaImages.Of(f.Image), f.X, f.Y, 1, actor.ZPlane, actor.SortY, actor.Actor));
        return list;
    }
}
