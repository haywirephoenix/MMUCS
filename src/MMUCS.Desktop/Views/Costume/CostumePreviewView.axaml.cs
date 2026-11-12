using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using MMUCS.Desktop.Controls;
using MMUCS.Desktop.ViewModels;

namespace MMUCS.Desktop.Views.Costume;

/// <summary>The costume canvas with the cel / frame info under it.</summary>
public partial class CostumePreviewView : UserControl
{
    private AkosViewModel _subscribedVm;

    public CostumePreviewView()
    {
        InitializeComponent();
        // DataContextChanged += (_, _) => Subscribe();
        // Docking the panel elsewhere unloads and reloads the view with the same DataContext.
        // Loaded += (_, _) => Subscribe();
        // Unloaded += (_, _) => Unsubscribe();
    }

    public CelCanvas CelCanvas => Canvas;

    private void Subscribe()
    {
        Unsubscribe();
        if (DataContext is not AkosViewModel vm) return;
        _subscribedVm = vm;
        _subscribedVm.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void Unsubscribe()
    {
        if (_subscribedVm == null) return;
        _subscribedVm.PropertyChanged -= OnViewModelPropertyChanged;
        _subscribedVm = null;
    }

    /// <summary>New cel / frame info starts at the top, where its title line is.</summary>
    private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        // if (e.PropertyName == nameof(AkosViewModel.CelInfo))
        //     Dispatcher.UIThread.Post(() => InfoScrollViewer?.ScrollToHome());
    }
}
