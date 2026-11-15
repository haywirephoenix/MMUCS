using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using MMUCS.Desktop.ViewModels.Docking;
using MMUCS.Desktop.Views;
using MMUCS.Desktop.Views.Costume;
using MMUCS.Desktop.Views.Panels;

namespace MMUCS.Desktop;

/// <summary>
/// Builds the panel content for each dockable. Registered app-wide so floating (host) windows
/// resolve it too.
/// <para>
/// This is an explicit table rather than a name convention: the view names don't follow the
/// view-model names (BlocksTool → BlockTreeView), and each view binds to part of its dockable
/// (Main, Main.Room, Akos…) rather than the dockable itself. A table also needs no reflection, so it
/// stays safe under trimming. To add a panel, add one line here.
/// </para>
/// <para>
/// The Costume document isn't here: it reuses one long-lived view (see
/// <see cref="CostumeViewTemplate"/>), which is registered before this locator.
/// </para>
/// </summary>
public sealed class ViewLocator : IDataTemplate
{
    private static readonly Dictionary<Type, Func<object, Control>> Factories = new()
    {
        // Main window panels.
        [typeof(BlocksTool)] = d => new BlockTreeView { DataContext = ((BlocksTool)d).Main },
        [typeof(MetadataTool)] = d => new MetadataView { DataContext = ((MetadataTool)d).Main },
        [typeof(HexTool)] = d => new HexPanelView { DataContext = ((HexTool)d).Main },
        [typeof(RoomDocument)] = d => new RoomView { DataContext = ((RoomDocument)d).Main.Room },
        [typeof(ObjectDocument)] = d => new ObjectImageView { DataContext = ((ObjectDocument)d).Main.ObjectImage },
        [typeof(ScriptDocument)] = d => new ScriptView { DataContext = ((ScriptDocument)d).Main.Script },

        // The Costume viewer's own panels.
        [typeof(CostumeCelsTool)] = d => new CostumeCelsView { DataContext = ((CostumeCelsTool)d).Akos },
        [typeof(CostumePreviewDocument)] = d => new CostumePreviewView { DataContext = ((CostumePreviewDocument)d).Akos },
        [typeof(CostumeChoresTool)] = d => new CostumeChoresView { DataContext = ((CostumeChoresTool)d).Akos },
        [typeof(CostumePaletteTool)] = d => new CostumePaletteView { DataContext = ((CostumePaletteTool)d).Akos },
    };

    public bool Match(object data) => data != null && Factories.ContainsKey(data.GetType());

    public Control Build(object param) =>
        param != null && Factories.TryGetValue(param.GetType(), out var create)
            ? create(param)
            : new TextBlock { Text = $"No view for {param?.GetType().Name}" };
}
