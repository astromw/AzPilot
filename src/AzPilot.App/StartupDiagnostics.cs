using System.Runtime.InteropServices;
using System.Text;

namespace AzPilot.App;

internal static class StartupDiagnostics
{
    private static readonly object Sync = new();
    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AzPilot", "logs");
    public static string LogPath { get; } = Path.Combine(LogDirectory, $"startup-{DateTime.Now:yyyyMMdd}.log");

    public static void Info(string message) => Write("INFO", message, null);
    public static void Fatal(string message, Exception exception) => Write("FATAL", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(LogPath, $"[{DateTimeOffset.Now:O}] {level} {message}\r\n{exception}\r\n", Encoding.UTF8);
            }
        }
        catch { }
    }

    public static void ShowFatalMessage(Exception exception)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var text = $"AzPilot could not start.\r\n\r\n{exception.Message}\r\n\r\nDiagnostic log:\r\n{LogPath}";
            MessageBox(IntPtr.Zero, text, "AzPilot startup error", 0x10);
        }
        catch { }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);
}
