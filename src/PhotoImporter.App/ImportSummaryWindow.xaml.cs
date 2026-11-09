using System.Diagnostics;
using System.Windows;
using PhotoImporter.App.ViewModels;

namespace PhotoImporter.App;

public partial class ImportSummaryWindow : Window
{
    private readonly string _targetFolder;
    private readonly string? _logPath;

    public ImportSummaryWindow(ImportFinishedInfo info)
    {
        InitializeComponent();
        var result = info.Result;
        _targetFolder = info.TargetFolder;
        _logPath = info.LogPath;

        HeadlineText.Text = result.Cancelled ? "Импорт отменён"
            : result.Failures.Count > 0 ? "Импорт завершён с ошибками"
            : result.Warnings.Count > 0 || info.LogError is not null ? "Импорт завершён с предупреждениями"
            : "Импорт завершён";
        Title = HeadlineText.Text;

        var details = $"Импортировано: {Format.Files(result.Imported.Count)}.";
        if (result.Failures.Count > 0) details += $" Ошибок: {result.Failures.Count}.";
        if (result.Cancelled) details += " Оставшиеся файлы не тронуты.";
        DetailsText.Text = details;

        var warnings = result.Warnings.ToList();
        if (info.LogError is not null) warnings.Add($"Журнал импорта не удалось записать полностью: {info.LogError}");
        if (warnings.Count > 0)
        {
            WarningsText.Text = string.Join("\n\n", warnings);
            WarningsPanel.Visibility = Visibility.Visible;
        }

        if (result.Failures.Count > 0)
        {
            FailureList.ItemsSource = result.Failures;
            FailureList.Visibility = Visibility.Visible;
        }

        OpenLogButton.Visibility = _logPath is not null ? Visibility.Visible : Visibility.Collapsed;
        OpenLogButton.ToolTip = _logPath;
    }

    private void OpenTarget_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_targetFolder}\"") { UseShellExecute = true });
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        if (_logPath is not null) Process.Start(new ProcessStartInfo(_logPath) { UseShellExecute = true });
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
