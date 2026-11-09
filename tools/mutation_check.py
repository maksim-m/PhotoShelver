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
TESTS = os.path.join("tests", "PhotoImporter.Core.Tests")
IMP = os.path.join("src", "PhotoImporter.Core", "Importing", "Importer.cs")
DUP = os.path.join("src", "PhotoImporter.Core", "Scanning", "DuplicateDetector.cs")
CDR = os.path.join("src", "PhotoImporter.Core", "Metadata", "CaptureDateReader.cs")

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
    ("leftover cleanup deletes any file", os.path.join("src", "PhotoImporter.Core", "Importing", "ImportLeftovers.cs"),
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
    ("read-only source file treated as deletable", os.path.join("src", "PhotoImporter.Core", "PathUtil.cs"),
     "if (File.GetAttributes(filePath).HasFlag(FileAttributes.ReadOnly)) return false;",
     ""),
    ("scan follows folder links (junction loops)", os.path.join("src", "PhotoImporter.Core", "PathUtil.cs"),
     "!e.Attributes.HasFlag(FileAttributes.ReparsePoint) || !IsFolderLink(e.ToFullPath())",
     "true"),
    ("drive-root target joins paths without a separator", os.path.join("src", "PhotoImporter.Core", "PathUtil.cs"),
     "return Path.EndsInDirectorySeparator(full) ? full : full + Path.DirectorySeparatorChar;",
     "return Path.TrimEndingDirectorySeparator(full) + Path.DirectorySeparatorChar;"),
    ("files not reported to the import log", IMP,
     "onFileDone?.Invoke(new ImportFileOutcome(",
     "_ = (new ImportFileOutcome("),
    ("metadata errors not caught", CDR,
     "catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))",
     "catch (IOException)"),
]

UI_TESTS = os.path.join("tests", "PhotoImporter.UiTests")
VM = os.path.join("src", "PhotoImporter.App", "ViewModels", "MainViewModel.cs")

# Mutations of the app's view models, checked by the end-to-end UI tests (--ui).
UI_MUTATIONS = [
    ("no automatic duplicate re-check", VM,
     "CurrentCheckKey() != _attemptedKey)\n            _ = RunDuplicateCheckAsync();",
     "CurrentCheckKey() != _attemptedKey)\n            { }"),
    ("plugged-in disk not noticed", VM,
     "_environmentTimer.Start();",
     ""),
    ("selection not reset to 'all new' after the duplicate check", os.path.join("src", "PhotoImporter.App", "ViewModels", "MediaItemViewModel.cs"),
     "IsSelected = IsNew;",
     ""),
    ("day checkbox also ticks duplicates", os.path.join("src", "PhotoImporter.App", "ViewModels", "GridRows.cs"),
     "foreach (var item in Items.Where(i => i.IsNew)) item.IsSelected = true;",
     "foreach (var item in Items) item.IsSelected = item.CanBeImported;"),
    ("leftovers deleted without confirmation", VM,
     "if (answer != System.Windows.MessageBoxResult.Yes) return;",
     ""),
    ("Cancel button does nothing", VM,
     '_importCts?.Cancel();\n        ImportStatus = "Отмена',
     '\n        ImportStatus = "Отмена'),
    ("window can be closed during an import", os.path.join("src", "PhotoImporter.App", "MainWindow.xaml.cs"),
     "if (_vm.IsImporting)",
     "if (false)"),
    ("nested source/target not blocked", VM,
     'IsNested() ? "Источник и цель не должны совпадать или находиться одна внутри другой" :',
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
        env["PHOTOIMPORTER_UI_TESTS"] = "1"
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
