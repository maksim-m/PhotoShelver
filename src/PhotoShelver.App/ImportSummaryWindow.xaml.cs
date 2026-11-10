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

        HeadlineText.Text = result.Cancelled ? Strings.ImportCancelled
            : result.Failures.Count > 0 ? Strings.ImportFinishedWithErrors
            : result.Warnings.Count > 0 || info.LogError is not null ? Strings.ImportFinishedWithWarnings
            : Ui.ImportFinished;
        Title = HeadlineText.Text;

        var details = L.F(Strings.DialogImported, Format.Files(result.Imported.Count));
        var backups = result.Imported.Count(i => i.BackupPath is not null);
        if (backups > 0) details += L.F(Strings.SecondCopies, backups);
        if (result.Failures.Count > 0) details += L.F(Strings.DialogErrors, result.Failures.Count);
        if (result.Cancelled) details += Strings.TheRemainingFilesWereNotTouched;
        DetailsText.Text = details;

        var warnings = result.Warnings.ToList();
        if (info.LogError is not null) warnings.Add(L.F(Strings.TheImportLogCouldNotBeWritten, info.LogError));
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
