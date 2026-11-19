using PhotoShelver.App.Services;
using PhotoShelver.Core;
using System.Windows;
using System.Windows.Threading;

namespace PhotoShelver.App;

public partial class App : Application
{
    /// <summary>Next to the settings file, so tests (PHOTOSHELVER_SETTINGS) never write into the user's folder.</summary>
    private static string LogPath => Path.Combine(SettingsStore.DataDirectory, "errors.log");

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

    private bool _exiting;

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception);
        e.Handled = true;
        if (_exiting) return; // a follow-up error while shutting down: logged, no second message
        // Without a main window (it failed while opening) there is nothing to keep running: say so and exit,
        // instead of leaving an invisible process behind.
        if (MainWindow is not { IsLoaded: true })
        {
            _exiting = true;
            MessageBox.Show(L.F(Strings.TheAppCouldNotStart, e.Exception.Message, LogPath),
                "PhotoShelver", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
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
