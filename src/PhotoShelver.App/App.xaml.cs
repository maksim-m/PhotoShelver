using PhotoShelver.Core;
using System.Windows;
using System.Windows.Threading;

namespace PhotoShelver.App;

public partial class App : Application
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoShelver", "errors.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        // Language: Windows' display language; PHOTOSHELVER_LANG=ru|en overrides it (tests, screenshots).
        L.Override(Environment.GetEnvironmentVariable("PHOTOSHELVER_LANG"));

        // A UI glitch must not kill the app (an import may be running in the background).
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log(args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => { Log(args.Exception); args.SetObserved(); };
        base.OnStartup(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception);
        e.Handled = true;
        MessageBox.Show(
            L.F(Strings.AnUnexpectedInterfaceErrorOccurredTheApp, e.Exception.Message, LogPath),
            "PhotoShelver", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static void Log(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch (Exception logError) when (logError is IOException or UnauthorizedAccessException)
        {
            // Nothing else we can do.
        }
    }
}
