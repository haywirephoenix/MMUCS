using Dock.Model.Core;
using MMUCS.Desktop.ViewModels;
using MMUCS.Desktop.ViewModels.Docking;
using Xunit;

namespace MMUCS.Desktop.Tests;

/// <summary>Layouts/layout.default.json is read and places every panel, so the built-in fallback isn't used by accident.</summary>
public class DefaultLayoutTests
{
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
            try { System.Text.Json.JsonSerializer.Deserialize<DockLayoutStore.LayoutFile>(text, new System.Text.Json.JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }); }
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
            Assert.True(Find(root, id) != null, $"{id} is missing from layout.default.json");
    }

    [Fact]
    public void Costume_default_comes_from_the_file()
    {
        var factory = new CostumeDockFactory(new AkosViewModel());
        Assert.NotNull(factory.TryRestore(DockLayoutStore.ReadDefault()?.Costume));
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
