using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace AzPilot.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            StartupDiagnostics.Info("Creating the main window.");
            desktop.MainWindow = new MainWindow();
            StartupDiagnostics.Info("Main window created; starting ASCOM services with the mount disconnected.");
        }

        base.OnFrameworkInitializationCompleted();
    }
}