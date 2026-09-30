using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Stedjcast.Services;

namespace Stedjcast;

public partial class App : System.Windows.Application
{
    public static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));

    // Without these handlers an unhandled exception silently kills the process: here it
    // is shown to the user and always written to crash.log (even with technical logging
    // disabled), so a problem on another PC can be diagnosed.
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            ReportCrash(args.ExceptionObject as Exception, fatal: true);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrashLog(args.Exception);
            args.SetObserved();
        };

        base.OnStartup(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // UI thread error: report it and try to stay open, so a faulty button doesn't cut
        // a live broadcast. If the main window isn't loaded yet (startup error), staying
        // open would only leave an invisible process behind: shut down instead.
        var fatal = Current.MainWindow is not { IsLoaded: true };
        ReportCrash(e.Exception, fatal);
        e.Handled = true;
        if (fatal)
            Current.Shutdown(1);
    }

    private static readonly object CrashLock = new();
    private static int _dialogOpen;
    private static string _lastCrash = "";
    private static DateTime _lastCrashTime;

    private static void ReportCrash(Exception? exception, bool fatal)
    {
        // A recurring error (e.g. on every meter update) must not flood the log with
        // thousands of copies nor open one dialog after another.
        var signature = exception?.ToString() ?? "";
        lock (CrashLock)
        {
            if (!fatal && signature == _lastCrash && DateTime.Now - _lastCrashTime < TimeSpan.FromSeconds(10))
                return;
            _lastCrash = signature;
            _lastCrashTime = DateTime.Now;
        }

        var logPath = WriteCrashLog(exception);
        if (Interlocked.Exchange(ref _dialogOpen, 1) == 1)
            return;

        try
        {
            ShowCrashDialog(exception, fatal, logPath);
        }
        finally
        {
            Interlocked.Exchange(ref _dialogOpen, 0);
        }
    }

    private static void ShowCrashDialog(Exception? exception, bool fatal, string logPath)
    {
        System.Windows.MessageBox.Show(
            (fatal ? "Stedjcast closed because of an unexpected error.\n\n" : "An unexpected error occurred.\n\n") +
            $"{exception?.GetType().Name}: {exception?.Message}\n\n" +
            $"Details saved to:\n{logPath}",
            "Stedjcast - error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private static string WriteCrashLog(Exception? exception)
    {
        var path = Path.Combine(SettingsService.Folder, "crash.log");
        try
        {
            Directory.CreateDirectory(SettingsService.Folder);
            File.AppendAllText(path,
                $"[{DateTime.Now:O}] {Environment.OSVersion} x64={Environment.Is64BitOperatingSystem}\r\n{exception}\r\n\r\n");
        }
        catch
        {
            // If even the file can't be written, the on-screen message is still shown.
        }

        return path;
    }
}
