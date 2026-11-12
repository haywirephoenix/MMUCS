using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MMUCS.Desktop.ViewModels;
using MMUCS.Desktop.Views;

namespace MMUCS.Desktop;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        Controls.DockCollapseBehavior.Install();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainWindowViewModel();
            desktop.MainWindow = new MainWindow { DataContext = vm };
            // Without this, open floating panel windows would keep the process alive after the
            // main window closes.
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

            // "MMUCS.exe path/to/COMI.LA1" opens that file; otherwise optionally reopen the last one.
            _ = vm.StartupAsync(desktop.Args);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
