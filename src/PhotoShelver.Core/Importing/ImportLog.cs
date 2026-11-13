using System.Globalization;
using System.Text;

namespace PhotoShelver.Core.Importing;

/// <summary>What happened to one file of an import (reported as soon as the file is done).</summary>
/// <param name="Imported">The placed file, or null when nothing was placed.</param>
/// <param name="Problem">Why it failed, or why the source was kept although the file was imported.</param>
public sealed record ImportFileOutcome(ImportJob Job, ImportedFile? Imported, string? Problem);

/// <summary>
/// Human-readable log of one import, written line by line while the import runs: after a crash or a power cut
/// the log still lists every file that was done (a missing "Итог" line means the import was interrupted).
/// Writing problems never stop the import; they are reported through <see cref="Error"/>.
/// </summary>
public sealed class ImportLog
{
    private static readonly Encoding Utf8WithBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    private readonly ImportMode _mode;

    private ImportLog(string path, ImportMode mode)
    {
        Path = path;
        _mode = mode;
    }

    public string Path { get; }

    /// <summary>First problem writing the log (the log may be incomplete), or null.</summary>
    public string? Error { get; private set; }

    public static ImportLog Start(string directory, ImportMode mode, string source, string target, string template,
        IReadOnlyCollection<ImportJob> jobs, string appVersion, DateTime? now = null, string? nameTemplate = null, BackupPlan? backup = null)
    {
        var started = now ?? DateTime.Now;
        string path;
        try
        {
            Directory.CreateDirectory(directory);
            path = UniquePath(directory, $"import-{started:yyyy-MM-dd_HH-mm-ss}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            var failed = new ImportLog(System.IO.Path.Combine(directory, "import.log"), mode);
            failed.Error = ex.Message;
            return failed;
        }

        var log = new ImportLog(path, mode);
        var backupLine = backup is null ? ""
            : $"{Strings.LogSecondCopyLabel} " + (backup.Layout == BackupLayout.Mirror
                ? backup.Root + Strings.TheSameFoldersAndNamesAsIn
                : (backup.SessionFolder ?? backup.Root) + Strings.AFolderPerImport)
              + Environment.NewLine;
        log.Append(
            $"PhotoShelver {appVersion} — {Strings.LogTitle}{Environment.NewLine}" +
            $"{Strings.LogStarted} {started:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}" +
            $"{Strings.LogMode} {(mode == ImportMode.Move ? Strings.LogModeMove : Strings.LogModeCopy)}{Environment.NewLine}" +
            $"{Strings.LogSource} {source}{Environment.NewLine}" +
            $"{Strings.LogTarget} {target}{Environment.NewLine}" +
            $"{Strings.LogTemplate} {template}{Environment.NewLine}" +
            (nameTemplate is null ? "" : $"{Strings.LogNames} {nameTemplate}{Environment.NewLine}") +
            backupLine +
            $"{Strings.LogFiles} {jobs.Count} ({L.Bytes(jobs.Sum(j => j.Size))}){Environment.NewLine}{Environment.NewLine}");
        return log;
    }

    public void Record(ImportFileOutcome outcome)
    {
        var source = outcome.Job.SourcePath;
        string line;
        if (outcome.Imported is { } imported && outcome.Problem is null)
            line = $"[OK]       {source} → {imported.DestinationPath}  ({L.Bytes(outcome.Job.Size)}{HashText(imported)}){BackupText(imported)}";
        else if (outcome.Imported is { } kept && _mode == ImportMode.Move)
            line = L.F(Strings.LogLineKept, source, kept.DestinationPath, L.Bytes(outcome.Job.Size), HashText(kept), BackupText(kept), outcome.Problem);
        else if (outcome.Imported is { } partial)
            // Copy mode: imported, but something else went wrong (e.g. the second copy).
            line = L.F(Strings.LogLinePartial, source, partial.DestinationPath, L.Bytes(outcome.Job.Size), HashText(partial), BackupText(partial), outcome.Problem);
        else
            line = L.F(Strings.LogLineError, source, outcome.Problem);
        Append(line + Environment.NewLine);
    }

    public void Finish(ImportResult result, DateTime? now = null)
    {
        var kept = result.Imported.Count(i => result.Failures.Any(f => f.SourcePath == i.SourcePath));
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine($"{Strings.LogFinished} {now ?? DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.Append($"{Strings.LogResult} {Strings.LogImported} {result.Imported.Count}");
        if (kept > 0)
            sb.Append(_mode == ImportMode.Move
                ? L.F(Strings.SourceKeptFor, kept)
                : L.F(Strings.WithAProblem, kept));
        sb.Append(L.F(Strings.LogErrors, result.Failures.Count - kept));
        sb.AppendLine(result.Cancelled ? Strings.ImportCancelledTheRemainingFilesWereNot : "");
        foreach (var warning in result.Warnings) sb.AppendLine($"{Strings.Warning}: {warning}");
        Append(sb.ToString());
    }

    private void Append(string text)
    {
        if (Error is not null) return;
        try
        {
            // Opened and closed per line: whatever was written survives a crash of the app.
            File.AppendAllText(Path, text, Utf8WithBom);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = ex.Message;
        }
    }

    private static string UniquePath(string directory, string baseName)
    {
        for (var n = 0; ; n++)
        {
            var path = System.IO.Path.Combine(directory, n == 0 ? baseName + ".log" : $"{baseName}_{n}.log");
            if (!File.Exists(path)) return path;
        }
    }

    private static string HashText(ImportedFile file) =>
        file.Hash is { } hash ? $", xxh128 {hash:x32}" : Strings.MovedByRenamingOnTheSameDrive;

    private static string BackupText(ImportedFile file) =>
        file.BackupPath is { } backup ? L.F(Strings.LogSecondCopy, backup) : "";

}
