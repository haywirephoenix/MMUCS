using Dock.Model.Core;
using MMUCS.Desktop.ViewModels;
using MMUCS.Desktop.ViewModels.Docking;
using Xunit;

namespace MMUCS.Desktop.Tests;

/// <summary>Layouts/layout.default.json is read and places every panel, so the built-in fallback isn't used by accident.</summary>
public class DefaultLayoutTests
{
    private static readonly System.Text.Json.JsonSerializerOptions NamedFloats = new() { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };

    [Fact]
    public void Default_layout_file_is_embedded_and_readable()
    {
        var asm = typeof(DockLayoutStore).Assembly;
        string names = string.Join(", ", asm.GetManifestResourceNames());
        using (var stream = asm.GetManifestResourceStream("layout.default.json"))
        {
            Assert.True(stream != null, "resources: " + names);
            using var reader = new System.IO.StreamReader(stream);
            var text = reader.ReadToEnd();
            try { System.Text.Json.JsonSerializer.Deserialize<DockLayoutStore.LayoutFile>(text, NamedFloats); }
            catch (System.Exception ex) { Assert.Fail(ex.Message); }
        }
        var file = DockLayoutStore.ReadDefault();
        Assert.NotNull(file);
        Assert.NotNull(file.Root);
        Assert.NotNull(file.Costume);
    }

    [Fact]
    public void Main_default_comes_from_the_file_and_places_every_panel()
    {
        var factory = new MmucsDockFactory(null);
        var root = DockLayoutStore.TryRestore(factory, DockLayoutStore.ReadDefault(), requireAllPanels: true);
        Assert.NotNull(root);
        foreach (var id in MmucsDockFactory.PanelIds)
            Assert.True(Find(root, id) != null || factory.IsHidden(id), $"{id} is missing from layout.default.json");
        // The Inspector shows costumes and objects; their own panels start hidden.
        Assert.NotNull(Find(root, MmucsDockFactory.InspectorId));
        Assert.True(factory.IsHidden(MmucsDockFactory.CostumeId));
        Assert.True(factory.IsHidden(MmucsDockFactory.ObjectId));
    }

    [Fact]
    public void Layout_from_before_the_inspector_gets_it_on_the_right_and_hides_costume_and_object()
    {
        // The 2.0 default: Costume and Object placed, no Inspector.
        var old = new DockLayoutStore.LayoutFile
        {
            Version = 1,
            Root = new DockLayoutStore.Node
            {
                Kind = "split", Id = "MainDock", Orientation = "Horizontal",
                Children =
                [
                    new() { Kind = "tools", Id = "LeftDock", Proportion = 0.25, Panels = ["Blocks", "Metadata", "Hex"] },
                    new() { Kind = "documents", Id = "Documents", Proportion = 0.45, Panels = ["Room", "Script"] },
                    new() { Kind = "documents", Id = "CostumeDocuments", Proportion = 0.30, Panels = ["Costume", "Object"] },
                ],
            },
        };
        var factory = new MmucsDockFactory(null);
        var root = DockLayoutStore.TryRestore(factory, old);
        Assert.NotNull(root);
        factory.InitLayout(root);
        factory.AdoptInspector(root);

        var inspector = Find(root, MmucsDockFactory.InspectorId);
        Assert.NotNull(inspector);
        var main = (Dock.Model.Controls.IProportionalDock)root.DefaultDockable;
        Assert.Same(inspector.Owner, main.VisibleDockables[^1]);
        Assert.Null(Find(root, MmucsDockFactory.CostumeId));
        Assert.Null(Find(root, MmucsDockFactory.ObjectId));
        Assert.True(factory.IsHidden(MmucsDockFactory.CostumeId));

        // Adopting again (the next start, with the Inspector saved) changes nothing.
        int panes = main.VisibleDockables.Count;
        factory.AdoptInspector(root);
        Assert.Equal(panes, main.VisibleDockables.Count);
    }

    [Fact]
    public void Costume_default_comes_from_the_file()
    {
        var factory = new CostumeDockFactory(new AkosViewModel());
        Assert.NotNull(factory.TryRestore(DockLayoutStore.ReadDefault()?.Costume));
    }

    [Fact]
    public void Room_default_comes_from_the_file()
    {
        var factory = new RoomDockFactory(new RoomViewModel());
        Assert.NotNull(factory.TryRestore(DockLayoutStore.ReadDefault()?.Room));
    }

    [Fact]
    public void Object_default_comes_from_the_file()
    {
        var factory = new ObjectDockFactory(new ObjectImageViewModel(new RoomViewModel()));
        Assert.NotNull(factory.TryRestore(DockLayoutStore.ReadDefault()?.Object));
    }

    [Fact]
    public void Hidden_objects_panel_is_saved_and_restored_hidden()
    {
        var room = new RoomViewModel();
        room.ShowObjectsPanel = false;
        Assert.False(room.ViewerLayout.IsPanelShown(RoomDockFactory.ObjectsId));

        // Capture it as the layout file does, then rebuild.
        var json = SaveViewers(room.ViewerLayout);
        var restored = new RoomViewModel { ShowObjectsPanel = false };
        restored.ViewerLayout.Restore(json.Room);
        Assert.Equal("RoomRoot", restored.ViewerLayout.Layout.Id);
        Assert.False(restored.ViewerLayout.IsPanelShown(RoomDockFactory.ObjectsId));

        restored.ShowObjectsPanel = true;
        Assert.True(restored.ViewerLayout.IsPanelShown(RoomDockFactory.ObjectsId));
        var objects = Find(restored.ViewerLayout.Layout, RoomDockFactory.ObjectsId);
        Assert.Equal("ObjectsDock", objects.Owner.Id);
    }

    /// <summary>Round-trips a room viewer's arrangement through the layout file's JSON.</summary>
    private static DockLayoutStore.LayoutFile SaveViewers(ViewerDock room)
    {
        var node = DockLayoutStore.CaptureViewer(room);
        var options = new System.Text.Json.JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
        var json = System.Text.Json.JsonSerializer.Serialize(new DockLayoutStore.LayoutFile { Room = node }, options);
        return System.Text.Json.JsonSerializer.Deserialize<DockLayoutStore.LayoutFile>(json, options);
    }

    private static IDockable Find(IDockable node, string id)
    {
        if (node.Id == id && node is not IDock) return node;
        if (node is IDock dock && dock.VisibleDockables != null)
            foreach (var child in dock.VisibleDockables)
                if (Find(child, id) is { } found) return found;
        return null;
    }
}
