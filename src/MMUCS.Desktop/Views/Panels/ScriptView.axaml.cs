using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using MMUCS.Desktop.Services;
using MMUCS.Desktop.ViewModels;

namespace MMUCS.Desktop.Views.Panels;

public partial class ScriptView : UserControl
{
    private ScriptViewModel _vm;
    private GridLength _listWidth = new(220);
    private IBrush[] _brushes;

    public ScriptView() => InitializeComponent();

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as ScriptViewModel;
        if (_vm != null) _vm.PropertyChanged += OnVmPropertyChanged;
        ApplyScriptsPanel();
        ShowCode();
    }

    private void OnVmPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ScriptViewModel.ShowScriptsPanel): ApplyScriptsPanel(); break;
            case nameof(ScriptViewModel.Tokens):
            case nameof(ScriptViewModel.ShowOffsets): ShowCode(); break;
            case nameof(ScriptViewModel.Text): if (_vm.Tokens == null) ShowCode(); break;
        }
    }

    /// <summary>The script as coloured runs (offsets left out when hidden), or the plain message text.</summary>
    private void ShowCode()
    {
        if (_vm?.Tokens is not { } tokens)
        {
            Code.Inlines = null;
            Code.Text = _vm?.Text;
            return;
        }
        _brushes ??= LoadBrushes();
        bool offsets = _vm.ShowOffsets;
        var inlines = new InlineCollection();
        foreach (var t in tokens)
        {
            if (t.Kind == ScriptTokenKind.Offset && !offsets) continue;
            var run = new Run(t.Text);
            if (_brushes[(int)t.Kind] is { } brush) run.Foreground = brush;
            inlines.Add(run);
        }
        Code.Text = null;
        Code.Inlines = inlines;
    }

    private IBrush[] LoadBrushes()
    {
        var kinds = System.Enum.GetValues<ScriptTokenKind>();
        var brushes = new IBrush[kinds.Length];
        foreach (var kind in kinds)
            if (kind != ScriptTokenKind.Plain && this.TryFindResource($"Script{kind}Brush", out var r) && r is IBrush b)
                brushes[(int)kind] = b;
        return brushes;
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
