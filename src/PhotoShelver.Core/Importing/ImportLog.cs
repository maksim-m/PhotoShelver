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

    private ImportLog(string path) => Path = path;

    public string Path { get; }

    /// <summary>First problem writing the log (the log may be incomplete), or null.</summary>
    public string? Error { get; private set; }

    public static ImportLog Start(string directory, ImportMode mode, string source, string target, string template,
        IReadOnlyCollection<ImportJob> jobs, string appVersion, DateTime? now = null)
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
            var failed = new ImportLog(System.IO.Path.Combine(directory, "import.log"));
            failed.Error = ex.Message;
            return failed;
        }

        var log = new ImportLog(path);
        log.Append(
            $"PhotoShelver {appVersion} — {L.T("import log", "журнал импорта")}{Environment.NewLine}" +
            $"{L.T("Started: ", "Начало:  ")} {started:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}" +
            $"{L.T("Mode:    ", "Режим:   ")} {(mode == ImportMode.Move ? L.T("move", "перемещение") : L.T("copy", "копирование"))}{Environment.NewLine}" +
            $"{L.T("Source:  ", "Источник:")} {source}{Environment.NewLine}" +
            $"{L.T("Target:  ", "Цель:    ")} {target}{Environment.NewLine}" +
            $"{L.T("Template:", "Шаблон:  ")} {template}{Environment.NewLine}" +
            $"{L.T("Files:   ", "Файлов:  ")} {jobs.Count} ({Size(jobs.Sum(j => j.Size))}){Environment.NewLine}{Environment.NewLine}");
        return log;
    }

    public void Record(ImportFileOutcome outcome)
    {
        var source = outcome.Job.SourcePath;
        string line;
        if (outcome.Imported is { } imported && outcome.Problem is null)
            line = $"[OK]       {source} → {imported.DestinationPath}  ({Size(outcome.Job.Size)}{HashText(imported)})";
        else if (outcome.Imported is { } kept)
            line = L.T($"[KEPT]     {source} → {kept.DestinationPath}  ({Size(outcome.Job.Size)}{HashText(kept)}) — source not deleted: {outcome.Problem}",
                $"[ОСТАВЛЕН] {source} → {kept.DestinationPath}  ({Size(outcome.Job.Size)}{HashText(kept)}) — исходник не удалён: {outcome.Problem}");
        else
            line = L.T($"[ERROR]    {source} — {outcome.Problem}", $"[ОШИБКА]   {source} — {outcome.Problem}");
        Append(line + Environment.NewLine);
    }

    public void Finish(ImportResult result, DateTime? now = null)
    {
        var kept = result.Imported.Count(i => result.Failures.Any(f => f.SourcePath == i.SourcePath));
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine($"{L.T("Finished:", "Конец:   ")} {now ?? DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.Append($"{L.T("Result:  ", "Итог:    ")} {L.T("imported", "импортировано")} {result.Imported.Count}");
        if (kept > 0) sb.Append(L.T($" (source kept for {kept})", $" (из них исходник оставлен: {kept})"));
        sb.Append(L.T($", errors {result.Failures.Count - kept}", $", ошибок {result.Failures.Count - kept}"));
        sb.AppendLine(result.Cancelled ? L.T(", import cancelled — the remaining files were not touched", ", импорт отменён — оставшиеся файлы не тронуты") : "");
        foreach (var warning in result.Warnings) sb.AppendLine($"{L.T("Warning", "Предупреждение")}: {warning}");
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
        file.Hash is { } hash ? $", xxh128 {hash:x32}" : L.T(", moved by renaming on the same drive", ", перемещён переименованием на том же диске");

    private static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.0", L.Culture) + L.T(" GB", " ГБ"),
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0.0", L.Culture) + L.T(" MB", " МБ"),
        >= 1L << 10 => (bytes / (double)(1L << 10)).ToString("0", L.Culture) + L.T(" KB", " КБ"),
        _ => bytes + L.T(" B", " Б"),
    };
}
