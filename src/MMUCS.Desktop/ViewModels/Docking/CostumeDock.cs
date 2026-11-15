using System.Collections.Generic;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;

namespace MMUCS.Desktop.ViewModels.Docking;

// The Costume viewer's own panels. They share the costume view model, like the main panels share
// the main one; ViewLocator maps each type to its view.

public sealed class CostumeCelsTool(AkosViewModel akos) : Tool { public AkosViewModel Akos { get; } = akos; }
public sealed class CostumeChoresTool(AkosViewModel akos) : Tool { public AkosViewModel Akos { get; } = akos; }
public sealed class CostumePaletteTool(AkosViewModel akos) : Tool { public AkosViewModel Akos { get; } = akos; }
public sealed class CostumePreviewDocument(AkosViewModel akos) : Document { public AkosViewModel Akos { get; } = akos; }

/// <summary>Docking inside the Costume viewer: Cels | Preview | Chores and Resolved palette as tabs (see <see cref="ViewerDockFactory"/>).</summary>
public sealed class CostumeDockFactory(AkosViewModel akos) : ViewerDockFactory(Group, "Costume")
{
    public const string Group = "costume";
    public const string CelsId = "Cels", ChoresId = "Chores", PaletteId = "Palette", PreviewId = "Preview";
    public static readonly string[] PanelIds = { CelsId, ChoresId, PaletteId, PreviewId };

    public override IReadOnlyList<string> Ids => PanelIds;

    protected override IEnumerable<ViewerPanel> DefinePanels() => new ViewerPanel[]
    {
        new(CelsId, "Cels", new CostumeCelsTool(akos)),
        new(ChoresId, "Chores", new CostumeChoresTool(akos)),
        new(PaletteId, "Resolved palette", new CostumePaletteTool(akos)),
        new(PreviewId, "Preview", new CostumePreviewDocument(akos)),
    };

    protected override IDockable CreateBuiltInContent(Dictionary<string, IDockable> p)
    {
        var cels = NewToolDock("CelsDock", CreateList(p[CelsId]));
        cels.Proportion = 0.18;
        var preview = NewDocumentDock("PreviewDock", CreateList(p[PreviewId]));
        preview.Proportion = 0.60;
        var side = NewToolDock("SideDock", CreateList(p[ChoresId], p[PaletteId]));
        side.Proportion = 0.22;
        return Split("CostumeMain", Orientation.Horizontal, cels, preview, side);
    }

    public override DockLayoutStore.Node DefaultNode(DockLayoutStore.LayoutFile file) => file.Costume;
}
