using System.Diagnostics;
using System.Windows;
using PhotoShelver.App.ViewModels;
using PhotoShelver.Core;

namespace PhotoShelver.App;

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

        HeadlineText.Text = result.Cancelled ? L.T("Import cancelled", "Импорт отменён")
            : result.Failures.Count > 0 ? L.T("Import finished with errors", "Импорт завершён с ошибками")
            : result.Warnings.Count > 0 || info.LogError is not null ? L.T("Import finished with warnings", "Импорт завершён с предупреждениями")
            : Ui.ImportFinished;
        Title = HeadlineText.Text;

        var details = L.T($"Imported: {Format.Files(result.Imported.Count)}.", $"Импортировано: {Format.Files(result.Imported.Count)}.");
        if (result.Failures.Count > 0) details += L.T($" Errors: {result.Failures.Count}.", $" Ошибок: {result.Failures.Count}.");
        if (result.Cancelled) details += L.T(" The remaining files were not touched.", " Оставшиеся файлы не тронуты.");
        DetailsText.Text = details;

        var warnings = result.Warnings.ToList();
        if (info.LogError is not null) warnings.Add(L.T($"The import log could not be written completely: {info.LogError}", $"Журнал импорта не удалось записать полностью: {info.LogError}"));
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
