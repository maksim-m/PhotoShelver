using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PhotoShelver.App.Services;
using PhotoShelver.Core;
using PhotoShelver.Core.Hashing;
using PhotoShelver.Core.Importing;
using PhotoShelver.Core.Metadata;
using PhotoShelver.Core.Planning;
using PhotoShelver.Core.Scanning;

namespace PhotoShelver.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly ThumbnailService _thumbnails;
    private readonly CaptureDateReader _dateReader = new();
    private readonly HashCache _hashCache = new();
    private readonly Importer _importer = new();
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _templateTimer;
    private readonly DispatcherTimer _environmentTimer;
    private (bool Source, bool Target, bool Backup) _foldersExist;
    private readonly bool _initializing;

    private List<MediaItemViewModel> _items = [];
    private FolderTemplate? _template;
    private FileNameTemplate? _nameTemplate;
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
        // A card or disk can be plugged in or removed at any time: notice it without waiting for a click.
        _environmentTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => CheckFolders(), dispatcher) { IsEnabled = false };

        _initializing = true;
        SourcePath = settings.SourcePath;
        TargetPath = settings.TargetPath;
        TemplateText = string.IsNullOrWhiteSpace(settings.Template) ? FolderTemplate.Default : settings.Template;
        IncludeSubfolders = settings.IncludeSubfolders;
        IsMoveMode = settings.MoveMode;
        ShowFullTree = settings.ShowFullTree;
        NameTemplateText = string.IsNullOrWhiteSpace(settings.NameTemplate) ? FileNameTemplate.Default : settings.NameTemplate;
        IsBackupEnabled = settings.BackupEnabled;
        BackupPath = settings.BackupPath;
        IsBackupMirror = settings.BackupMirror;
        ParseTemplate(TemplateText);
        ParseNameTemplate(NameTemplateText);
        _initializing = false;
        UpdateAdvanced();
    }

    /// <summary>Raised after an import finishes.</summary>
    public event Action<ImportFinishedInfo>? ImportFinished;

    public ObservableCollection<GridRow> Rows { get; } = [];

    public ObservableCollection<FolderNodeViewModel> Tree { get; } = [];

    // ---------- Inputs ----------

    [ObservableProperty] private string? _sourcePath;
    [ObservableProperty] private string? _targetPath;
    [ObservableProperty] private string _templateText = FolderTemplate.Default;
    [ObservableProperty] private string _nameTemplateText = FileNameTemplate.Default;
    [ObservableProperty] private bool _includeSubfolders = true;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsCopyMode))] private bool _isMoveMode = true;
    [ObservableProperty] private bool _showFullTree = true;

    public bool IsCopyMode
    {
        get => !IsMoveMode;
        set => IsMoveMode = !value;
    }

    partial void OnSourcePathChanged(string? value)
    {
        if (_initializing) return;
        _collapsedDays.Clear();
        _treeScrollKey = null;
        StartScan();
    }

    partial void OnIncludeSubfoldersChanged(bool value) { if (!_initializing) StartScan(); }

    partial void OnTargetPathChanged(string? value)
    {
        if (_initializing) return;
        Notice = null;
        ResetTreeView();
        _ = RunDuplicateCheckAsync();
        _ = FindLeftoversAsync();
    }

    partial void OnTemplateTextChanged(string value)
    {
        ParseTemplate(value);
        if (_initializing) return;
        // Duplicate marks belong to the old template: the check key no longer matches, so the import is
        // blocked until the new check (debounced below) has run.
        ResetTreeView();
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
            TemplateWarning = template!.Warning;
            TemplateExample = Strings.Example + template.Format(DateTime.Now);
        }
        else
        {
            _template = null;
            TemplateError = error;
            TemplateWarning = null;
            TemplateExample = null;
        }
    }

    [ObservableProperty] private string? _templateWarning;

    // ---------- File name template ("Advanced") ----------

    /// <summary>Original name shown in the examples of the file name template.</summary>
    private const string ExampleFileName = "IMG_2041.JPG";

    partial void OnNameTemplateTextChanged(string value)
    {
        ParseNameTemplate(value);
        if (!_initializing) UpdateState();
    }

    private void ParseNameTemplate(string value)
    {
        if (FileNameTemplate.TryParse(value, out var names, out var error))
        {
            _nameTemplate = names;
            NameTemplateError = null;
            NameTemplateWarning = names!.Warning;
            NameTemplateExample = Strings.Example + names.Format(DateTime.Now, ExampleFileName);
        }
        else
        {
            _nameTemplate = null;
            NameTemplateError = error;
            NameTemplateWarning = null;
            NameTemplateExample = null;
        }
    }

    // ---------- Second copy ("Advanced") ----------

    [ObservableProperty] private bool _isBackupEnabled;
    [ObservableProperty] private string? _backupPath;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsBackupPerImport))] private bool _isBackupMirror;

    public bool IsBackupPerImport
    {
        get => !IsBackupMirror;
        set => IsBackupMirror = !value;
    }

    partial void OnIsBackupEnabledChanged(bool value) => OnBackupChanged();
    partial void OnBackupPathChanged(string? value) => OnBackupChanged();
    partial void OnIsBackupMirrorChanged(bool value) => OnBackupChanged();

    private void OnBackupChanged()
    {
        if (_initializing) return;
        Notice = null;
        UpdateState();
        _ = FindLeftoversAsync();
    }

    [ObservableProperty] private string? _backupExample;
    [ObservableProperty] private string? _backupWarning;

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void BrowseBackup()
    {
        var path = PickFolder(Strings.FolderForTheSecondCopy, BackupPath);
        if (path is not null) BackupPath = path;
    }

    /// <summary>Why the import cannot run with the chosen second copy, or null (also null when it is off).</summary>
    private string? BackupProblem()
    {
        if (!IsBackupEnabled) return null;
        if (string.IsNullOrWhiteSpace(BackupPath))
            return Strings.ChooseAFolderForTheSecondCopy;
        if (!Directory.Exists(BackupPath))
            return Strings.BackupFolderNotFound;
        if (PathUtil.IsReadOnlyVolume(BackupPath))
            return Strings.BackupFolderWriteProtected;
        if (!string.IsNullOrWhiteSpace(SourcePath) && PathUtil.AreNestedOrEqual(BackupPath, SourcePath))
            return Strings.BackupInsideSource;
        if (!string.IsNullOrWhiteSpace(TargetPath) && PathUtil.AreNestedOrEqual(BackupPath, TargetPath))
            return Strings.BackupInsideTarget;
        return null;
    }

    /// <summary>The second copy as planned for an import starting now (null when it is off).</summary>
    private BackupPlan? CurrentBackupPlan(DateTime started) =>
        !IsBackupEnabled || BackupPath is null || SourcePath is null ? null
        : IsBackupMirror ? new BackupPlan(BackupPath, BackupLayout.Mirror, SourcePath)
        : new BackupPlan(BackupPath, BackupLayout.PerImport, SourcePath, ImportPlan.NewSessionFolder(BackupPath, started));

    /// <summary>Example, warning and the collapsed header of the "Advanced" section.</summary>
    private void UpdateAdvanced()
    {
        BackupExample = null;
        BackupWarning = null;
        if (IsBackupEnabled && BackupProblem() is null && BackupPath is not null)
        {
            var now = DateTime.Now;
            if (!IsBackupMirror)
                BackupExample = Strings.Example + Path.Combine(BackupPath, now.ToString("yyyy-MM-dd HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture), "…", ExampleFileName);
            else if (_template is not null && _nameTemplate is not null)
                BackupExample = Strings.Example + Path.Combine(BackupPath, _template.Format(now), _nameTemplate.Format(now, ExampleFileName));
            if (Directory.Exists(TargetPath) && PathUtil.IsSameVolume(BackupPath, TargetPath!))
                BackupWarning = Strings.BackupOnLibraryDrive;
            else if (Directory.Exists(SourcePath) && PathUtil.IsSameVolume(BackupPath, SourcePath!))
                BackupWarning = Strings.BackupOnSourceDrive;
        }

        var parts = new List<string>();
        if (_nameTemplate is null) parts.Add(Strings.ErrorInTheFileNameTemplate);
        else if (!_nameTemplate.KeepsOriginalNames) parts.Add(Strings.AdvancedNames + _nameTemplate.Format(DateTime.Now, ExampleFileName));
        if (IsBackupEnabled)
            parts.Add(Strings.AdvancedSecondCopy + (string.IsNullOrWhiteSpace(BackupPath) ? Strings.FolderNotChosen : BackupPath));
        AdvancedSummary = parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    [ObservableProperty] private string? _nameTemplateError;
    [ObservableProperty] private string? _nameTemplateWarning;
    [ObservableProperty] private string? _nameTemplateExample;

    /// <summary>What the collapsed "Advanced" section shows: a non-default setting must stay visible.</summary>
    [ObservableProperty] private string? _advancedSummary;

    [ObservableProperty] private bool _isAdvancedExpanded;

    public IReadOnlyList<TemplatePreset> NameTemplatePresets { get; } = FileNameTemplate.Presets
        .Select(p => new TemplatePreset(p, FileNameTemplate.Parse(p).Format(DateTime.Now, ExampleFileName)))
        .ToList();

    /// <summary>Ready-made templates for the drop-down, with an example for today.</summary>
    public IReadOnlyList<TemplatePreset> TemplatePresets { get; } = FolderTemplate.Presets
        .Select(p => new TemplatePreset(p, FolderTemplate.Parse(p).Format(DateTime.Now)))
        .ToList();

    partial void OnShowFullTreeChanged(bool value)
    {
        if (_initializing) return;
        _treeScrollKey = null; // scroll to the first destination again in the new layout
        ScheduleRefresh();
    }

    partial void OnIsMoveModeChanged(bool value) { if (!_initializing) Notice = null; }

    private void CheckFolders()
    {
        var now = (Directory.Exists(SourcePath), Directory.Exists(TargetPath), Directory.Exists(BackupPath));
        if (now == _foldersExist) return;
        _foldersExist = now;
        ForgetExistingFolders();
        UpdateState();
        ScheduleRefresh();
    }

    public void Initialize()
    {
        _foldersExist = (Directory.Exists(SourcePath), Directory.Exists(TargetPath), Directory.Exists(BackupPath));
        _environmentTimer.Start();
        StartScan();
        _ = FindLeftoversAsync();
    }

    // ---------- Leftovers of interrupted imports ----------

    private List<string> _leftovers = [];
    private CancellationTokenSource? _leftoverCts;

    [ObservableProperty] private string? _leftoversText;

    private async Task FindLeftoversAsync()
    {
        _leftoverCts?.Cancel();
        var cts = _leftoverCts = new CancellationTokenSource();
        // Leftovers can be in the target and in the folder of second copies.
        var roots = new[] { TargetPath, IsBackupEnabled ? BackupPath : null }
            .Where(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)).Select(r => r!).ToList();
        if (roots.Count == 0)
        {
            _leftovers = [];
            LeftoversText = null;
            return;
        }
        try
        {
            var found = await Task.Run(() => roots.SelectMany(r => ImportLeftovers.Find(r, cts.Token)).ToList(), cts.Token);
            if (cts != _leftoverCts) return;
            _leftovers = found.Select(f => f.FullName).ToList();
            LeftoversText = _leftovers.Count == 0 ? null
                : L.F(Strings.TheTargetFolderOrTheSecondCopy, Format.Files(_leftovers.Count));
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException)
        {
        }
    }

    // ---------- Opening files (double click, context menu) ----------

    // Parameter is object: recycled tile containers can briefly bind something else.
    [RelayCommand]
    private void OpenFile(object? parameter) => WithExistingFile(parameter, ShellLauncher.Open);

    [RelayCommand]
    private void ShowInFolder(object? parameter) => WithExistingFile(parameter, ShellLauncher.ShowInFolder);

    private void WithExistingFile(object? parameter, Action<string> action)
    {
        if (parameter is not MediaItemViewModel item) return;
        var path = item.File.Path;
        if (!File.Exists(path))
        {
            Notice = L.F(Strings.FileNotFoundPerhapsAlreadyMoved, path);
            return;
        }
        try
        {
            action(path);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            Notice = L.F(Strings.CouldNotOpen, Path.GetFileName(path), ex.Message);
        }
    }

    [RelayCommand]
    private void ShowLeftovers()
    {
        if (_leftovers.FirstOrDefault() is { } first)
            ShellLauncher.ShowInFolder(first);
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task DeleteLeftoversAsync()
    {
        var answer = System.Windows.MessageBox.Show(
            L.F(Strings.DeleteImportingLeftOverFromAnInterrupted, Format.Files(_leftovers.Count))
            + Environment.NewLine + Environment.NewLine
            + Strings.OnlyFilesNamedLikeNameXxxxxxxxImporting,
            "PhotoShelver", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        if (answer != System.Windows.MessageBoxResult.Yes) return;

        var failed = await Task.Run(() => ImportLeftovers.Delete(_leftovers));
        if (failed.Count > 0)
            Notice = L.F(Strings.CouldNotDelete, Format.Files(failed.Count), failed[0].Error);
        await FindLeftoversAsync();
    }

    public void SaveTo(AppSettings settings)
    {
        settings.SourcePath = SourcePath;
        settings.TargetPath = TargetPath;
        settings.Template = TemplateText;
        settings.NameTemplate = NameTemplateText;
        settings.IncludeSubfolders = IncludeSubfolders;
        settings.MoveMode = IsMoveMode;
        settings.ShowFullTree = ShowFullTree;
        settings.BackupEnabled = IsBackupEnabled;
        settings.BackupPath = BackupPath;
        settings.BackupMirror = IsBackupMirror;
    }

    // ---------- Output state ----------

    [ObservableProperty] private string? _templateError;
    [ObservableProperty] private string? _templateExample;

    [ObservableProperty] private string _summaryText = "";
    [ObservableProperty] private string? _skippedText;
    [ObservableProperty] private string? _sourcePlaceholder = ChooseSourceText;

    private static string ChooseSourceText => Strings.ChooseAFolderWithPhotosAndVideos;
    [ObservableProperty] private string? _treePlaceholder;

    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _isCheckingDuplicates;
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


    public int SelectedCount { get; private set; }

    partial void OnIsScanningChanged(bool value)
    {
        UpdateState();
        ScheduleRefresh(); // tree placeholder depends on the scan state
    }
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
        var path = PickFolder(Strings.SourceFolderImportFrom, SourcePath);
        if (path is null) return;
        if (string.Equals(path, SourcePath, StringComparison.OrdinalIgnoreCase)) StartScan();
        else SourcePath = path;
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void BrowseTarget()
    {
        var path = PickFolder(Strings.TargetFolderImportTo, TargetPath);
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
        ImportStatus = Strings.CancellingTheCurrentFileWillBeFinished;
    }

    private bool CanImport() =>
        !IsBusy && DuplicatesChecked && BlockingReason is null && _template is not null && _nameTemplate is not null && SelectedCount > 0
        && Directory.Exists(SourcePath) && Directory.Exists(TargetPath);

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportAsync()
    {
        Notice = null;
        var target = TargetPath!;
        var template = _template!;
        var names = _nameTemplate!;
        var mode = IsMoveMode ? ImportMode.Move : ImportMode.Copy;
        // Checked again at the click (the folders are otherwise checked every 2 s): never import without a second copy
        // that was asked for.
        if (BackupProblem() is { } backupProblem)
        {
            Notice = backupProblem;
            return;
        }
        BackupPlan? backup;
        List<ImportJob> jobs;
        try
        {
            backup = CurrentBackupPlan(DateTime.Now);
            if (IsBackupEnabled && backup is null)
                throw new InvalidOperationException(Strings.TheSecondCopyIsOnButIts);
            jobs = ImportPlan.CreateJobs(_items.Where(i => i.IsSelected && i.CanBeImported).Select(i => i.File), target, template, names, backup);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
        {
            Notice = L.F(Strings.CouldNotPrepareTheImport, ex.Message);
            return;
        }
        if (jobs.Count == 0) return;

        // Only a whole write-protected volume blocks a move; single protected files are copied and kept (see Importer).
        if (mode == ImportMode.Move && PathUtil.IsReadOnlyVolume(jobs[0].SourcePath))
        {
            Notice = Strings.TheSourceDriveIsWriteProtectedE;
            NoticeHasAction = true;
            return;
        }

        var needed = jobs.Where(j => mode == ImportMode.Copy || !PathUtil.IsSameVolume(j.SourcePath, target)).Sum(j => j.Size);
        var backupNeeded = backup is null ? 0 : jobs.Sum(j => j.Size); // a mirror may already hold some: counted all the same
        if (backup is not null && PathUtil.IsSameVolume(backup.Root, target))
        {
            needed += backupNeeded;
            backupNeeded = 0;
        }
        if (PathUtil.AvailableFreeSpace(target) is { } free && needed > free)
        {
            Notice = L.F(Strings.NotEnoughSpaceOnTheTargetDrive, Format.Bytes(needed), Format.Bytes(free));
            return;
        }
        if (backup is not null && backupNeeded > 0 && PathUtil.AvailableFreeSpace(backup.Root) is { } backupFree && backupNeeded > backupFree)
        {
            Notice = L.F(Strings.NotEnoughSpaceForTheSecondCopy, Format.Bytes(backupNeeded), Format.Bytes(backupFree));
            return;
        }

        var version = typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "?";
        var log = ImportLog.Start(SettingsStore.LogsDirectory, mode, SourcePath!, target, template.Pattern, jobs, version,
            nameTemplate: names.KeepsOriginalNames ? null : names.Pattern, backup: backup);

        _importCts = new CancellationTokenSource();
        var token = _importCts.Token;
        var clock = Stopwatch.StartNew();
        ImportProgress = 0;
        ImportStatus = L.F(Strings.Importing0Of, jobs.Count);
        IsImporting = true;

        var progress = new Progress<ImportProgress>(p =>
        {
            if (token.IsCancellationRequested) return;
            ImportProgress = p.BytesTotal == 0 ? 0 : (double)p.BytesDone / p.BytesTotal;
            var status = L.F(Strings.ImportingOfOf, p.FilesDone, p.FilesTotal, Format.Bytes(p.BytesDone), Format.Bytes(p.BytesTotal));
            if (clock.Elapsed > TimeSpan.FromSeconds(2) && p.BytesDone > 0)
            {
                var remaining = clock.Elapsed * ((double)(p.BytesTotal - p.BytesDone) / p.BytesDone);
                status += L.F(Strings.Left, Format.Duration(remaining));
            }
            ImportStatus = status;
        });

        ImportResult result;
        try
        {
            result = await Task.Run(() => _importer.Run(jobs, mode, progress, token, log.Record));
            log.Finish(result);
        }
        catch (Exception ex)
        {
            // Should not happen (the importer reports per-file errors), but never crash mid-import.
            Notice = L.F(Strings.TheImportStoppedBecauseOfAnUnexpected, ex.Message);
            StartScan();
            return;
        }
        finally
        {
            IsImporting = false;
            ImportStatus = null;
            _importCts.Dispose();
            _importCts = null;
        }

        ForgetExistingFolders(); // the import has created folders
        ImportFinished?.Invoke(new ImportFinishedInfo(result, target, log.Error is null ? log.Path : null, log.Error));
        _ = FindLeftoversAsync();
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
            SourcePlaceholder = ChooseSourceText;
            UpdateState();
            return;
        }
        if (!Directory.Exists(path))
        {
            SourcePlaceholder = Strings.FolderNotFound;
            UpdateState();
            return;
        }

        var token = cts.Token;
        IsScanning = true;
        ScanIndeterminate = true;
        ScanStatus = Strings.LookingForFiles;
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
                ScanStatus = L.F(Strings.ReadingMetadataOf, n, total);
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
            // Show files as selected right away; the duplicate check then unticks duplicates
            // (import stays disabled until it has finished).
            foreach (var item in _items) item.IsSelected = item.CanBeImported;
            SourcePlaceholder = _items.Count == 0 ? Strings.NoPhotosOrVideosInThisFolder : null;
            RebuildRows();

            IsScanning = false;
            await RunDuplicateCheckAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (cts == _scanCts) Notice = L.F(Strings.CouldNotReadTheSourceFolder, ex.Message);
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
        _checkedKey = null;
        _attemptedKey = null;
        _skippedOther = 0;
        Rows.Clear();
        HasGroups = false;
        ScheduleRefresh();
    }

    /// <summary>
    /// What the last successful duplicate check was done for (target folder + template), or null.
    /// The import is allowed only when it matches the current target and template exactly - so a target that
    /// appears later (a disk plugged in) or a changed template always gets a fresh check first.
    /// </summary>
    private string? _checkedKey;

    /// <summary>Key of the last check that was started (prevents re-running a failing check in a loop).</summary>
    private string? _attemptedKey;

    private string CurrentCheckKey() =>
        (Directory.Exists(TargetPath) && !IsNested() ? PathUtil.WithoutTrailingSeparator(TargetPath!) : "")
        + "|" + (_template?.Pattern ?? "");

    private bool DuplicatesChecked => _checkedKey is not null && _checkedKey == CurrentCheckKey();

    private async Task RunDuplicateCheckAsync()
    {
        _duplicateCts?.Cancel();
        var cts = _duplicateCts = new CancellationTokenSource();
        var token = cts.Token;
        var items = _items;
        _checkedKey = null;
        if (items.Count == 0 || IsScanning)
        {
            ScheduleRefresh();
            return;
        }

        var files = items.Select(i => i.File).ToList();
        var target = Directory.Exists(TargetPath) && !IsNested() ? TargetPath : null;
        var template = _template;
        var key = CurrentCheckKey();
        _attemptedKey = key;

        IsCheckingDuplicates = true;
        ScanIndeterminate = false;
        ScanProgress = 0;
        ScanStatus = Strings.CheckingForDuplicates;
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
                item.ApplyDuplicate(result.GetValueOrDefault(item.File));
            _checkedKey = key;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Without a duplicate check we cannot promise not to re-import files: block the import and say why.
            if (cts == _duplicateCts) Notice = L.F(Strings.CouldNotCheckForDuplicatesImportIs, ex.Message);
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
            var collapsed = _collapsedDays.Contains(group.Key);
            var header = new HeaderRow(group.Key, group.Key is { } d ? Format.Date(d) : Strings.CouldNotBeRead, list, collapsed);
            Rows.Add(header);
            if (!collapsed)
                foreach (var row in TileRowsOf(header)) Rows.Add(row);
        }
        HasGroups = Rows.Count > 0;
    }

    private IEnumerable<TileRow> TileRowsOf(HeaderRow header)
    {
        for (var i = 0; i < header.Items.Count; i += _columns)
            yield return new TileRow(header.Items.Skip(i).Take(_columns).ToList());
    }

    /// <summary>Days collapsed by the user; kept across rescans of the same source, reset when the source changes.</summary>
    private readonly HashSet<DateTime?> _collapsedDays = [];

    [ObservableProperty] private bool _hasGroups;

    // Parameter is object on purpose: recycled row containers can briefly bind a TileRow here,
    // and a typed RelayCommand<HeaderRow> would throw from CanExecute.
    [RelayCommand]
    private void ToggleGroup(object? parameter)
    {
        if (parameter is not HeaderRow header) return;
        var index = Rows.IndexOf(header);
        if (index < 0) return;

        if (header.IsCollapsed)
        {
            // Insert in place instead of rebuilding, so the scroll position stays put.
            var at = index + 1;
            foreach (var row in TileRowsOf(header)) Rows.Insert(at++, row);
            _collapsedDays.Remove(header.Day);
        }
        else
        {
            while (index + 1 < Rows.Count && Rows[index + 1] is TileRow) Rows.RemoveAt(index + 1);
            _collapsedDays.Add(header.Day);
        }
        header.IsCollapsed = !header.IsCollapsed;
    }

    [RelayCommand]
    private void CollapseAll()
    {
        foreach (var header in Rows.OfType<HeaderRow>()) _collapsedDays.Add(header.Day);
        RebuildRows();
    }

    [RelayCommand]
    private void ExpandAll()
    {
        _collapsedDays.Clear();
        RebuildRows();
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
                L.F(Strings.Total, items.Count),
                L.F(Strings.New, items.Count(i => i.IsNew)),
            };
            var inTarget = items.Count(i => !i.HasError && i.State == DuplicateState.InTarget);
            var inSource = items.Count(i => !i.HasError && i.State == DuplicateState.DuplicateInSource);
            var errors = items.Count(i => i.HasError);
            if (inTarget > 0) parts.Add(L.F(Strings.AlreadyInTarget, inTarget));
            if (inSource > 0) parts.Add(L.F(Strings.DuplicatesInSource, inSource));
            if (errors > 0) parts.Add(L.F(Strings.SummaryErrors, errors));
            parts.Add(L.F(Strings.SummarySelected, selected.Count) + $" ({Format.Bytes(selected.Sum(i => i.File.Size))})");
            SummaryText = string.Join(" · ", parts.Select(p => p.Replace(' ', '\u00A0')));
        }
        else
        {
            SummaryText = "";
        }
        SkippedText = _skippedOther > 0 ? L.F(Strings.SkippedOfOtherTypes, Format.Files(_skippedOther)) : null;
        foreach (var header in Rows.OfType<HeaderRow>()) header.RefreshCounts();

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
            TreePlaceholder = Strings.ChooseTheFolderToImportTo;
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
        // Building the tree touches the target disk and can be slow while it is busy (e.g. hashing).
        if (Tree.Count == 0 && (destinations.Count > 0 || full)) TreePlaceholder = Strings.BuildingTheFolderTree;
        try
        {
            // The target's existing folders do not change when files are ticked: listed once and kept for a few
            // seconds (a big library on a hard disk or a network share takes long to list).
            var depth = template.Depth;
            var listingKey = $"{PathUtil.WithoutTrailingSeparator(target)}|{depth}";
            var cached = full && _existingFolders is { } listing && listing.Key == listingKey && listing.Age.Elapsed < ExistingFoldersMaxAge
                ? listing.Folders : null;
            var (nodes, existing) = await Task.Run(() =>
            {
                var folders = !full ? null : cached ?? FolderTree.ListExisting(target, depth, cts.Token);
                return (FolderTree.Build(target, destinations, full, depth, cts.Token, folders), folders);
            }, cts.Token);
            if (cts != _treeCts) return;
            if (existing is not null && !ReferenceEquals(existing, cached))
                _existingFolders = (listingKey, existing, Stopwatch.StartNew());

            // Updated in place: ticking files changes counts and labels, not the whole TreeView.
            FolderNodeViewModel.Sync(Tree, nodes, null, _treeExpansion, (path, expanded) => _treeExpansion[path] = expanded);
            var roots = Tree;

            // Nothing will be added: open the most recent top-level folder so the current structure is visible.
            var firstAddition = FirstAddition(roots);
            if (firstAddition is null && roots.Count > 0 && !_treeExpansion.ContainsKey(roots[^1].FullPath))
                roots[^1].SetDefaultExpanded(true);

            // Scroll only when the first destination folder changes, so ticking files does not make the tree jump.
            var scrollKey = firstAddition?.FullPath ?? "";
            if (!string.Equals(scrollKey, _treeScrollKey, StringComparison.OrdinalIgnoreCase))
            {
                _treeScrollKey = scrollKey;
                if (firstAddition is not null) ScrollTreeRequested?.Invoke(firstAddition.PathFromRoot());
            }

            TreePlaceholder = nodes.Count > 0 ? null
                : IsScanning ? Strings.ReadingTheSourceFiles
                : string.IsNullOrWhiteSpace(SourcePath) ? Strings.ChooseASourceToSeeWhereThe
                : !Directory.Exists(SourcePath) ? Strings.SourceFolderNotFound
                : _items.Count == 0 ? Strings.NoPhotosOrVideosInTheSource
                : Strings.NoFilesToImport;
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>User's manual expand/collapse per folder path; reset when the target or template changes.</summary>
    private readonly Dictionary<string, bool> _treeExpansion = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The folder the tree was last scrolled to ("" = none); a change triggers a new scroll.</summary>
    private string? _treeScrollKey;

    /// <summary>Asks the view to scroll the tree so that the last node of the path is near the top.</summary>
    public event Action<IReadOnlyList<FolderNodeViewModel>>? ScrollTreeRequested;

    /// <summary>The target's existing folders as last listed (path|depth, folders, age); see <see cref="RefreshTreeAsync"/>.</summary>
    private (string Key, IReadOnlyList<string> Folders, Stopwatch Age)? _existingFolders;

    private static readonly TimeSpan ExistingFoldersMaxAge = TimeSpan.FromSeconds(10);

    /// <summary>Forget the listing of the target's folders (after an import, or when folders appear or disappear).</summary>
    private void ForgetExistingFolders() => _existingFolders = null;

    /// <summary>
    /// First folder (in display order) that receives files: follow nodes with additions down to the
    /// actual destination folder, stopping at a node the user collapsed.
    /// </summary>
    private static FolderNodeViewModel? FirstAddition(IEnumerable<FolderNodeViewModel> roots)
    {
        var node = roots.FirstOrDefault(n => n.HasCount);
        while (node is { IsExpanded: true } && node.Children.FirstOrDefault(c => c.HasCount) is { } child)
            node = child;
        return node;
    }

    private void ResetTreeView()
    {
        ForgetExistingFolders();
        _treeExpansion.Clear();
        _treeScrollKey = null;
    }

    private bool IsNested() =>
        !string.IsNullOrWhiteSpace(SourcePath) && !string.IsNullOrWhiteSpace(TargetPath)
        && PathUtil.AreNestedOrEqual(SourcePath, TargetPath);

    private void UpdateState()
    {
        UpdateAdvanced();
        BlockingReason =
            !string.IsNullOrWhiteSpace(SourcePath) && !Directory.Exists(SourcePath) ? Strings.SourceFolderNotFound :
            !string.IsNullOrWhiteSpace(TargetPath) && !Directory.Exists(TargetPath) ? Strings.TargetFolderNotFound :
            !string.IsNullOrWhiteSpace(TargetPath) && PathUtil.IsReadOnlyVolume(TargetPath!) ? Strings.TheTargetFolderIsOnAWrite :
            IsNested() ? Strings.TheSourceAndTheTargetMustNot :
            BackupProblem();

        OnPropertyChanged(nameof(IsBusy));
        ImportCommand.NotifyCanExecuteChanged();
        BrowseSourceCommand.NotifyCanExecuteChanged();
        BrowseTargetCommand.NotifyCanExecuteChanged();
        BrowseBackupCommand.NotifyCanExecuteChanged();
        DeleteLeftoversCommand.NotifyCanExecuteChanged();

        // The target or template is no longer the one the duplicates were checked for (e.g. the target disk
        // was plugged in after the check): check again. Not while typing a template (that check is debounced).
        if (_items.Count > 0 && !IsBusy && _template is not null && !_templateTimer.IsEnabled
            && !DuplicatesChecked && CurrentCheckKey() != _attemptedKey)
            _ = RunDuplicateCheckAsync();
    }

    private static string? PickFolder(string title, string? current)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current)) dialog.InitialDirectory = current;
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public void Dispose()
    {
        _environmentTimer.Stop();
        _scanCts?.Cancel();
        _duplicateCts?.Cancel();
        _treeCts?.Cancel();
        _importCts?.Cancel();
        _thumbnails.Dispose();
    }
}
