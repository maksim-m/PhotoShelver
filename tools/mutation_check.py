"""
Mutation check: deliberately breaks safety-critical code and verifies that the test suite notices.

Each mutation replaces one snippet in a source file, runs the tests and restores the file.
A mutation that leaves all tests green is reported as NOT CAUGHT - that is a gap in the tests.

Usage (from anywhere):
  python tools/mutation_check.py        core mutations, checked by the core tests
  python tools/mutation_check.py --ui   UI mutations, checked by the UI tests (opens app windows)
Exit code: 0 if every mutation was caught, 1 otherwise.

When code changes so that a snippet below no longer exists, the script stops with an error -
update the snippet together with the code. Snippets use "\n" line endings; files may use CRLF.
"""
import io
import os
import re
import shutil
import subprocess
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TESTS = os.path.join("tests", "PhotoShelver.Core.Tests")
IMP = os.path.join("src", "PhotoShelver.Core", "Importing", "Importer.cs")
DUP = os.path.join("src", "PhotoShelver.Core", "Scanning", "DuplicateDetector.cs")
CDR = os.path.join("src", "PhotoShelver.Core", "Metadata", "CaptureDateReader.cs")
FNT = os.path.join("src", "PhotoShelver.Core", "FileNameTemplate.cs")
SYN = os.path.join("src", "PhotoShelver.Core", "TemplateSyntax.cs")
PLAN = os.path.join("src", "PhotoShelver.Core", "Importing", "ImportPlan.cs")

# (description, file, original snippet, mutated snippet)
MUTATIONS = [
    ("rename may overwrite (same volume)", IMP,
     "candidate => File.Move(job.SourcePath, candidate, overwrite: false)",
     "candidate => File.Move(job.SourcePath, candidate, overwrite: true)"),
    ("temp may overwrite on final rename", IMP,
     "candidate => File.Move(tempPath, candidate, overwrite: false)",
     "candidate => File.Move(tempPath, Path.Combine(job.DestinationFolder, fileName), overwrite: true)"),
    ("copy not verified from disk", IMP,
     "if (copyOnDisk.Hash != copiedHash)",
     "if (false)"),
    ("source not re-read before placing", IMP,
     "if (sourceOnDevice.Hash != copiedHash)",
     "if (false)"),
    ("move deletes source despite weak verification", IMP,
     "if (!sourceReadFromDevice)",
     "if (false)"),
    ("no retry when another program holds the file", IMP,
     "TransientRetry.Run(() => place(candidate), RetryDelays);",
     "place(candidate);"),
    ("leftover cleanup deletes any file", os.path.join("src", "PhotoShelver.Core", "Importing", "ImportLeftovers.cs"),
     "if (!IsLeftover(Path.GetFileName(path)))",
     "if (false)"),
    ("source-changed check disabled", IMP,
     "if (source.Length != sizeBefore || source.LastWriteTimeUtc != writeTimeBefore)",
     "if (false)"),
    ("copy mode deletes source", IMP,
     "if (mode != ImportMode.Move) return;",
     ""),
    ("source deleted after a failure", IMP,
     "failures.Add(new ImportFailure(job.SourcePath, ex.Message));",
     "failures.Add(new ImportFailure(job.SourcePath, ex.Message)); try { File.Delete(job.SourcePath); } catch { }"),
    ("temp file not cleaned up", IMP,
     "TransientRetry.Run(() => File.Delete(tempPath), RetryDelays);",
     ""),
    ("temp deleted even if not ours", IMP,
     "if (ownsTemp && temp is not null)",
     "if (temp is not null)"),
    ("same-folder guard removed", IMP,
     "if (SamePath(source.DirectoryName!, job.DestinationFolder))",
     "if (false)"),
    ("duplicate = same size only", DUP,
     "if (existingHash == hash)",
     "if (true)"),
    ("leftover .importing counted as imported", DUP,
     'if (f.Name.EndsWith(".importing", StringComparison.OrdinalIgnoreCase)) continue;',
     ""),
    ("moved file stays read-only in the library", IMP,
     "ClearReadOnly(moved, warnings);",
     ""),
    ("camera-protected original deleted anyway", IMP,
     "if (source.Attributes.HasFlag(FileAttributes.ReadOnly))",
     "if (false)"),
    ("read-only source file treated as deletable", os.path.join("src", "PhotoShelver.Core", "PathUtil.cs"),
     "if (File.GetAttributes(filePath).HasFlag(FileAttributes.ReadOnly)) return false;",
     ""),
    ("scan follows folder links (junction loops)", os.path.join("src", "PhotoShelver.Core", "PathUtil.cs"),
     "!e.Attributes.HasFlag(FileAttributes.ReparsePoint) || !IsFolderLink(e.ToFullPath())",
     "true"),
    ("drive-root target joins paths without a separator", os.path.join("src", "PhotoShelver.Core", "PathUtil.cs"),
     "return Path.EndsInDirectorySeparator(full) ? full : full + Path.DirectorySeparatorChar;",
     "return Path.TrimEndingDirectorySeparator(full) + Path.DirectorySeparatorChar;"),
    ("files not reported to the import log", IMP,
     "onFileDone?.Invoke(new ImportFileOutcome(",
     "_ = (new ImportFileOutcome("),
    ("Canon video EXIF ignored (UTC + PC zone instead of the camera clock)",
     os.path.join("src", "PhotoShelver.Core", "Metadata", "IsoBmffReader.cs"),
     "state.EmbeddedJpeg ??= ReadCanonThumbnail(s, box);",
     ""),
    ("lossless RAW data taken for a preview", os.path.join("src", "PhotoShelver.Core", "Metadata", "EmbeddedPreview.cs"),
     "if (marker is 0xC0 or 0xC1 or 0xC2)",
     "if (marker is >= 0xC0 and <= 0xC3)"),
    ("RAW preview orientation ignored", os.path.join("src", "PhotoShelver.Core", "Metadata", "EmbeddedPreview.cs"),
     "case 0x0112 when isIfd0: orientation = (int)tiff.Value(e); break;",
     ""),
    ("source deleted before the placed file is committed", os.path.join("src", "PhotoShelver.Core", "Importing", "Importer.cs"),
     "TransientRetry.Run(() => CommitPlaced(dest), RetryDelays);",
     ""),
    ("parallel verification ignores the source re-read", os.path.join("src", "PhotoShelver.Core", "Importing", "Importer.cs"),
     "return (copyHash, sourceRead.GetAwaiter().GetResult());",
     "return (copyHash, copyHash);"),
    ("verification chunk not sector aligned", os.path.join("src", "PhotoShelver.Core", "Hashing", "FileHasher.cs"),
     "const int chunk = BufferSize;",
     "const int chunk = BufferSize - 1;"),
    ("TIFF thumbnail turned twice", os.path.join("src", "PhotoShelver.Core", "Metadata", "ThumbnailOrientation.cs"),
     "return storedLandscape == thumbLandscape ? orientation : 1;",
     "return orientation;"),
    ("TIFF orientation tag not read", os.path.join("src", "PhotoShelver.Core", "Metadata", "EmbeddedPreview.cs"),
     "else if (e.Tag == 0x0112) orientation = tiff.Value(e);",
     ""),
    ("renamed file moved under its original name (same volume)", IMP,
     "return PlaceWithSuffix(job.FileName, job.DestinationFolder,",
     "return PlaceWithSuffix(Path.GetFileName(job.SourcePath), job.DestinationFolder,"),
    ("renamed file copied under its original name", IMP,
     "CopyViaTemp(job.SourcePath, job.DestinationFolder, job.FileName,",
     "CopyViaTemp(job.SourcePath, job.DestinationFolder, Path.GetFileName(job.SourcePath),"),
    ("importer accepts a destination name with a path", IMP,
     "if (!IsPlainFileName(job.FileName))",
     "if (false)"),
    ("file name template drops the extension", FNT,
     "originalFileName : name + extension;",
     "originalFileName : name;"),
    ("generated name keeps spaces at its ends", FNT,
     "var name = sb.ToString().Trim();",
     "var name = sb.ToString();"),
    ("unknown token in braces accepted", SYN,
     "if (!allowed.Contains(name))",
     "if (false)"),
    ("unclosed brace accepted", SYN,
     "if (close < 0 || (nextOpen >= 0 && nextOpen < close))",
     "if (close < 0)"),
    ("importer accepts a device name (NUL.JPG)", IMP,
     "&& !FolderTemplate.ReservedNames.Contains(name.Split('.')[0]);",
     ";"),
    ("file name template accepts folders", FNT,
     "if (pattern.IndexOfAny(['/', '\\\\']) >= 0)",
     "if (false)"),
    ("move goes on although the second copy failed", IMP,
     "if (backupError is not null)\n        {",
     "if (false)\n        {"),
    ("same-drive move renames the source away although the second copy failed", IMP,
     "if (mode == ImportMode.Move && backupError is null && !TreatAsDifferentVolumes",
     "if (mode == ImportMode.Move && !TreatAsDifferentVolumes"),
    ("copies that differ from each other not noticed", IMP,
     "if (backup is not null && backup.Hash != hash)",
     "if (false)"),
    ("second copy not committed before a same-drive move", IMP,
     "// The new second copy is committed to its disk before the source is renamed away.\n                if (backup is { IsNew: true })",
     "// The new second copy is committed to its disk before the source is renamed away.\n                if (false)"),
    ("second copy not committed before the source is deleted", IMP,
     "TransientRetry.Run(() => CommitPlaced(dest), RetryDelays);\n            if (backup is { IsNew: true })",
     "TransientRetry.Run(() => CommitPlaced(dest), RetryDelays);\n            if (false)"),
    ("mirror takes a file of the same size for the second copy", IMP,
     "if (FileHasher.HashFromDisk(candidate.FullName, forceCached: ForceCachedVerification).Hash == sourceHash)",
     "if (true)"),
    ("per-import copy reuses existing files", PLAN,
     "BackupReusesIdentical: backup.Layout == BackupLayout.Mirror",
     "BackupReusesIdentical: true"),
    ("per-import copy loses the card's folders", PLAN,
     'return relative == "." ? session : Inside(session, relative);',
     "return session;"),
    ("per-import copy renamed like the library", PLAN,
     "backup.Layout == BackupLayout.Mirror ? name : f.FileName",
     "name"),
    ("import folder of second copies reused", PLAN,
     "if (!Directory.Exists(folder) && !File.Exists(folder)) return folder;",
     "return folder;"),
    ("mirror takes the source itself for the second copy", IMP,
     ".Where(f => !PathUtil.IsSameFile(f.FullName, sourcePath))",
     ".Where(f => true)"),
    ("same-drive rename without re-reading the source after the second copy", IMP,
     "&& (backup is null || FileHasher.HashFromDisk(job.SourcePath, forceCached: ForceCachedVerification).Hash == backup.Hash))",
     ")"),
    ("temp name not shortened for long names", IMP,
     "if (fileName.Length <= max) return fileName;",
     "return fileName;"),
    ("nesting not seen through junctions or subst drives", os.path.join("src", "PhotoShelver.Core", "PathUtil.cs"),
     "return Nested(Normalize(a), Normalize(b)) || Nested(WithTrailingSeparator(CanonicalPath(a)), WithTrailingSeparator(CanonicalPath(b)));",
     "return Nested(Normalize(a), Normalize(b));"),
    ("hard link not recognised as the same file", os.path.join("src", "PhotoShelver.Core", "PathUtil.cs"),
     "return FileId(a) is { } ida && FileId(b) is { } idb && ida == idb;",
     "return false;"),
    ("metadata errors not caught", CDR,
     "catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))\n        {\n            // Malformed",
     "catch (IOException)\n        {\n            // Malformed"),
]

UI_TESTS = os.path.join("tests", "PhotoShelver.UiTests")
VM = os.path.join("src", "PhotoShelver.App", "ViewModels", "MainViewModel.cs")

# Mutations of the app's view models, checked by the end-to-end UI tests (--ui).
UI_MUTATIONS = [
    ("no automatic duplicate re-check", VM,
     "CurrentCheckKey() != _attemptedKey)\n            _ = RunDuplicateCheckAsync();",
     "CurrentCheckKey() != _attemptedKey)\n            { }"),
    ("plugged-in disk not noticed", VM,
     "_environmentTimer.Start();",
     ""),
    ("selection not reset to 'all new' after the duplicate check", os.path.join("src", "PhotoShelver.App", "ViewModels", "MediaItemViewModel.cs"),
     "IsSelected = IsNew;",
     ""),
    ("day checkbox also ticks duplicates", os.path.join("src", "PhotoShelver.App", "ViewModels", "GridRows.cs"),
     "foreach (var item in Items.Where(i => i.IsNew)) item.IsSelected = true;",
     "foreach (var item in Items) item.IsSelected = item.CanBeImported;"),
    ("leftovers deleted without confirmation", VM,
     "if (answer != System.Windows.MessageBoxResult.Yes) return;",
     ""),
    ("Cancel button does nothing", VM,
     '_importCts?.Cancel();\n        ImportStatus = L.T("Cancelling',
     '\n        ImportStatus = L.T("Cancelling'),
    ("window can be closed during an import", os.path.join("src", "PhotoShelver.App", "MainWindow.xaml.cs"),
     "if (_vm.IsImporting)",
     "if (false)"),
    ("double click does not open the file", os.path.join("src", "PhotoShelver.App", "MainWindow.xaml"),
     'Gesture="LeftDoubleClick"',
     'Gesture="MiddleDoubleClick"'),
    ("'Show in folder' opens the file instead", VM,
     "private void ShowInFolder(object? parameter) => WithExistingFile(parameter, ShellLauncher.ShowInFolder);",
     "private void ShowInFolder(object? parameter) => WithExistingFile(parameter, ShellLauncher.Open);"),
    ("invalid file name template does not block the import", VM,
     "_template is not null && _nameTemplate is not null && SelectedCount > 0",
     "_template is not null && SelectedCount > 0"),
    ("collapsed 'Advanced' hides the renaming", VM,
     "else if (!_nameTemplate.KeepsOriginalNames) parts.Add(",
     "else if (false) parts.Add("),
    ("collapsed 'Advanced' hides the second copy", VM,
     "if (IsBackupEnabled)\n            parts.Add(",
     "if (false)\n            parts.Add("),
    ("second copy problems do not block the import", VM,
     "            BackupProblem();",
     "            null;"),
    ("second copy not made by the import", VM,
     ".Select(i => i.File), target, template, names, backup);",
     ".Select(i => i.File), target, template, names, null);"),
    ("nested source/target not blocked", VM,
     'IsNested() ? L.T("The source and the target must not be the same folder or inside one another", "Источник и цель не должны совпадать или находиться одна внутри другой") :',
     ""),
]

CRLF = "\r\n"
LF = "\n"


def read_raw(path):
    return io.open(path, encoding="utf-8", newline="").read()


def write_raw(path, text):
    io.open(path, "w", encoding="utf-8", newline="").write(text)


def find_dotnet():
    found = shutil.which("dotnet")
    if found:
        return found
    default = os.path.join(os.environ.get("ProgramFiles", r"C:\Program Files"), "dotnet", "dotnet.exe")
    if os.path.exists(default):
        return default
    sys.exit("dotnet not found: install the .NET SDK or add it to PATH")


def run_tests(dotnet, ui=False):
    env = dict(os.environ)
    if ui:
        env["PHOTOSHELVER_UI_TESTS"] = "1"
        # The UI tests start the app's exe: build it (and the tests) first; a build error counts as "caught".
        build = subprocess.run([dotnet, "build", UI_TESTS, "-nologo", "-v", "q"], cwd=REPO, env=env,
                               capture_output=True, text=True, encoding="utf-8", errors="replace")
        if build.returncode != 0:
            return None
        args = [dotnet, "test", UI_TESTS, "--no-build", "-nologo", "-v", "q"]
    else:
        args = [dotnet, "test", TESTS, "-nologo", "-v", "q"]
    proc = subprocess.run(args, cwd=REPO, env=env, capture_output=True, text=True, encoding="utf-8", errors="replace")
    out = proc.stdout + proc.stderr
    m = re.search(r"Failed:\s+(\d+), Passed:\s+(\d+)", out)
    return (int(m.group(1)), int(m.group(2))) if m else None


def main():
    dotnet = find_dotnet()
    ui = "--ui" in sys.argv
    mutations = UI_MUTATIONS if ui else MUTATIONS

    def run():
        return run_tests(dotnet, ui)

    # Sanity: all snippets exist exactly once and the unmodified suite is green.
    for name, rel, old, _ in mutations:
        text = read_raw(os.path.join(REPO, rel)).replace(CRLF, LF)
        if text.count(old) != 1:
            sys.exit(f"Snippet for '{name}' found {text.count(old)}x in {rel} (expected exactly 1) - update this script")
    baseline = run()
    if baseline is None or baseline[0] != 0:
        sys.exit(f"Baseline test run is not green ({baseline}); fix the tests first")
    print(f"baseline: {baseline[1]} tests passed", flush=True)

    missed = []
    for name, rel, old, new in mutations:
        path = os.path.join(REPO, rel)
        original = read_raw(path)
        uses_crlf = CRLF in original
        mutated = original.replace(CRLF, LF).replace(old, new, 1)
        if uses_crlf:
            mutated = mutated.replace(LF, CRLF)
        try:
            write_raw(path, mutated)
            result = run()
        finally:
            write_raw(path, original)  # always restore, byte for byte

        if result is None:
            status, caught = "build failed", True
        else:
            status, caught = f"{result[0]} failed / {result[1]} passed", result[0] > 0
        if not caught:
            missed.append(name)
        print(f"[{'caught' if caught else 'NOT CAUGHT':10}] {name}: {status}", flush=True)

    print(f"\n{len(mutations) - len(missed)}/{len(mutations)} mutations caught")
    return 1 if missed else 0


if __name__ == "__main__":
    sys.exit(main())
