# PhotoShelver

**Move photos and videos from a memory card or phone into your library, sorted into folders by capture date — without ever losing or overwriting a file.**

A small, focused Windows 11 app in the spirit of Lightroom's Import dialog, without the catalog: your library stays plain folders that any program can read.

![PhotoShelver: the source on the left, the target folder tree on the right](docs/screenshot.png)

## Why

Copying a card by hand in Explorer is tedious and error-prone: you sort files into dated folders yourself, re-import the same shots because the card was never cleared, and Explorer's copy never checks that what landed on the disk is what was on the card. Full photo managers solve this, but pull you into their catalog and their way of working.

PhotoShelver does one job:

1. **Pick the source** (a card, a phone folder, a download folder) — you see every photo and video as a thumbnail, grouped by day.
2. **Pick the target** (your photo library) — you see the folder tree it will create, with the number of files going into every folder and **NEW** on folders that do not exist yet.
3. **Press Import.** Files are moved (or copied) into `2026/2026-08-14`-style folders, verified, and logged.

## Highlights

- **Sorted by the real capture date.** EXIF for photos; for videos the maker's own date where there is one — Apple's and Sony's date with time zone, the camera's clock of Canon (MP4 and MOV), GoPro, AVI cameras and AVCHD camcorders — otherwise the standard UTC time with the GPS position's time zone — so photos and videos from one moment land in the same folder. Files without metadata fall back to the modification date (and are marked). Also TIFF scans of any size (metadata beyond 2 GB, BigTIFF), and a damaged file can never crash the app.
- **Duplicates are recognised by content.** Files that are already in the library (from an earlier import) or appear twice on the card are dimmed and not selected — detected by a 128-bit content hash, not by name.
- **Your folder scheme.** `{YYYY}/{YYYY}-{MM}-{DD}` by default, or any template with `{YYYY}`, `{YY}`, `{MM}`, `{MMM}`, `{DD}` and `/` — text outside the braces is kept as is; mistakes are explained as you type, and an invalid template never lets the import start.
- **Optional second copy.** Under *Advanced*: every imported file is also copied to another folder — a folder per import with the card's own names (like Lightroom), or a mirror of the library. When moving, the card is cleared only after *both* copies are verified.
- **Optional renaming.** Under *Advanced*: a file name template such as `{YYYY}-{MM}-{DD}_{NAME}` → `2026-08-14_IMG_2041.JPG` (date, time `{HH}{mm}{ss}`, original name `{NAME}`). Off by default; when on, it stays visible even with the section collapsed.
- **Camera and phone formats.** JPEG, HEIC/HEIF (also Fujifilm .HIF), AVIF, JPEG XL, WebP, PNG, TIFF; RAW of every major maker (CR2, CR3, CRW, NEF, NRW, ARW, SR2, RAF, ORF, RW2, PEF, SRW, DNG, GPR, 3FR, IIQ and more); video MP4, MOV, 3GP, AVI, AVCHD (MTS/M2TS), MPG/MOD/TOD, MKV, Insta360 (.insv), GoPro MAX (.360), Canon CRM, MXF, BRAW, R3D. Camera proxies (GoPro .LRV/.THM, DJI .LRF) are left alone.
- **RAW-friendly.** RAW thumbnails come from the preview the camera embedded — fast and without installing codecs. TIFF scans are shown upright.
- **Fast on big cards.** Thousands of files are scanned in seconds; thumbnails load for what is on screen first.
- **Native look.** Windows 11 Fluent design, light and dark theme, English and Russian interface (follows the Windows display language).
- **One file, nothing to install.** A self-contained `PhotoShelver.exe`; the .NET runtime is inside.

## Safety first

Your photos are irreplaceable, so the importer is built around a few hard rules:

- **Nothing is ever overwritten.** A name clash gets a suffix: `IMG_0001_1.JPG`, `IMG_0001_2.JPG`, …
- **Every copy is verified before it counts.** The copy is read back from the disk bypassing the Windows cache and compared with the data that was read; the source is re-read from the card as well, which catches flaky card readers and cables.
- **A source is deleted only after its copy (and the second copy, if on) is verified and committed to the disk** — a power cut or a crash can never leave you with neither file. Write-protected cards and files protected in the camera are never deleted.
- **Files are copied under a temporary name** (`*.importing`) and get their real name only when complete, so an interrupted import never leaves a half-written photo looking like a real one.
- **Every import is logged** — which file went where, with its hash — in `%LOCALAPPDATA%\PhotoShelver\logs`.

These rules are enforced by tests that inject failures (full disk, card removed mid-copy, file changing while it is copied, bit rot after writing, sharing violations), by tests on real exFAT volumes, by end-to-end UI tests, and by a mutation check that deliberately breaks each safety rule and makes sure some test notices.

## Getting it

Requires Windows 11 (x64). Build it with the [.NET 10 SDK](https://dotnet.microsoft.com/download):

```powershell
dotnet publish src/PhotoShelver.App -c Release -o publish
```

This produces a single `publish\PhotoShelver.exe` (~66 MB, runtime included) that runs without installing anything.

Settings (last folders, template, mode) are kept in `%LOCALAPPDATA%\PhotoShelver\settings.json`.

## Development

```powershell
dotnet build
dotnet test                       # core tests (xUnit)
dotnet run --project src/PhotoShelver.App
```

- **UI tests** — real window, real mouse; about a minute, don't touch the mouse meanwhile:
  `powershell -ExecutionPolicy Bypass -File tools/run_ui_tests.ps1`
- **All tests in Windows Sandbox** — a disposable virtual machine that sees only `src`, `tests` and .NET (read-only, no network): nothing on this computer can be touched, and the UI tests use the sandbox's own mouse, so you can keep working (~10 min). It also runs the tests on real exFAT volumes: two virtual disks created and formatted inside the sandbox (one write-protected, like a locked SD card) — on its own disposable disk, without administrator rights here. Needs the Windows feature "Windows Sandbox":
  `powershell -ExecutionPolicy Bypass -File tools/run_tests_sandbox.ps1` (`-UiOnly`, `-CoreOnly`, `-VolumesOnly`)
- **Test safety** — every test works in its own folder under `%TEMP%\PhotoShelverTests`, which refuses paths outside itself; the UI tests refuse to start the app unless every folder it gets is such a test folder.
- **A real card or USB disk** — set `PHOTOSHELVER_TEST_VOLUME` (see the header of `RealVolumeTests.cs`) and run `dotnet test --filter Category=RealVolume`.
- **Mutation check** — breaks each safety rule in turn and expects a failing test: `python tools/mutation_check.py` (add `--ui` for the interface).
- **CI** (GitHub Actions, `.github/workflows/ci.yml`): every push to `main` and every pull request is built, the core and UI tests run, and so do the exFAT volume tests (two virtual disks created on the runner, one write-protected — the runner is a disposable VM with administrator rights, like Windows Sandbox locally; `tools/create_test_volumes.ps1` serves both); the exe is started once and is attached to the run as an artifact (kept 14 days). The mutation check is started by hand (`mutation.yml`, Actions → Mutation check → Run workflow).
- **Releases** (`release.yml`): push a version tag from `main` — `git tag v1.2.0 && git push origin v1.2.0` (a pre-release: `v1.3.0-beta.1`). All CI checks run, the exe is built with that version, started once, and published as a GitHub Release together with `SHA256SUMS.txt`. Local builds are version `0.0.0-dev`. The exe is not signed yet: SmartScreen warns when a downloaded copy is first started.
- **Build rules**: warnings are errors; the .NET SDK is pinned exactly in `global.json` (the SDK brings some packages of its own version, which are in the lock files too; to move to a newer SDK, change the version there and run `dotnet restore --force-evaluate`); NuGet packages are locked in `packages.lock.json` (CI restores with `--locked-mode`). After changing a package: `dotnet restore --force-evaluate` and commit the updated lock files.
- **Test files** are synthetic (`tests/PhotoShelver.Core.Tests/Fixtures`, generated by `generate_fixtures.py`); format replicas of camera and phone files use the real structure with invented values.
- **Language**: Windows' display language decides when there is a translation for it, English otherwise; `PHOTOSHELVER_LANG=ru|en` overrides it. The texts are in `src/PhotoShelver.Core/Strings.resx` (English) and `Strings.ru.resx` (Russian).
- **Adding a language**: copy `Strings.resx` to `Strings.<language code>.resx` (e.g. `Strings.de.resx`) and translate the values — nothing else to change. Keep `{0}`, `{1}`… and the tokens in braces (`{NAME}`, `{YYYY}`…) as they are; `FileOne` / `FileFew` / `FileMany` are the plural forms of “file”. A test checks that every text exists in every language with the same `{0}`, `{1}`….
- **Screenshot**: the demo card and library are drawn by `python tools/make_demo_library.py <empty folder>`.

### Layout

- `src/PhotoShelver.Core` — everything without UI: scanning, capture dates, hashing, duplicates, folder tree, import.
- `src/PhotoShelver.App` — the WPF app.
- `tests/` — core tests and UI tests.
- `tools/` — test runners, mutation check, icon and demo generators.
