using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PhotoImporter.App.Services;
using PhotoImporter.Core;
using PhotoImporter.Core.Hashing;
using PhotoImporter.Core.Importing;
using PhotoImporter.Core.Metadata;
using PhotoImporter.Core.Planning;
using PhotoImporter.Core.Scanning;

namespace PhotoImporter.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly ThumbnailService _thumbnails;
    private readonly CaptureDateReader _dateReader = new();
    private readonly HashCache _hashCache = new();
    private readonly Importer _importer = new();
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _templateTimer;
    private readonly bool _initializing;

    private List<MediaItemViewModel> _items = [];
    private FolderTemplate? _template;
    private int _skippedOther;
    private int _columns = 5;
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _duplicateCts;
    private CancellationTokenSource? _treeCts;
    private CancellationTokenSource? _importCts;

    public MainViewModel(Dispatcher dispatcher, AppSettings settings)
    {
        _thumbnails = new ThumbnailService(dispatcher);
        _refreshTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (_, _) => { _refreshTimer!.Stop(); Refresh(); }, dispatcher) { IsEnabled = false };
        _templateTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => { _templateTimer!.Stop(); _ = RunDuplicateCheckAsync(); }, dispatcher) { IsEnabled = false };

        _initializing = true;
        SourcePath = settings.SourcePath;
        TargetPath = settings.TargetPath;
        TemplateText = string.IsNullOrWhiteSpace(settings.Template) ? FolderTemplate.Default : settings.Template;
        IncludeSubfolders = settings.IncludeSubfolders;
        IsMoveMode = settings.MoveMode;
        ShowFullTree = settings.ShowFullTree;
        ParseTemplate(TemplateText);
        _initializing = false;
    }

    /// <summary>Raised after an import finishes (result, target folder).</summary>
    public event Action<ImportResult, string>? ImportFinished;

    public ObservableCollection<GridRow> Rows { get; } = [];

    public ObservableCollection<FolderNodeViewModel> Tree { get; } = [];

    // ---------- Inputs ----------

    [ObservableProperty] private string? _sourcePath;
    [ObservableProperty] private string? _targetPath;
    [ObservableProperty] private string _templateText = FolderTemplate.Default;
    [ObservableProperty] private bool _includeSubfolders = true;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsCopyMode))] private bool _isMoveMode = true;
    [ObservableProperty] private bool _showFullTree;

    public bool IsCopyMode
    {
        get => !IsMoveMode;
        set => IsMoveMode = !value;
    }

    partial void OnSourcePathChanged(string? value) { if (!_initializing) StartScan(); }

    partial void OnIncludeSubfoldersChanged(bool value) { if (!_initializing) StartScan(); }

    partial void OnTargetPathChanged(string? value)
    {
        if (_initializing) return;
        Notice = null;
        _ = RunDuplicateCheckAsync();
    }

    partial void OnTemplateTextChanged(string value)
    {
        ParseTemplate(value);
        if (_initializing) return;
        _templateTimer.Stop();
        _templateTimer.Start();
        UpdateState();
    }

    private void ParseTemplate(string value)
    {
        if (FolderTemplate.TryParse(value, out var template, out var error))
        {
            _template = template;
            TemplateError = null;
            TemplateExample = "Пример: " + template!.Format(DateTime.Now);
        }
        else
        {
            _template = null;
            TemplateError = error;
            TemplateExample = null;
        }
    }

    partial void OnShowFullTreeChanged(bool value) { if (!_initializing) ScheduleRefresh(); }

    partial void OnIsMoveModeChanged(bool value) { if (!_initializing) Notice = null; }

    public void Initialize() => StartScan();

    public void SaveTo(AppSettings settings)
    {
        settings.SourcePath = SourcePath;
        settings.TargetPath = TargetPath;
        settings.Template = TemplateText;
        settings.IncludeSubfolders = IncludeSubfolders;
        settings.MoveMode = IsMoveMode;
        settings.ShowFullTree = ShowFullTree;
    }

    // ---------- Output state ----------

    [ObservableProperty] private string? _templateError;
    [ObservableProperty] private string? _templateExample;

    [ObservableProperty] private string _summaryText = "";
    [ObservableProperty] private string? _skippedText;
    [ObservableProperty] private string? _sourcePlaceholder = "Выберите папку с фото и видео";
    [ObservableProperty] private string? _treePlaceholder;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowScanProgress))] private bool _isScanning;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowScanProgress))] private bool _isCheckingDuplicates;
    [ObservableProperty] private string? _scanStatus;
    [ObservableProperty] private double _scanProgress;
    [ObservableProperty] private bool _scanIndeterminate;

    [ObservableProperty] private bool _isImporting;
    [ObservableProperty] private string? _importStatus;
    [ObservableProperty] private double _importProgress;

    [ObservableProperty] private string? _notice;
    [ObservableProperty] private bool _noticeHasAction;

    [ObservableProperty] private string? _blockingReason;

    public bool IsBusy => IsScanning || IsCheckingDuplicates || IsImporting;

    public bool IsIdle => !IsImporting;

    public bool ShowScanProgress => IsScanning || IsCheckingDuplicates;

    public int SelectedCount { get; private set; }

    partial void OnIsScanningChanged(bool value) => UpdateState();
    partial void OnIsCheckingDuplicatesChanged(bool value) => UpdateState();
    partial void OnIsImportingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsIdle));
        UpdateState();
    }

    public string? DisplayNotice => Notice ?? BlockingReason;
    partial void OnNoticeChanged(string? value)
    {
        if (value is null) NoticeHasAction = false;
        OnPropertyChanged(nameof(DisplayNotice));
    }
    partial void OnBlockingReasonChanged(string? value) => OnPropertyChanged(nameof(DisplayNotice));

    // ---------- Commands ----------

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void BrowseSource()
    {
        var path = PickFolder("Папка-источник (откуда импортировать)", SourcePath);
        if (path is null) return;
        if (string.Equals(path, SourcePath, StringComparison.OrdinalIgnoreCase)) StartScan();
        else SourcePath = path;
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void BrowseTarget()
    {
        var path = PickFolder("Папка-цель (куда импортировать)", TargetPath);
        if (path is not null) TargetPath = path;
    }

    [RelayCommand]
    private void SelectAllNew()
    {
        foreach (var item in _items) item.IsSelected = item.IsNew;
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var item in _items) item.IsSelected = false;
    }

    [RelayCommand]
    private void SwitchToCopy()
    {
        IsMoveMode = false;
        Notice = null;
    }

    [RelayCommand]
    private void CancelImport()
    {
        _importCts?.Cancel();
        ImportStatus = "Отмена… текущий файл будет завершён";
    }

    private bool CanImport() =>
        !IsBusy && BlockingReason is null && _template is not null && SelectedCount > 0
        && Directory.Exists(SourcePath) && Directory.Exists(TargetPath);

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportAsync()
    {
        Notice = null;
        var target = TargetPath!;
        var template = _template!;
        var mode = IsMoveMode ? ImportMode.Move : ImportMode.Copy;
        var jobs = _items
            .Where(i => i.IsSelected && i.CanBeImported)
            .Select(i => new ImportJob(i.File.Path, Path.Combine(target, template.Format(i.File.Date!.Value.Local)), i.File.Size))
            .ToList();
        if (jobs.Count == 0) return;

        if (mode == ImportMode.Move && !PathUtil.IsWritable(jobs[0].SourcePath))
        {
            Notice = "Источник защищён от записи (например, заблокированная карта памяти) — файлы нельзя переместить.";
            NoticeHasAction = true;
            return;
        }

        var needed = jobs.Where(j => mode == ImportMode.Copy || !PathUtil.IsSameVolume(j.SourcePath, target)).Sum(j => j.Size);
        if (PathUtil.AvailableFreeSpace(target) is { } free && needed > free)
        {
            Notice = $"Недостаточно места на диске цели: нужно {Format.Bytes(needed)}, свободно {Format.Bytes(free)}.";
            return;
        }

        _importCts = new CancellationTokenSource();
        var token = _importCts.Token;
        var clock = Stopwatch.StartNew();
        ImportProgress = 0;
        ImportStatus = $"Импорт: 0 из {jobs.Count}";
        IsImporting = true;

        var progress = new Progress<ImportProgress>(p =>
        {
            if (token.IsCancellationRequested) return;
            ImportProgress = p.BytesTotal == 0 ? 0 : (double)p.BytesDone / p.BytesTotal;
            var status = $"Импорт: {p.FilesDone} из {p.FilesTotal} · {Format.Bytes(p.BytesDone)} из {Format.Bytes(p.BytesTotal)}";
            if (clock.Elapsed > TimeSpan.FromSeconds(2) && p.BytesDone > 0)
            {
                var remaining = clock.Elapsed * ((double)(p.BytesTotal - p.BytesDone) / p.BytesDone);
                status += $" · осталось {Format.Duration(remaining)}";
            }
            ImportStatus = status;
        });

        ImportResult result;
        try
        {
            result = await Task.Run(() => _importer.Run(jobs, mode, progress, token));
        }
        finally
        {
            IsImporting = false;
            ImportStatus = null;
            _importCts.Dispose();
            _importCts = null;
        }

        ImportFinished?.Invoke(result, target);
        StartScan();
    }

    // ---------- Scanning ----------

    private void StartScan()
    {
        _scanCts?.Cancel();
        _duplicateCts?.Cancel();
        var cts = _scanCts = new CancellationTokenSource();
        Notice = null;
        ClearItems();
        _ = ScanAsync(cts);
    }

    private async Task ScanAsync(CancellationTokenSource cts)
    {
        var path = SourcePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            SourcePlaceholder = "Выберите папку с фото и видео";
            UpdateState();
            return;
        }
        if (!Directory.Exists(path))
        {
            SourcePlaceholder = "Папка не найдена";
            UpdateState();
            return;
        }

        var token = cts.Token;
        IsScanning = true;
        ScanIndeterminate = true;
        ScanStatus = "Поиск файлов…";
        SourcePlaceholder = null;
        try
        {
            var recursive = IncludeSubfolders;
            var found = await Task.Run(() => SourceScanner.Enumerate(path, recursive, token), token);
            _skippedOther = found.SkippedOther;

            var total = found.Files.Count;
            ScanIndeterminate = false;
            ScanProgress = 0;
            var progress = new Progress<int>(n =>
            {
                if (token.IsCancellationRequested) return;
                ScanStatus = $"Чтение метаданных: {n} из {total}";
                ScanProgress = (double)n / Math.Max(1, total);
            });
            await SourceScanner.ReadMetadataAsync(found.Files, _dateReader, progress, token);
            token.ThrowIfCancellationRequested();

            _items = found.Files
                .Select(f => new MediaItemViewModel(f, _thumbnails, ScheduleRefresh))
                .OrderBy(i => i.CanBeImported ? 0 : 1)
                .ThenBy(i => i.File.Date?.Local)
                .ThenBy(i => i.FileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            SourcePlaceholder = _items.Count == 0 ? "В этой папке нет фото и видео" : null;
            RebuildRows();

            IsScanning = false;
            await RunDuplicateCheckAsync(forceDefaultSelection: true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            if (cts == _scanCts) Notice = $"Не удалось прочитать папку-источник: {ex.Message}";
        }
        finally
        {
            if (cts == _scanCts) IsScanning = false;
            ScheduleRefresh();
        }
    }

    private void ClearItems()
    {
        _thumbnails.ClearQueue();
        foreach (var item in _items) item.IsAlive = false;
        _items = [];
        _skippedOther = 0;
        Rows.Clear();
        ScheduleRefresh();
    }

    private async Task RunDuplicateCheckAsync(bool forceDefaultSelection = false)
    {
        _duplicateCts?.Cancel();
        var cts = _duplicateCts = new CancellationTokenSource();
        var token = cts.Token;
        var items = _items;
        if (items.Count == 0 || IsScanning)
        {
            ScheduleRefresh();
            return;
        }

        var files = items.Select(i => i.File).ToList();
        var target = Directory.Exists(TargetPath) && !IsNested() ? TargetPath : null;
        var template = _template;

        IsCheckingDuplicates = true;
        ScanIndeterminate = false;
        ScanProgress = 0;
        ScanStatus = "Проверка дубликатов…";
        var progress = new Progress<(int Done, int Total)>(p =>
        {
            if (token.IsCancellationRequested) return;
            ScanProgress = (double)p.Done / Math.Max(1, p.Total);
        });

        try
        {
            var result = await Task.Run(() => DuplicateDetector.Detect(files, target, template, _hashCache, progress, token), token);
            if (cts != _duplicateCts || items != _items) return;
            foreach (var item in items)
                item.ApplyDuplicate(result.GetValueOrDefault(item.File), forceDefaultSelection);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (cts == _duplicateCts) IsCheckingDuplicates = false;
            ScheduleRefresh();
        }
    }

    // ---------- Grid ----------

    public void SetColumns(int columns)
    {
        columns = Math.Max(1, columns);
        if (columns == _columns) return;
        _columns = columns;
        RebuildRows();
    }

    private void RebuildRows()
    {
        Rows.Clear();
        foreach (var group in _items.GroupBy(i => i.CanBeImported ? i.File.Date!.Value.Local.Date : (DateTime?)null))
        {
            var list = group.ToList();
            Rows.Add(new HeaderRow(group.Key is { } d ? Format.Date(d) : "Не удалось прочитать", Format.Files(list.Count)));
            for (var i = 0; i < list.Count; i += _columns)
                Rows.Add(new TileRow(list.GetRange(i, Math.Min(_columns, list.Count - i))));
        }
    }

    // ---------- Counters, tree, state ----------

    private void ScheduleRefresh()
    {
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    private void Refresh()
    {
        var items = _items;
        var selected = items.Where(i => i.IsSelected && i.CanBeImported).ToList();
        SelectedCount = selected.Count;

        if (items.Count > 0)
        {
            var parts = new List<string>
            {
                $"Всего {items.Count}",
                $"Новых {items.Count(i => i.IsNew)}",
            };
            var inTarget = items.Count(i => !i.HasError && i.State == DuplicateState.InTarget);
            var inSource = items.Count(i => !i.HasError && i.State == DuplicateState.DuplicateInSource);
            var errors = items.Count(i => i.HasError);
            if (inTarget > 0) parts.Add($"Уже в цели {inTarget}");
            if (inSource > 0) parts.Add($"Дубликатов в источнике {inSource}");
            if (errors > 0) parts.Add($"Ошибок {errors}");
            parts.Add($"Выбрано {selected.Count} ({Format.Bytes(selected.Sum(i => i.File.Size))})");
            SummaryText = string.Join(" · ", parts.Select(p => p.Replace(' ', ' ')));
        }
        else
        {
            SummaryText = "";
        }
        SkippedText = _skippedOther > 0 ? $"Пропущено {Format.Files(_skippedOther)} других типов" : null;

        UpdateState();
        _ = RefreshTreeAsync(selected);
    }

    private async Task RefreshTreeAsync(List<MediaItemViewModel> selected)
    {
        _treeCts?.Cancel();
        var cts = _treeCts = new CancellationTokenSource();
        var target = TargetPath;
        var template = _template;

        if (string.IsNullOrWhiteSpace(target))
        {
            Tree.Clear();
            TreePlaceholder = "Выберите папку, куда импортировать";
            return;
        }
        if (!Directory.Exists(target) || template is null || IsNested())
        {
            Tree.Clear();
            TreePlaceholder = null;
            return;
        }

        var destinations = selected.Select(i => template.Format(i.File.Date!.Value.Local)).ToList();
        var full = ShowFullTree;
        try
        {
            var nodes = await Task.Run(() => FolderTree.Build(target, destinations, full, template.Depth, cts.Token), cts.Token);
            if (cts != _treeCts) return;
            Tree.Clear();
            foreach (var node in nodes) Tree.Add(new FolderNodeViewModel(node));
            TreePlaceholder = nodes.Count > 0 ? null
                : _items.Count == 0 ? "Выберите источник, чтобы увидеть, куда попадут файлы"
                : "Нет файлов для импорта";
        }
        catch (OperationCanceledException)
        {
        }
    }

    private bool IsNested() =>
        !string.IsNullOrWhiteSpace(SourcePath) && !string.IsNullOrWhiteSpace(TargetPath)
        && PathUtil.AreNestedOrEqual(SourcePath, TargetPath);

    private void UpdateState()
    {
        BlockingReason =
            !string.IsNullOrWhiteSpace(SourcePath) && !Directory.Exists(SourcePath) ? "Папка-источник не найдена" :
            !string.IsNullOrWhiteSpace(TargetPath) && !Directory.Exists(TargetPath) ? "Папка-цель не найдена" :
            IsNested() ? "Источник и цель не должны совпадать или находиться одна внутри другой" :
            null;

        OnPropertyChanged(nameof(IsBusy));
        ImportCommand.NotifyCanExecuteChanged();
        BrowseSourceCommand.NotifyCanExecuteChanged();
        BrowseTargetCommand.NotifyCanExecuteChanged();
    }

    private static string? PickFolder(string title, string? current)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current)) dialog.InitialDirectory = current;
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public void Dispose()
    {
        _scanCts?.Cancel();
        _duplicateCts?.Cancel();
        _treeCts?.Cancel();
        _importCts?.Cancel();
        _thumbnails.Dispose();
    }
}
