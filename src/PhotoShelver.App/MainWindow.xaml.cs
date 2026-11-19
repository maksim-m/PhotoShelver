using PhotoShelver.Core;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using PhotoShelver.App.Services;
using PhotoShelver.App.ViewModels;
using PhotoShelver.Core.Importing;

namespace PhotoShelver.App;

public partial class MainWindow : Window
{
    private const double TileWidth = 158; // tile 150 + right margin 8
    private const double ScrollBarAllowance = 20;

    private readonly AppSettings _settings;
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        DarkTitleBar.Attach(this);
        _settings = SettingsStore.Load();
        // A hand-edited or damaged settings file must not stop the window from opening (WPF throws on NaN or negative sizes).
        Width = SaneSize(_settings.WindowWidth, MinWidth, 1280);
        Height = SaneSize(_settings.WindowHeight, MinHeight, 820);
        if (_settings.WindowMaximized) WindowState = WindowState.Maximized;

        _vm = new MainViewModel(Dispatcher, _settings);
        _vm.ImportFinished += OnImportFinished;
        _vm.ScrollTreeRequested += path =>
            // Wait until the TreeView has generated containers for the new nodes.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => ScrollTreeTo(path));
        DataContext = _vm;
        Loaded += (_, _) => _vm.Initialize();
    }

    internal static double SaneSize(double saved, double min, double fallback) =>
        double.IsFinite(saved) && saved >= min && saved <= 20_000 ? saved : fallback;

    private void ThumbGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = ((ItemsControl)sender).ActualWidth - ScrollBarAllowance;
        _vm.SetColumns((int)Math.Floor(width / TileWidth));
    }

    /// <summary>Scrolls the target tree so that the last node of <paramref name="path"/> is near the top.</summary>
    private void ScrollTreeTo(IReadOnlyList<FolderNodeViewModel> path)
    {
        ItemsControl parent = TargetTree;
        TreeViewItem? item = null;
        foreach (var node in path)
        {
            parent.UpdateLayout();
            item = parent.ItemContainerGenerator.ContainerFromItem(node) as TreeViewItem;
            if (item is null) return; // tree was rebuilt meanwhile
            parent = item;
        }
        if (item is null || FindDescendant<ScrollViewer>(TargetTree) is not { } scroller) return;

        // Position of the item's own row relative to the visible area; keep exactly one full row above it
        // (usually the parent year) as context. The target is a destination folder, i.e. a leaf, so its
        // height is one row.
        var top = item.TransformToVisual(scroller).Transform(new Point(0, 0)).Y;
        var rowHeight = item.IsExpanded && item.HasItems ? 36 : item.ActualHeight;
        scroller.ScrollToVerticalOffset(Math.Max(0, scroller.VerticalOffset + top - rowHeight));
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T found) return found;
            if (FindDescendant<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    private void OnImportFinished(ImportFinishedInfo info)
    {
        new ImportSummaryWindow(info) { Owner = this }.ShowDialog();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // A window that failed while opening (the app is exiting): nothing was loaded, nothing to save.
        if (_vm is null)
        {
            base.OnClosing(e);
            return;
        }
        if (_vm.IsImporting)
        {
            e.Cancel = true;
            _vm.Notice = Strings.ImportInProgressWaitUntilItFinishes;
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
