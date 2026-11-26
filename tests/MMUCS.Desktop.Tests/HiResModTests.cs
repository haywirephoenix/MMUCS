using MMUCS.Core.Imaging;
using MMUCS.Core.Scumm;
using MMUCS.Desktop.ViewModels;
using Xunit;

namespace MMUCS.Desktop.Tests;

/// <summary>
/// The Room viewer's Mod images toggle draws a hi-res mod's background and object images, as the game window does:
/// at the room's size, the game's own images kept for picking. Needs MMUCS_GAME.
/// </summary>
public class HiResModTests
{
    private static string Game => Environment.GetEnvironmentVariable("MMUCS_GAME") is { } g && File.Exists(g) ? g : null;

    [Fact]
    public async Task The_room_viewer_draws_a_hi_res_mod_s_background_and_object_images_while_mod_images_are_shown()
    {
        if (Game == null) return;
        var root = ScummProject.Load(ScummProject.FindRelatedFiles(Game), null);
        RoomModel room = null;
        foreach (var lflf in root.FindChildrenRecursive(ScummTag.LFLF))
            if (RoomModel.TryCreate(lflf, out var r) && r.RoomNo == 22) room = r;
        Assert.NotNull(room);
        var caber = room.Objects.Single(o => o.Name == "clring-a-haggis-caber");

        string dir = Path.Combine(Path.GetTempPath(), "mmucs-hires-viewer-" + Guid.NewGuid().ToString("N"));
        string folder = Path.Combine(dir, "_mods", "Upscaled", "rooms", "022_clring-a");
        Directory.CreateDirectory(Path.Combine(folder, "objects"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "_mods", "Upscaled", "mod.json"), """{ "enabled": true, "scale": 2 }""");
            Png(Path.Combine(folder, "background.png"), room.Width * 2, room.Height * 2);
            Png(Path.Combine(folder, "objects", ScummExporters.ObjectFrameFile(caber, 0)), caber.Width * 2, caber.Height * 2);
            File.WriteAllText(Path.Combine(folder, "objects", Path.ChangeExtension(ScummExporters.ObjectFrameFile(caber, 0), ".meta.json")), """{ "x": 100, "y": 50 }""");
            var mods = HiResMods.Load(Path.Combine(dir, "COMI.LA0"));

            var shown = new RoomViewModel { HiResMods = mods, ShowModImages = true };
            await shown.LoadAsync(room);
            Assert.NotNull(shown.HiResBackground);
            Assert.Equal((room.Width * 2, (float)room.Width, (float)room.Height), (shown.HiResBackground.Image.Width, shown.HiResBackground.Width, shown.HiResBackground.Height));
            var layer = shown.ObjectLayers.Single(l => l.Object == caber && !l.IsActorOnly);
            Assert.NotNull(layer.HiRes);
            // Where its .meta.json puts it, at the object's size; the game's image stays for picking it there.
            Assert.Equal((100f, 50f, (float)caber.Width, (float)caber.Height), (layer.HiRes.X, layer.HiRes.Y, layer.HiRes.Width, layer.HiRes.Height));
            Assert.Equal((100, 50), (layer.X, layer.Y));
            Assert.NotNull(layer.Image);

            // With mod images off, the game's.
            var game = new RoomViewModel { HiResMods = mods, ShowModImages = false };
            await game.LoadAsync(room);
            Assert.Null(game.HiResBackground);
            var plain = game.ObjectLayers.Single(l => l.Object == caber && !l.IsActorOnly);
            Assert.Null(plain.HiRes);
            Assert.Equal((caber.X, caber.Y), (plain.X, plain.Y));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task A_full_width_object_shown_as_the_background_has_its_hi_res_picture_too()
    {
        if (Game == null) return;
        var root = ScummProject.Load(ScummProject.FindRelatedFiles(Game), null);
        RoomModel room = null;
        foreach (var lflf in root.FindChildrenRecursive(ScummTag.LFLF))
            if (room == null && RoomModel.TryCreate(lflf, out var r) && r.RoomNo == 3) room = r;
        Assert.NotNull(room);
        var backdrop = room.Objects.Single(o => o.Name == "inventory-bg-object");

        string dir = Path.Combine(Path.GetTempPath(), "mmucs-hires-icons-" + Guid.NewGuid().ToString("N"));
        string objects = Path.Combine(dir, "_mods", "Upscaled", "rooms", ScummExporters.RoomFolderName(room), "objects");
        Directory.CreateDirectory(objects);
        try
        {
            File.WriteAllText(Path.Combine(dir, "_mods", "Upscaled", "mod.json"), """{ "enabled": true, "scale": 4 }""");
            Png(Path.Combine(objects, ScummExporters.ObjectFrameFile(backdrop, 0)), backdrop.Width * 4, backdrop.Height * 4);
            var viewer = new RoomViewModel { HiResMods = HiResMods.Load(Path.Combine(dir, "COMI.LA0")), ShowModImages = true };
            await viewer.LoadAsync(room);
            // The icons' room has no background of its own: the inventory's backdrop is the one shown, in hi-res.
            Assert.Same(backdrop, viewer.BackgroundObject);
            Assert.NotNull(viewer.HiResBackground);
            Assert.Equal((backdrop.Width * 4, (float)backdrop.Width), (viewer.HiResBackground.Image.Width, viewer.HiResBackground.Width));
        }
        finally { Directory.Delete(dir, true); }
    }

    private static void Png(string file, int w, int h)
    {
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < rgba.Length; i += 4) (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = (200, 100, 50, 255);
        using var f = File.Create(file);
        PngWriter.Write(f, w, h, rgba);
    }
}
