using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using MMUCS.Desktop.ViewModels;

namespace MMUCS.Desktop.Views.Panels;

public partial class BlockTreeView : UserControl
{
    private MainWindowViewModel _vm;

    public BlockTreeView() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null) _vm.FocusFilterRequested -= FocusFilter;
        _vm = DataContext as MainWindowViewModel;
        if (_vm != null) _vm.FocusFilterRequested += FocusFilter;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // The panel can move between the main window and floating windows; don't leak the handler.
        if (_vm != null) _vm.FocusFilterRequested -= FocusFilter;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_vm != null)
        {
            _vm.FocusFilterRequested -= FocusFilter;
            _vm.FocusFilterRequested += FocusFilter;
        }
    }

    private void FocusFilter()
    {
        FilterBox.Focus();
        FilterBox.SelectAll();
    }

    private void OnFilterKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                _vm?.ClearFilter();
                e.Handled = true;
                break;
            case Key.Enter:
                _vm?.ApplyFilter(); // skip the debounce
                BlockTree.Focus();
                e.Handled = true;
                break;
        }
    }
}
