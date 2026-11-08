using System.Diagnostics;
using System.Windows;
using PhotoImporter.Core.Importing;

namespace PhotoImporter.App;

public partial class ImportSummaryWindow : Window
{
    private readonly string _targetFolder;

    public ImportSummaryWindow(ImportResult result, string targetFolder)
    {
        InitializeComponent();
        _targetFolder = targetFolder;

        HeadlineText.Text = result.Cancelled ? "Импорт отменён"
            : result.Failures.Count > 0 ? "Импорт завершён с ошибками"
            : result.Warnings.Count > 0 ? "Импорт завершён с предупреждениями"
            : "Импорт завершён";
        Title = HeadlineText.Text;

        var details = $"Импортировано: {Format.Files(result.Imported.Count)}.";
        if (result.Failures.Count > 0) details += $" Ошибок: {result.Failures.Count}.";
        if (result.Cancelled) details += " Оставшиеся файлы не тронуты.";
        DetailsText.Text = details;

        if (result.Warnings.Count > 0)
        {
            WarningsText.Text = string.Join("\n\n", result.Warnings);
            WarningsPanel.Visibility = Visibility.Visible;
        }

        if (result.Failures.Count > 0)
        {
            FailureList.ItemsSource = result.Failures;
            FailureList.Visibility = Visibility.Visible;
        }
    }

    private void OpenTarget_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_targetFolder}\"") { UseShellExecute = true });
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
