using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using MMUCS.Desktop.ViewModels.Docking;

namespace MMUCS.Desktop.Views;

/// <summary>
/// Shows the Costume panel with one long-lived <see cref="AkosView"/> per panel instead of a new view
/// each time its tab comes forward. The viewer holds its own docking area, and a nested DockControl
/// rebuilt from a layout another DockControl already showed comes up empty; reusing the view keeps
/// that dock (and the viewer's scroll / zoom state) alive across tab switches and re-docking.
/// </summary>
public sealed class CostumeViewTemplate : IDataTemplate
{
    private static readonly ConditionalWeakTable<CostumeDocument, AkosView> Views = new();

    public bool Match(object data) => data is CostumeDocument;

    public Control Build(object param)
    {
        if (param is not CostumeDocument doc) return null;
        return Views.GetValue(doc, d => new AkosView { DataContext = d.Main.Costume });
    }
}
