using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MMUCS.Desktop.ViewModels;
using MMUCS.Desktop.Views;

namespace MMUCS.Desktop;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainWindowViewModel();
            desktop.MainWindow = new MainWindow { DataContext = vm };

            // "MMUCS.exe path/to/COMI.LA1" opens that file; otherwise optionally reopen the last one.
            _ = vm.StartupAsync(desktop.Args);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
