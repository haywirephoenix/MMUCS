using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.VisualTree;
using MMUCS.Desktop.ViewModels;
using MMUCS.Desktop.ViewModels.Docking;

namespace MMUCS.Desktop.Views;

/// <summary>
/// Shows the Costume panel with one long-lived <see cref="AkosView"/> for the costume view model,
/// instead of a new view each time its tab comes forward. The viewer holds its own docking area, and
/// that inner layout can only be shown by one DockControl: a second view of it comes up empty, or
/// (after Layout ▸ Reset, while the old view still exists) breaks the window's layout. Reusing one view
/// keeps the inner dock, and the viewer's scroll and zoom, alive across tab switches, re-docking and
/// layout resets.
/// </summary>
public sealed class CostumeViewTemplate : IDataTemplate
{
    private static readonly ConditionalWeakTable<AkosViewModel, AkosView> Views = new();

    public bool Match(object data) => data is CostumeDocument;

    public Control Build(object param)
    {
        if (param is not CostumeDocument doc) return null;
        var view = Views.GetValue(doc.Main.Costume, vm => new AkosView { DataContext = vm });
        // Still the child of a presenter from a discarded layout (or the panel's old place): free it.
        if (view.GetVisualParent() is ContentPresenter old) old.Content = null;
        return view;
    }
}
