using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using MMUCS.Desktop.ViewModels;

namespace MMUCS.Desktop.Views.Panels;

public partial class ScriptView : UserControl
{
    private ScriptViewModel _vm;
    private GridLength _listWidth = new(220);

    public ScriptView() => InitializeComponent();

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as ScriptViewModel;
        if (_vm != null) _vm.PropertyChanged += OnVmPropertyChanged;
        ApplyScriptsPanel();
    }

    private void OnVmPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScriptViewModel.ShowScriptsPanel)) ApplyScriptsPanel();
    }

    /// <summary>Collapses or restores the script list column (keeping the width it had).</summary>
    private void ApplyScriptsPanel()
    {
        bool show = _vm?.ShowScriptsPanel ?? true;
        var column = ContentGrid.ColumnDefinitions[0];
        if (!show && column.Width.Value > 0) _listWidth = column.Width;
        column.Width = show ? _listWidth : new GridLength(0);
        ContentGrid.ColumnDefinitions[1].Width = new GridLength(show ? 4 : 0);
        ScriptsSplitter.IsVisible = show;
        ScriptList.IsVisible = show;
    }
}
