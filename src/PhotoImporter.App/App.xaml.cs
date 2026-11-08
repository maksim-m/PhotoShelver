using System.Windows;
using System.Windows.Threading;

namespace PhotoImporter.App;

public partial class App : Application
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoImporter", "errors.log");

    protected override void OnStartup(StartupEventArgs e)
    {
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
            $"Произошла непредвиденная ошибка интерфейса. Приложение продолжит работу.\n\n{e.Exception.Message}\n\nПодробности: {LogPath}",
            "PhotoImporter", MessageBoxButton.OK, MessageBoxImage.Warning);
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
