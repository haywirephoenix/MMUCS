using Avalonia.Controls;
using MMUCS.Desktop.ViewModels;

namespace MMUCS.Desktop.Views.Room;

/// <summary>The Room viewer's Preview: the toolbar and the room canvas.</summary>
public partial class RoomPreviewView : UserControl
{
    public RoomPreviewView()
    {
        InitializeComponent();
        // Right-click on the room: show/hide the object under the pointer, or all objects.
        Canvas.ObjectContextRequested += obj => { if (Vm is { } vm) RoomObjectMenu.Show(vm, obj, Canvas); };
        // Ctrl / Shift click: the Objects list owns the multi-selection.
        Canvas.ObjectModifierClicked += (obj, mods) => Vm?.RequestModifierSelect(obj, mods);
    }

    private RoomViewModel Vm => DataContext as RoomViewModel;
}
