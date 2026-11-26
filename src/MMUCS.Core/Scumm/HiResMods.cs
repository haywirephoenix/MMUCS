using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace MMUCS.Core.Scumm;

/// <summary>
/// Hi-res images from the mods (<see cref="GameMods"/>) whose mod.json gives a "scale" over 1: the mod's
/// pixels are that many to a game pixel, so a 4× mod's 640×480 room is a 2560×1920 PNG. They're laid out as an
/// export writes the game's images:
/// <code>
/// _mods/UpscaledBackgrounds/mod.json                                    { "enabled": true, "scale": 4 }
/// _mods/UpscaledBackgrounds/rooms/014_fortbase/background.png           the room's background (5984×1920)
/// _mods/UpscaledBackgrounds/rooms/069_stancrpt/objects/048_stans-office_f0.png   an object's image
/// </code>
/// A background covers the room from its top left; an object image goes at the object's place unless a
/// "&lt;same name&gt;.meta.json" beside it gives its top left in the room (<c>{ "x": 232, "y": 392 }</c>, game pixels).
/// They're drawn in full colour, not taken into the palette, and tinted as the scripts change the colours of the
/// game's image under them. The first enabled mod by name that has an image wins.
/// </summary>
public sealed class HiResMods
{
    /// <summary>A mod's hi-res image: its mod, the PNG, its scale, and where it goes in game pixels (null: the object's place).</summary>
    public sealed record Image(string Mod, string File, int Scale, int? X = null, int? Y = null);

    private readonly List<GameMods.Mod> _mods = [];

    /// <summary>The enabled hi-res mods that have a rooms/ folder (folder names).</summary>
    public List<string> Mods { get; } = [];

    /// <summary>What couldn't be read: a broken mod.json or .meta.json.</summary>
    public List<string> Problems { get; } = [];

    /// <summary>The hi-res mods beside <paramref name="gameFile"/>; none without a mods folder.</summary>
    public static HiResMods Load(string gameFile)
    {
        var mods = new HiResMods();
        foreach (var mod in GameMods.Load(gameFile, mods.Problems))
        {
            if (!mod.Enabled || mod.Scale <= 1 || !Directory.Exists(Path.Combine(mod.Folder, "rooms"))) continue;
            mods._mods.Add(mod);
            mods.Mods.Add(mod.Name);
        }
        return mods;
    }

    /// <summary>The mods' background for <paramref name="room"/>, or null.</summary>
    public Image Background(RoomModel room)
    {
        if (_mods.Count == 0 || room == null) return null;
        string relative = Path.Combine("rooms", ScummExporters.RoomFolderName(room), "background.png");
        foreach (var mod in _mods)
            if (Path.Combine(mod.Folder, relative) is var file && File.Exists(file)) return new Image(mod.Name, file, mod.Scale);
        return null;
    }

    /// <summary>The mods' image for image <paramref name="frame"/> (from 0) of <paramref name="obj"/> in <paramref name="room"/>, or null.</summary>
    public Image Object(RoomModel room, RoomObject obj, int frame)
    {
        if (_mods.Count == 0 || room == null || obj == null) return null;
        string relative = Path.Combine("rooms", ScummExporters.RoomFolderName(room), "objects", ScummExporters.ObjectFrameFile(obj, frame));
        foreach (var mod in _mods)
        {
            string file = Path.Combine(mod.Folder, relative);
            if (!File.Exists(file)) continue;
            var (x, y) = Place(mod, file);
            return new Image(mod.Name, file, mod.Scale, x, y);
        }
        return null;
    }

    private (int? X, int? Y) Place(GameMods.Mod mod, string png)
    {
        string meta = Path.ChangeExtension(png, ".meta.json");
        if (!File.Exists(meta)) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(meta), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            int? Int(string name) => doc.RootElement.TryGetProperty(name, out var v) && v.TryGetInt32(out int n) ? n : null;
            return (Int("x"), Int("y"));
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
        {
            lock (Problems) Problems.Add($"{mod.Name}: {Path.GetFileName(meta)}: {ex.Message}");
            return (null, null);
        }
    }
}
