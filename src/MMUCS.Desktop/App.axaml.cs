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
    #if DEBUG
        this.AttachDeveloperTools();
    #endif
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

            // A second launch brings this window forward and opens the file it was given.
            var window = desktop.MainWindow;
            Services.SingleInstance.Listen(file => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                window.Activate();
                if (!string.IsNullOrEmpty(file)) _ = vm.OpenFileAsync(file);
            }));
        }

        base.OnFrameworkInitializationCompleted();
    }
}
