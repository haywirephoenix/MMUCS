using System;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using MMUCS.Desktop.ViewModels.Docking;

namespace MMUCS.Desktop.Views;

/// <summary>
/// Shows each viewer that has its own docking area (Costume, Object, Room) with one long-lived
/// <see cref="ViewerDockView"/>, instead of a new view each time its tab comes forward. The inner layout
/// can only be shown by one DockControl: a second view of it comes up empty, or
/// (after Layout ▸ Reset, while the old view still exists) breaks the window's layout. Reusing one view
/// keeps the inner dock, and the viewer's scroll and zoom, alive across tab switches, re-docking,
/// floating and layout resets.
/// </summary>
public sealed class ViewerViewTemplate : IDataTemplate
{
    private static readonly ConditionalWeakTable<ViewerDock, ViewerDockView> Views = new();

    private static ViewerDock DockOf(object data) => data switch
    {
        CostumeDocument d => d.Main.Costume.ViewerLayout,
        ObjectDocument d => d.Main.ObjectImage.ViewerLayout,
        RoomDocument d => d.Main.Room.ViewerLayout,
        _ => null,
    };

    public bool Match(object data) => DockOf(data) != null;

    /// <summary>A new host each time; the shared view moves into whichever host is on screen.</summary>
    public Control Build(object param) =>
        DockOf(param) is { } dock ? new ViewerViewHost(Views.GetValue(dock, d => new ViewerDockView { DataContext = d })) : null;
}

/// <summary>
/// Holds a viewer's shared view while it's on screen. Dock presents panel content through deferred
/// presenters, so clearing an old presenter doesn't free the view at once; hosts hand it over between
/// themselves instead, synchronously, so it's only ever in one tree. A host in Dock's drag preview
/// leaves it alone, and one in a floating window takes it once the window is open.
/// </summary>
internal sealed class ViewerViewHost : Decorator
{
    private readonly ViewerDockView _view;
    private Window _waitingFor;

    public ViewerViewHost(ViewerDockView view) => _view = view;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Take the view after this attach pass ends: moving a subtree mid-attach isn't safe.
        Avalonia.Threading.Dispatcher.UIThread.Post(TryTake);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        StopWaiting();
        base.OnDetachedFromVisualTree(e);
    }

    private void TryTake()
    {
        if (ReferenceEquals(Child, _view)) return;
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return; // no longer on screen
        // Dock's own windows (drag preview, adorners) only show a picture of the panel for a moment.
        if (top is DragPreviewWindow or DockAdornerWindow || this.FindAncestorOfType<DragPreviewControl>() != null) return;
        // A floating window that isn't open yet: take the view once it is.
        if (top is Window w && !w.IsVisible)
        {
            if (_waitingFor == null)
            {
                _waitingFor = w;
                w.Opened += OnWindowOpened;
            }
            return;
        }
        if (_view.GetVisualParent() is ViewerViewHost previous) previous.Child = null;
        else if (_view.GetVisualParent() is ContentPresenter presenter) presenter.Content = null;
        if (_view.GetVisualParent() == null && _view.Parent == null) Child = _view;
    }

    private void OnWindowOpened(object sender, EventArgs e)
    {
        StopWaiting();
        Avalonia.Threading.Dispatcher.UIThread.Post(TryTake);
    }

    private void StopWaiting()
    {
        if (_waitingFor != null) _waitingFor.Opened -= OnWindowOpened;
        _waitingFor = null;
    }
}
