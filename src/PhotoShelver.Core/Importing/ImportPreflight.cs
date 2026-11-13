using PhotoShelver.Core.Scanning;

namespace PhotoShelver.Core.Importing;

/// <summary>
/// The chosen folders as found on disk at one moment. Reading this can take seconds (a sleeping hard disk, a network
/// share that is gone), so the app reads it off the UI thread (<see cref="ImportPreflight.ReadFolders"/>) and judges
/// by the latest reading; the import itself reads it again (<see cref="ImportPreflight.Check"/>).
/// </summary>
public sealed record FolderState
{
    // The folders this reading is for (as typed or picked; the second copy's folder also when it is off).
    public string? Source { get; init; }
    public string? Target { get; init; }
    public bool BackupEnabled { get; init; }
    public string? Backup { get; init; }

    public bool SourceExists { get; init; }
    public bool TargetExists { get; init; }
    public bool TargetReadOnly { get; init; }

    /// <summary>Source and target are the same folder or one contains the other (also through another name).</summary>
    public bool SourceTargetNested { get; init; }

    public bool BackupExists { get; init; }
    public bool BackupReadOnly { get; init; }
    public bool BackupNestedWithSource { get; init; }
    public bool BackupNestedWithTarget { get; init; }
    public bool BackupOnTargetVolume { get; init; }
    public bool BackupOnSourceVolume { get; init; }

    /// <summary>Whether this reading is for these folders (otherwise it is outdated: the user chose others since).</summary>
    public bool IsFor(string? source, string? target, bool backupEnabled, string? backup) =>
        Source == source && Target == target && BackupEnabled == backupEnabled && Backup == backup;

    /// <summary>Why no import can run with these folders (shown to the user), or null.</summary>
    public string? BlockingReason =>
        Chosen(Source) && !SourceExists ? Strings.SourceFolderNotFound :
        Chosen(Target) && !TargetExists ? Strings.TargetFolderNotFound :
        TargetReadOnly ? Strings.TheTargetFolderIsOnAWrite :
        SourceTargetNested ? Strings.TheSourceAndTheTargetMustNot :
        BackupProblem;

    /// <summary>Why the import cannot run with the chosen second copy, or null (also null when it is off).</summary>
    public string? BackupProblem =>
        !BackupEnabled ? null :
        !Chosen(Backup) ? Strings.ChooseAFolderForTheSecondCopy :
        !BackupExists ? Strings.BackupFolderNotFound :
        BackupReadOnly ? Strings.BackupFolderWriteProtected :
        BackupNestedWithSource ? Strings.BackupInsideSource :
        BackupNestedWithTarget ? Strings.BackupInsideTarget :
        null;

    /// <summary>A second copy that works but protects less (on the drive of the library or of the source), or null.</summary>
    public string? BackupWarning =>
        !BackupEnabled || BackupProblem is not null ? null :
        BackupOnTargetVolume ? Strings.BackupOnLibraryDrive :
        BackupOnSourceVolume ? Strings.BackupOnSourceDrive :
        null;

    /// <summary>Source and target are there and nothing blocks an import.</summary>
    public bool IsReady => SourceExists && TargetExists && BlockingReason is null;

    internal static bool Chosen(string? path) => !string.IsNullOrWhiteSpace(path);
}

/// <summary>What the user asked to import, as it was at the click.</summary>
public sealed record ImportRequest(
    IReadOnlyList<SourceFile> Files,
    string Source,
    string Target,
    ImportMode Mode,
    FolderTemplate Template,
    FileNameTemplate Names,
    bool BackupEnabled,
    string? BackupPath,
    bool BackupMirror,
    DateTime Started);

/// <param name="Folders">The folders as read for this check.</param>
/// <param name="Problem">Why the import cannot start (shown to the user), or null when it can.</param>
/// <param name="OfferCopy">The problem goes away by copying instead of moving (a write-protected card).</param>
/// <param name="Jobs">The import's jobs when it can start (may be empty: nothing to import).</param>
/// <param name="Backup">Where the second copies go, or null.</param>
public sealed record PreflightResult(
    FolderState Folders,
    string? Problem,
    bool OfferCopy = false,
    IReadOnlyList<ImportJob>? Jobs = null,
    BackupPlan? Backup = null);

/// <summary>
/// Everything checked before an import starts: the folders (existing, writable, not inside each other), the second
/// copy, a write-protected source for a move, the free space. Touches the disks: never call it on the UI thread.
/// </summary>
public sealed class ImportPreflight
{
    /// <summary>Test hook: free space of the drive holding a path (null: unknown, e.g. a network share).</summary>
    internal Func<string, long?> FreeSpace { get; init; } = PathUtil.AvailableFreeSpace;

    /// <summary>Test hook: whether the volume holding a path is write-protected.</summary>
    internal Func<string, bool> IsReadOnlyVolume { get; init; } = PathUtil.IsReadOnlyVolume;

    /// <summary>Test hook: whether two paths are on one volume (a move between them is a rename and needs no space).</summary>
    internal Func<string, string, bool> IsSameVolume { get; init; } = PathUtil.IsSameVolume;

    /// <summary>Reads the state of the chosen folders from the disks. Never throws.</summary>
    public FolderState ReadFolders(string? source, string? target, bool backupEnabled, string? backup)
    {
        var sourceExists = Exists(source);
        var targetExists = Exists(target);
        var backupChosen = backupEnabled && FolderState.Chosen(backup);
        var backupExists = backupChosen && Exists(backup);
        return new FolderState
        {
            Source = source,
            Target = target,
            BackupEnabled = backupEnabled,
            Backup = backup,
            SourceExists = sourceExists,
            TargetExists = targetExists,
            TargetReadOnly = targetExists && Safe(() => IsReadOnlyVolume(target!), false),
            SourceTargetNested = FolderState.Chosen(source) && FolderState.Chosen(target) && Nested(source!, target!),
            BackupExists = backupExists,
            BackupReadOnly = backupExists && Safe(() => IsReadOnlyVolume(backup!), false),
            BackupNestedWithSource = backupChosen && FolderState.Chosen(source) && Nested(backup!, source!),
            BackupNestedWithTarget = backupChosen && FolderState.Chosen(target) && Nested(backup!, target!),
            BackupOnTargetVolume = backupExists && targetExists && Safe(() => IsSameVolume(backup!, target!), false),
            BackupOnSourceVolume = backupExists && sourceExists && Safe(() => IsSameVolume(backup!, source!), false),
        };
    }

    /// <summary>
    /// The full check at the click, with the folders read again (the app's reading can be seconds old), and the
    /// import's jobs when it can start.
    /// </summary>
    public PreflightResult Check(ImportRequest request)
    {
        var folders = ReadFolders(request.Source, request.Target, request.BackupEnabled, request.BackupPath);
        PreflightResult Stop(string problem, bool offerCopy = false) => new(folders, problem, offerCopy);

        if (folders.BlockingReason is { } reason) return Stop(reason);
        if (!folders.SourceExists) return Stop(Strings.SourceFolderNotFound);
        if (!folders.TargetExists) return Stop(Strings.TargetFolderNotFound);

        BackupPlan? backup = null;
        List<ImportJob> jobs;
        try
        {
            // BackupProblem (above) guarantees a chosen, existing folder when the second copy is on.
            if (request.BackupEnabled)
                backup = request.BackupMirror
                    ? new BackupPlan(request.BackupPath!, BackupLayout.Mirror, request.Source)
                    : new BackupPlan(request.BackupPath!, BackupLayout.PerImport, request.Source,
                        ImportPlan.NewSessionFolder(request.BackupPath!, request.Started));
            jobs = ImportPlan.CreateJobs(request.Files, request.Target, request.Template, request.Names, backup);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            return Stop(L.F(Strings.CouldNotPrepareTheImport, ex.Message));
        }
        if (jobs.Count == 0) return new PreflightResult(folders, null, Jobs: jobs, Backup: backup);

        // Only a whole write-protected volume blocks a move; single protected files are copied and kept (see Importer).
        if (request.Mode == ImportMode.Move && Safe(() => IsReadOnlyVolume(jobs[0].SourcePath), false))
            return Stop(Strings.TheSourceDriveIsWriteProtectedE, offerCopy: true);

        // A move within one volume is a rename and takes no space; a mirror may already hold some files: counted
        // all the same (better a false "not enough space" than a disk filled up halfway).
        var needed = jobs.Where(j => request.Mode == ImportMode.Copy || !Safe(() => IsSameVolume(j.SourcePath, request.Target), false))
            .Sum(j => j.Size);
        var backupNeeded = backup is null ? 0 : jobs.Sum(j => j.Size);
        if (backup is not null && Safe(() => IsSameVolume(backup.Root, request.Target), false))
        {
            needed += backupNeeded;
            backupNeeded = 0;
        }
        if (FreeSpace(request.Target) is { } free && needed > free)
            return Stop(L.F(Strings.NotEnoughSpaceOnTheTargetDrive, L.Bytes(needed), L.Bytes(free)));
        if (backup is not null && backupNeeded > 0 && FreeSpace(backup.Root) is { } backupFree && backupNeeded > backupFree)
            return Stop(L.F(Strings.NotEnoughSpaceForTheSecondCopy, L.Bytes(backupNeeded), L.Bytes(backupFree)));

        return new PreflightResult(folders, null, Jobs: jobs, Backup: backup);
    }

    private static bool Exists(string? path) => FolderState.Chosen(path) && Directory.Exists(path);

    /// <summary>Nested or equal; a path that cannot be resolved counts as nested (the safe answer: it blocks).</summary>
    private static bool Nested(string a, string b) => Safe(() => PathUtil.AreNestedOrEqual(a, b), true);

    private static bool Safe(Func<bool> check, bool onError)
    {
        try
        {
            return check();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return onError;
        }
    }
}
