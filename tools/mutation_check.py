"""
Mutation check: deliberately breaks safety-critical code and verifies that the test suite notices.

Each mutation replaces one snippet in a source file, runs the tests and restores the file.
A mutation that leaves all tests green is reported as NOT CAUGHT - that is a gap in the tests.

Usage (from anywhere):  python tools/mutation_check.py
Exit code: 0 if every mutation was caught, 1 otherwise.

When code changes so that a snippet below no longer exists, the script stops with an error -
update the snippet together with the code.
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
     "candidate => File.Move(temp, candidate, overwrite: false)",
     "candidate => File.Move(temp, Path.Combine(job.DestinationFolder, fileName), overwrite: true)"),
    ("hash verification disabled", IMP,
     "if (copyHash != sourceHash)",
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
     "if (File.Exists(temp)) File.Delete(temp);",
     ""),
    ("same-folder guard removed", IMP,
     "if (SamePath(source.DirectoryName!, job.DestinationFolder))",
     "if (false)"),
    ("duplicate = same size only", DUP,
     "if (existingHash == hash)",
     "if (true)"),
    ("metadata errors not caught", CDR,
     "catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))",
     "catch (IOException)"),
]


def find_dotnet():
    found = shutil.which("dotnet")
    if found:
        return found
    default = os.path.join(os.environ.get("ProgramFiles", r"C:\Program Files"), "dotnet", "dotnet.exe")
    if os.path.exists(default):
        return default
    sys.exit("dotnet not found: install the .NET SDK or add it to PATH")


def run_tests(dotnet):
    proc = subprocess.run([dotnet, "test", TESTS, "-nologo", "-v", "q"], cwd=REPO,
                          capture_output=True, text=True, encoding="utf-8", errors="replace")
    out = proc.stdout + proc.stderr
    m = re.search(r"Failed:\s+(\d+), Passed:\s+(\d+)", out)
    return (int(m.group(1)), int(m.group(2))) if m else None


def main():
    dotnet = find_dotnet()

    # Sanity: all snippets exist and the unmodified suite is green.
    for name, rel, old, _ in MUTATIONS:
        text = io.open(os.path.join(REPO, rel), encoding="utf-8", newline="").read()
        if text.count(old) != 1:
            sys.exit(f"Snippet for '{name}' found {text.count(old)}x in {rel} (expected exactly 1) - update this script")
    baseline = run_tests(dotnet)
    if baseline is None or baseline[0] != 0:
        sys.exit(f"Baseline test run is not green ({baseline}); fix the tests first")
    print(f"baseline: {baseline[1]} tests passed", flush=True)

    missed = []
    for name, rel, old, new in MUTATIONS:
        path = os.path.join(REPO, rel)
        original = io.open(path, encoding="utf-8", newline="").read()
        try:
            io.open(path, "w", encoding="utf-8", newline="").write(original.replace(old, new, 1))
            result = run_tests(dotnet)
        finally:
            io.open(path, "w", encoding="utf-8", newline="").write(original)  # always restore

        if result is None:
            status, caught = "build failed", True
        else:
            status, caught = f"{result[0]} failed / {result[1]} passed", result[0] > 0
        if not caught:
            missed.append(name)
        print(f"[{'caught' if caught else 'NOT CAUGHT':10}] {name}: {status}", flush=True)

    print(f"\n{len(MUTATIONS) - len(missed)}/{len(MUTATIONS)} mutations caught")
    return 1 if missed else 0


if __name__ == "__main__":
    sys.exit(main())
