using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MMUCS.Core;
using MMUCS.Core.Imaging;
using MMUCS.Core.Scumm;
using SkiaSharp;

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
