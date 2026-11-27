using MMUCS.Core.Scumm;
using MMUCS.Desktop.ViewModels;
using Xunit;

namespace MMUCS.Desktop.Tests;

/// <summary>
/// The Inspector's Position: a mod's place can be moved (each change previewed), saved and put back; the game's is
/// only shown. And saving writes the mod's meta.json without losing what else it holds.
/// </summary>
public class PositionTests
{
    [Fact]
    public void A_mod_s_position_previews_each_change_and_saves_or_reverts_and_the_game_s_does_neither()
    {
        var previews = new List<(int, int)>();
        var saves = new List<(int, int)>();
        var p = new PositionViewModel();
        p.Show("cel 48", "mod", true, -13, 0, (x, y) => previews.Add((x, y)), (x, y) => saves.Add((x, y)));
        Assert.True(p.IsAvailable && p.IsModded);
        Assert.False(p.IsDirty);
        Assert.False(p.SaveCommand.CanExecute(null));
        Assert.Empty(previews); // showing isn't a change

        p.X = -10;
        Assert.Equal([(-10, 0)], previews);
        Assert.True(p.IsDirty && p.SaveCommand.CanExecute(null) && p.RevertCommand.CanExecute(null));
        p.SaveCommand.Execute(null);
        Assert.Equal([(-10, 0)], saves);
        Assert.False(p.IsDirty);

        p.Y = 4;
        p.RevertCommand.Execute(null);
        Assert.Equal((-10m, 0m), (p.X, p.Y));
        Assert.Equal((-10, 0), previews[^1]); // put back in the viewers too
        Assert.False(p.IsDirty);

        // The game's: shown, never previewed or saved.
        p.Show("cel 3", "game", false, 0, 0, (x, y) => previews.Add((x, y)), (x, y) => saves.Add((x, y)));
        int before = previews.Count;
        p.X = 5;
        Assert.Equal(before, previews.Count);
        Assert.False(p.IsDirty);
        p.Clear();
        Assert.False(p.IsAvailable);
    }

    [Fact]
    public void The_move_tool_moves_a_mod_s_position_with_one_preview_and_leaves_the_game_s_alone()
    {
        var previews = new List<(int, int)>();
        var p = new PositionViewModel();
        p.Show("object", "mod", true, 100, 50, (x, y) => previews.Add((x, y)));
        p.MoveTo(112, 47);
        Assert.Equal([(112, 47)], previews); // both at once, not x then y
        Assert.Equal((112m, 47m), (p.X, p.Y));
        Assert.True(p.IsDirty && p.SaveCommand.CanExecute(null));
        p.MoveTo(112, 47);
        Assert.Single(previews); // the same place again is no change
        p.MoveBy(-2, 3); // the move tool's steps on a cel
        Assert.Equal((110, 50), previews[^1]);

        p.Show("object", "game", false, 10, 20, (x, y) => previews.Add((x, y)));
        int before = previews.Count;
        p.MoveTo(0, 0);
        p.MoveBy(1, 1);
        Assert.Equal((10m, 20m), (p.X, p.Y));
        Assert.Equal(before, previews.Count);
    }

    [Fact]
    public void Saving_a_place_or_an_offset_keeps_the_rest_of_the_meta_json()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mmucs-meta-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // An object image's .meta.json: x and y set, its other fields kept.
            string png = Path.Combine(dir, "041_clring-a-haggis-caber_f0.png");
            File.WriteAllText(Path.ChangeExtension(png, ".meta.json"), """{ "x": 1, "y": 2, "note": "keep" }""");
            ObjectImageMods.SavePlace(new ObjectImageMods.Image("Wide", png, 1, 2), 236, 392);
            using (var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(png, ".meta.json"))))
            {
                var root = doc.RootElement;
                Assert.Equal((236, 392, "keep"), (root.GetProperty("x").GetInt32(), root.GetProperty("y").GetInt32(), root.GetProperty("note").GetString()));
            }

            // A costume folder's meta.json: the cel set, the others and other fields kept, the cels in order.
            File.WriteAllText(Path.Combine(dir, "meta.json"), """{ "author": "me", "cels": { "cel_048": { "x": -13, "y": 0 }, "cel_037": { "x": -13, "y": -2 } } }""");
            CostumeCelMods.SaveOffsets(dir, new Dictionary<string, (int, int)> { ["cel_044.png"] = (-13, -1), ["cel_048"] = (-12, 0) });
            using (var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "meta.json"))))
            {
                var root = doc.RootElement;
                Assert.Equal("me", root.GetProperty("author").GetString());
                var cels = root.GetProperty("cels").EnumerateObject().Select(c => (c.Name, c.Value.GetProperty("x").GetInt32(), c.Value.GetProperty("y").GetInt32())).ToList();
                Assert.Equal([("cel_037", -13, -2), ("cel_044", -13, -1), ("cel_048", -12, 0)], cels);
            }
        }
        finally { Directory.Delete(dir, true); }
    }
}
