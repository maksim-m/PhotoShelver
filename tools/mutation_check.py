"""
Mutation check: deliberately breaks safety-critical code and verifies that the test suite notices.

Each mutation replaces one snippet in a source file, runs the tests and restores the file.
A mutation that leaves all tests green is reported as NOT CAUGHT - that is a gap in the tests.

Usage (from anywhere):
  python tools/mutation_check.py        core mutations, checked by the core tests
  python tools/mutation_check.py --ui   UI mutations, checked by the UI tests (opens app windows)
  python tools/mutation_check.py --only=TIFF --only=stack   only the mutations whose name contains one of these
  python tools/mutation_check.py --changed-since=v1.2.0     only those a change since that commit can affect
  python tools/mutation_check.py --shard=1/4                the second of four parts (parallel runs)
  python tools/mutation_check.py --plan [--changed-since=..] [--shard-size=12]   the parts as JSON (CI matrix)
Exit code: 0 if every mutation was caught, 1 otherwise.

When code changes so that a snippet below no longer exists, the script stops with an error -
update the snippet together with the code. Snippets use "\n" line endings; files may use CRLF.
"""
import base64
import io
import json
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
TIR = os.path.join("src", "PhotoShelver.Core", "Metadata", "TiffIfdReader.cs")
ISO = os.path.join("src", "PhotoShelver.Core", "Metadata", "IsoBmffReader.cs")
EMB = os.path.join("src", "PhotoShelver.Core", "Metadata", "EmbeddedPreview.cs")
LVR = os.path.join("src", "PhotoShelver.Core", "Metadata", "LegacyVideoReader.cs")
FNT = os.path.join("src", "PhotoShelver.Core", "FileNameTemplate.cs")
SYN = os.path.join("src", "PhotoShelver.Core", "TemplateSyntax.cs")
PLAN = os.path.join("src", "PhotoShelver.Core", "Importing", "ImportPlan.cs")
PRE = os.path.join("src", "PhotoShelver.Core", "Importing", "ImportPreflight.cs")

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
    ("an impossible MP4 creation time loses the file's other dates", os.path.join("src", "PhotoShelver.Core", "Metadata", "IsoBmffReader.cs"),
     "if (seconds == 0 || seconds > (ulong)(DateTime.MaxValue - Epoch1904).TotalSeconds) return null;",
     "if (seconds == 0) return null;"),
    ("TIFF metadata read sequentially again (whole file when it comes after the image)", CDR,
     "if (EmbeddedPreview.IsTiff(path) && tiff.Valid)",
     "if (false)"),
    ("metadata read on a normal stack (library stack overflow ends the app)", CDR,
     "    {\n        T result = default!;",
     "    {\n        if (DateTime.Now.Year > 0) return read();\n        T result = default!;"),
    ("BigTIFF given to the library (huge directory on the stack)", CDR,
     "if (tiff.IsBigTiff) return ExifDate(ReadTiffDates(stream));",
     "if (tiff.IsBigTiff && EmbeddedPreview.IsRaw(path)) return ExifDate(ReadTiffDates(stream));"),
    ("TIFF with metadata beyond 2 GB: no own reader after the library fails", CDR,
     "            return ExifDate(ReadTiffDates(stream));\n        }",
     "            return null;\n        }"),
    ("BigTIFF IFD8 pointer not read", TIR,
     "4 or 13 => 4, 16 or 18 => 8,",
     "4 or 13 => 4,"),
    ("TIFF reader may read past the end of the file", TIR,
     "offset < _start || offset > _end - count",
     "offset < _start || offset > _end"),
    ("TIFF read with the capturing (sequential) reader", CDR,
     "new MetadataExtractor.IO.IndexedSeekingReader(stream, 0, isMotorolaByteOrder: false)",
     "new MetadataExtractor.IO.IndexedCapturingReader(stream, 4096, isMotorolaByteOrder: false)"),
    ("videos dated only when named .mp4/.mov (Insta360, GoPro MAX, CRM lose their dates)", CDR,
     "fromMetadata = OnLargeStack(() => kind == MediaKind.Photo ? ReadPhoto(path) : ReadVideo(path));",
     "fromMetadata = OnLargeStack(() => kind == MediaKind.Photo ? ReadPhoto(path) : Path.GetExtension(path).ToLowerInvariant() is \".mp4\" or \".mov\" ? ReadVideo(path) : null);"),
    ("MP4/MOV structure recognised only with 'ftyp' (old QuickTime files lose their dates)", ISO,
     "[\"ftyp\", \"moov\", \"mdat\", \"wide\", \"free\", \"skip\", \"pnot\"]",
     "[\"ftyp\"]"),
    ("TIFF-based RAW of other makers without the embedded preview", EMB,
     "\".nrw\", \".srf\", \".sr2\", \".rwl\", \".pef\",",
     "\".srf\", \".sr2\", \".rwl\","),    ("Canon MOV: camera EXIF in moov/udta ignored", ISO,
     "case \"CNTH\": // Canon MOV",
     "case \"CNTH-\": // Canon MOV"),
    ("Sony XML local time with offset ignored", CDR,
     "if (!sonyIsUtc) return new CaptureDate(sonyLocal, DateSource.Metadata);",
     ""),
    ("GoPro firmware not recognised (mvhd camera clock read as UTC)", ISO,
     "return text.StartsWith(\"HD\", StringComparison.Ordinal)",
     "return false && text.StartsWith(\"HD\", StringComparison.Ordinal)"),
    ("GoPro telemetry box not recognised", ISO,
     "case \"GPMF\": // GoPro telemetry settings (HERO5 and later)\n                    state.GoPro = true;",
     "case \"GPMF\": // GoPro telemetry settings (HERO5 and later)\n                    state.GoPro |= false;"),
    ("AVI IDIT not read", LVR,
     "if (id == \"IDIT\" && size",
     "if (id == \"IDIX\" && size"),
    ("AVI IDIT searched inside the frame data too", LVR,
     "Encoding.ASCII.GetString(h, 8, 4) != \"movi\"",
     "Encoding.ASCII.GetString(h, 8, 4) != \"MOVI\""),
    ("AVCHD emulation bytes not removed", LVR,
     "if (zeros >= 2 && b == 0x03) { zeros = 0; continue; }",
     ""),
    ("unset camera clocks (1970/1980) taken as capture dates", LVR,
     "d.Year >= 1990 &&",
     "d.Year >= 1 &&"),    ("BigTIFF with a non-zero reserved field given to the library (stack overflow)", TIR,
     "IsBigTiff = true;\n            var big = Read(start, 16);\n            if (big is null || U16(big, 4) != 8) return;",
     "var big = Read(start, 16);\n            if (big is null || U16(big, 4) != 8 || U16(big, 6) != 0) return;\n            IsBigTiff = true;"),
    ("AVCHD searched in raw packets (MDPM split over packets lost)", LVR,
     "list.AddRange(data.AsSpan(start, end - start));",
     "list.AddRange(data.AsSpan(p, size));"),
    ("AVI with a wrong weekday loses its date", LVR,
     "if (parts.Count == 5 &&",
     "if (false && parts.Count == 5 &&"),
    ("MP4/MOV starting with another box not recognised", ISO,
     "if (box.Type == \"moov\") return true;",
     "if (box.Type == \"moov\") return false;"),    ("a JPEG named .tif loses its date", CDR,
     "if (EmbeddedPreview.IsTiff(path) && tiff.Valid)",
     "if (EmbeddedPreview.IsTiff(path))"),
    ("metadata errors not caught", CDR,
     "catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))\n        {\n            // Malformed",
     "catch (IOException)\n        {\n            // Malformed"),
    # Checks before an import (ImportPreflight): what the app shows while folders are chosen, and the check at the click.
    ("second copy problems do not block the import", PRE,
     "        BackupProblem;\n",
     "        null;\n"),
    ("second copy not made by the import", PRE,
     "request.Template, request.Names, backup);",
     "request.Template, request.Names, null);"),
    ("nested source/target not blocked", PRE,
     "SourceTargetNested ? Strings.TheSourceAndTheTargetMustNot :\n",
     ""),
    ("unresolvable paths count as not nested", PRE,
     "PathUtil.AreNestedOrEqual(a, b), true);",
     "PathUtil.AreNestedOrEqual(a, b), false);"),
    ("folders not read again at the click", PRE,
     "var folders = ReadFolders(request.Source, request.Target, request.BackupEnabled, request.BackupPath);",
     "var folders = new FolderState { Source = request.Source, Target = request.Target, BackupEnabled = request.BackupEnabled,"
     " Backup = request.BackupPath, SourceExists = true, TargetExists = true, BackupExists = true };"),
    ("move from a write-protected card not stopped", PRE,
     "if (request.Mode == ImportMode.Move && Safe(",
     "if (false && Safe("),
    ("free space in the library not checked", PRE,
     "is { } free && needed > free)",
     "is { } free && needed > long.MaxValue)"),
    ("second copy on the library drive not counted there", PRE,
     "needed += backupNeeded;\n            backupNeeded = 0;",
     ""),
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
     '_importCts?.Cancel();\n        ImportStatus = Strings.CancellingTheCurrentFileWillBeFinished;',
     '\n        ImportStatus = Strings.CancellingTheCurrentFileWillBeFinished;'),
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
    ("window size from the settings file not checked", os.path.join("src", "PhotoShelver.App", "MainWindow.xaml.cs"),
     "Width = SaneSize(_settings.WindowWidth, MinWidth, 1280);",
     "Width = _settings.WindowWidth;"),
    ("tree counts not updated in place", os.path.join("src", "PhotoShelver.App", "ViewModels", "FolderNodeViewModel.cs"),
     "if (changed)",
     "if (false)"),
    ("a folder the user opened closes when the tree updates", os.path.join("src", "PhotoShelver.App", "ViewModels", "FolderNodeViewModel.cs"),
     "SetDefaultExpanded(expansionOverrides.TryGetValue(FullPath, out var manual) ? manual : node.Count > 0);",
     "SetDefaultExpanded(node.Count > 0);"),
    ("a new reading of the folders is ignored", VM,
     "        _folders = state;\n",
     ""),
    ("kept folder listing not renewed after an import", VM,
     "ForgetExistingFolders(); // the import has created folders",
     "// the import has created folders"),
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
        args = [dotnet, "test", UI_TESTS, "--no-build", "-nologo", "-v", "q", "--blame-hang-timeout", "5m"]
    else:
        args = [dotnet, "test", TESTS, "-nologo", "-v", "q", "--blame-hang-timeout", "5m"]
    proc = subprocess.run(args, cwd=REPO, env=env, capture_output=True, text=True, encoding="utf-8", errors="replace")
    out = proc.stdout + proc.stderr
    m = re.search(r"Failed:\s+(\d+), Passed:\s+(\d+)", out)
    if not m:
        return None
    # A run stopped by --blame-hang-timeout (a test hung) reports only part of the tests and fails as a whole.
    aborted = proc.returncode != 0 and int(m.group(1)) == 0
    return int(m.group(1)), int(m.group(2)), aborted


MARKER = os.path.join(REPO, "tools", ".mutation-in-progress.json")


def restore_interrupted_run():
    """A previous run was stopped while a mutation was applied (closed window, killed process): undo it first."""
    if not os.path.exists(MARKER):
        return
    with open(MARKER, encoding="utf-8") as f:
        saved = json.load(f)
    write_raw(os.path.join(REPO, saved["path"]), base64.b64decode(saved["original"]).decode("utf-8"))
    os.remove(MARKER)
    print(f"restored {saved['path']} from an interrupted run", flush=True)


def option(name):
    """The value of --name=value (the last one given), or None."""
    values = [a[len(name) + 3:] for a in sys.argv if a.startswith(f"--{name}=")]
    return values[-1] if values else None


def changed_files(since):
    """Files changed between the commit `since` and the working tree (repo-relative, with "/")."""
    proc = subprocess.run(["git", "diff", "--name-only", since], cwd=REPO, capture_output=True, text=True)
    if proc.returncode != 0:
        sys.exit(f"git diff {since} failed: {proc.stderr.strip()}")
    return {line.strip() for line in proc.stdout.splitlines() if line.strip()}


def select(ui, changed):
    """
    The mutations of one suite that a change can affect (all of them when `changed` is None).
    A mutation is affected when its own file changed. Every mutation of the suite is, when its tests (or their test
    files) changed, or this script: a test that no longer catches a mutation can be anywhere in the suite.
    """
    mutations = UI_MUTATIONS if ui else MUTATIONS
    if changed is None:
        return list(mutations)
    tests = (UI_TESTS if ui else TESTS).replace("\\", "/") + "/"
    if "tools/mutation_check.py" in changed or any(f.startswith(tests) for f in changed):
        return list(mutations)
    return [m for m in mutations if m[1].replace("\\", "/") in changed]


def shard_of(mutations, shard):
    """Part i of n ("i/n", from 0): every n-th mutation, so that slow and fast ones spread evenly."""
    i, n = (int(x) for x in shard.split("/"))
    if not 0 <= i < n:
        sys.exit(f"--shard={shard}: expected i/n with 0 <= i < n")
    return mutations[i::n]


def only_filter(mutations, only):
    """The mutations whose name contains one of the --only texts (all when none are given)."""
    return [m for m in mutations if any(o.lower() in m[0].lower() for o in only)] if only else mutations


def plan(changed, shard_size, only):
    """
    Prints the parts for parallel runs as JSON, e.g. {"count": 23, "include": [{"suite": "core", "shard": "0/2"}, ...]}
    (the CI matrix). Needs neither .NET nor a build.
    """
    include = []
    count = 0
    for suite, ui in (("core", False), ("ui", True)):
        selected = only_filter(select(ui, changed), only)
        count += len(selected)
        parts = (len(selected) + shard_size - 1) // shard_size
        include += [{"suite": suite, "shard": f"{i}/{parts}"} for i in range(parts)]
    print(json.dumps({"count": count, "include": include}))


def main():
    since = option("changed-since")
    changed = changed_files(since) if since else None
    only = [a[len("--only="):] for a in sys.argv if a.startswith("--only=")]
    if "--plan" in sys.argv:
        plan(changed, int(option("shard-size") or "12"), only)
        return 0

    restore_interrupted_run()
    ui = "--ui" in sys.argv
    mutations = only_filter(select(ui, changed), only)
    if only and not mutations and not since:
        sys.exit(f"No mutation matches {only}")
    if option("shard"):
        mutations = shard_of(mutations, option("shard"))
    if not mutations:
        print("No mutation to check" + (f" (nothing they cover changed since {since})" if since else ""))
        return 0
    dotnet = find_dotnet()

    def run():
        return run_tests(dotnet, ui)

    # Sanity: all snippets exist exactly once and the unmodified suite is green.
    for name, rel, old, _ in mutations:
        text = read_raw(os.path.join(REPO, rel)).replace(CRLF, LF)
        if text.count(old) != 1:
            sys.exit(f"Snippet for '{name}' found {text.count(old)}x in {rel} (expected exactly 1) - update this script")
    baseline = run()
    if baseline is None or baseline[0] != 0 or baseline[2]:
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
        with open(MARKER, "w", encoding="utf-8") as f:
            json.dump({"path": rel, "original": base64.b64encode(original.encode("utf-8")).decode("ascii")}, f)
        try:
            write_raw(path, mutated)
            result = run()
        finally:
            write_raw(path, original)  # always restore, byte for byte
            os.remove(MARKER)

        if result is None:
            status, caught = "build failed", True
        elif result[2]:
            status, caught = f"test run aborted (a test hung) after {result[1]} passed", True
        else:
            status, caught = f"{result[0]} failed / {result[1]} passed", result[0] > 0
        if not caught:
            missed.append(name)
        print(f"[{'caught' if caught else 'NOT CAUGHT':10}] {name}: {status}", flush=True)

    print(f"\n{len(mutations) - len(missed)}/{len(mutations)} mutations caught")
    return 1 if missed else 0


if __name__ == "__main__":
    sys.exit(main())
