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

- **Sorted by the real capture date.** EXIF for photos; for videos the camera's own clock (Canon), Apple's date with time zone, or the GPS position's time zone — so photos and videos from one moment land in the same folder. Files without metadata fall back to the modification date (and are marked).
- **Duplicates are recognised by content.** Files that are already in the library (from an earlier import) or appear twice on the card are dimmed and not selected — detected by a 128-bit content hash, not by name.
- **Your folder scheme.** `{YYYY}/{YYYY}-{MM}-{DD}` by default, or any template with `{YYYY}`, `{YY}`, `{MM}`, `{MMM}`, `{DD}` and `/` — text outside the braces is kept as is; mistakes are explained as you type, and an invalid template never lets the import start.
- **Optional second copy.** Under *Advanced*: every imported file is also copied to another folder — a folder per import with the card's own names (like Lightroom), or a mirror of the library. When moving, the card is cleared only after *both* copies are verified.
- **Optional renaming.** Under *Advanced*: a file name template such as `{YYYY}-{MM}-{DD}_{NAME}` → `2026-08-14_IMG_2041.JPG` (date, time `{HH}{mm}{ss}`, original name `{NAME}`). Off by default; when on, it stays visible even with the section collapsed.
- **RAW-friendly.** Thumbnails for CR2, CR3, NEF, ARW, DNG, RAF, ORF, RW2 come from the preview the camera embedded — fast and without installing codecs. TIFF scans are shown upright.
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
- **Real volumes** — creates temporary exFAT virtual disks (one write-protected, like a locked SD card), runs the `RealVolume` tests and removes the disks; asks for administrator rights:
  `powershell -ExecutionPolicy Bypass -File tools/run_real_volume_tests.ps1`
- **Mutation check** — breaks each safety rule in turn and expects a failing test: `python tools/mutation_check.py` (add `--ui` for the interface).
- **Test files** are synthetic (`tests/PhotoShelver.Core.Tests/Fixtures`, generated by `generate_fixtures.py`); format replicas of camera and phone files use the real structure with invented values.
- **Language**: Windows' display language decides when there is a translation for it, English otherwise; `PHOTOSHELVER_LANG=ru|en` overrides it. The texts are in `src/PhotoShelver.Core/Strings.resx` (English) and `Strings.ru.resx` (Russian).
- **Adding a language**: copy `Strings.resx` to `Strings.<language code>.resx` (e.g. `Strings.de.resx`) and translate the values — nothing else to change. Keep `{0}`, `{1}`… and the tokens in braces (`{NAME}`, `{YYYY}`…) as they are; `FileOne` / `FileFew` / `FileMany` are the plural forms of “file”. A test checks that every text exists in every language with the same `{0}`, `{1}`….
- **Screenshot**: the demo card and library are drawn by `python tools/make_demo_library.py <empty folder>`.

### Layout

- `src/PhotoShelver.Core` — everything without UI: scanning, capture dates, hashing, duplicates, folder tree, import.
- `src/PhotoShelver.App` — the WPF app.
- `tests/` — core tests and UI tests.
- `tools/` — test runners, mutation check, icon and demo generators.
