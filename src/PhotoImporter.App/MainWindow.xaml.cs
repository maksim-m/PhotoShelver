using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using PhotoImporter.App.Services;
using PhotoImporter.App.ViewModels;
using PhotoImporter.Core.Importing;

namespace PhotoImporter.App;

public partial class MainWindow : Window
{
    private const double TileWidth = 158; // tile 150 + right margin 8
    private const double ScrollBarAllowance = 20;

    private readonly AppSettings _settings;
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        _settings = SettingsStore.Load();
        Width = _settings.WindowWidth;
        Height = _settings.WindowHeight;
        if (_settings.WindowMaximized) WindowState = WindowState.Maximized;

        _vm = new MainViewModel(Dispatcher, _settings);
        _vm.ImportFinished += OnImportFinished;
        DataContext = _vm;
        Loaded += (_, _) => _vm.Initialize();
    }

    private void ThumbGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = ((ItemsControl)sender).ActualWidth - ScrollBarAllowance;
        _vm.SetColumns((int)Math.Floor(width / TileWidth));
    }

    private void OnImportFinished(ImportResult result, string targetFolder)
    {
        new ImportSummaryWindow(result, targetFolder) { Owner = this }.ShowDialog();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_vm.IsImporting)
        {
            e.Cancel = true;
            _vm.Notice = "Идёт импорт. Дождитесь окончания или нажмите «Отмена».";
            return;
        }

        _vm.SaveTo(_settings);
        _settings.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            _settings.WindowWidth = Width;
            _settings.WindowHeight = Height;
        }
        SettingsStore.Save(_settings);
        _vm.Dispose();
        base.OnClosing(e);
    }
}
