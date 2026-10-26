using Avalonia;
using System.Runtime.InteropServices;

namespace AzPilot.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            if (eventArgs.ExceptionObject is Exception exception)
                StartupDiagnostics.Fatal("Unhandled application exception", exception);
        };
        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            StartupDiagnostics.Fatal("Unobserved background exception", eventArgs.Exception);
            eventArgs.SetObserved();
        };

        try
        {
            StartupDiagnostics.Info($"Starting AzPilot 0.3.9 | {RuntimeInformation.OSDescription} | {RuntimeInformation.OSArchitecture} | .NET {Environment.Version}");
            StartupDiagnostics.Info($"Executable: {Environment.ProcessPath}");
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            StartupDiagnostics.Info("Application exited normally.");
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Fatal("Startup failed", exception);
            StartupDiagnostics.ShowFatalMessage(exception);
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
