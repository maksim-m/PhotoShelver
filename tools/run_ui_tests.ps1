<#
Runs the end-to-end UI tests: the real PhotoShelver window is started and driven through UI Automation.
Windows will appear and close while the tests run. Do not use the mouse or keyboard meanwhile:
some tests click with the real mouse (double click, context menu).

Each test uses its own temp folder with a copy of the test fixtures and its own settings file
(PHOTOSHELVER_SETTINGS), so your real settings, photos and libraries are never touched.

  powershell -ExecutionPolicy Bypass -File tools\run_ui_tests.ps1
On failure a screenshot path is printed (in %TEMP%\PhotoShelverUiTests).
#>
$Repo = Split-Path -Parent $PSScriptRoot
$Dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $Dotnet) { $Dotnet = "C:\Program Files\dotnet\dotnet.exe" }

& $Dotnet build (Join-Path $Repo "tests\PhotoShelver.UiTests") -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "build failed" }

$env:PHOTOSHELVER_UI_TESTS = "1"
& $Dotnet test (Join-Path $Repo "tests\PhotoShelver.UiTests") --no-build -nologo --blame-hang-timeout 5m --logger "console;verbosity=normal"
exit $LASTEXITCODE
