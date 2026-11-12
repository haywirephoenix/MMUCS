using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MMUCS.Desktop.ViewModels;

namespace MMUCS.Desktop.Views;

public partial class AkosView : UserControl
{
    private AkosViewModel _subscribedVm;

    public AkosView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Subscribe();
        // Docking or floating the panel unloads and reloads the view with the same DataContext.
        Loaded += (_, _) => Subscribe();
        Unloaded += (_, _) => Unsubscribe();
    }

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
        if (e.PropertyName == nameof(AkosViewModel.CelInfo))
            Dispatcher.UIThread.Post(() => InfoScrollViewer?.ScrollToHome());
    }

    private void OnFitClick(object sender, RoutedEventArgs e) => Canvas.ZoomToFit();
    private void OnActualClick(object sender, RoutedEventArgs e) => Canvas.ZoomToActual();
}
