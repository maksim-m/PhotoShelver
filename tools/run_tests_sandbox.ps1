<#
Runs the tests inside Windows Sandbox: a disposable virtual machine with its own desktop, discarded when it closes.
  - The sandbox sees only src\, tests\ (with the build output), tools\ and the .NET installation, all read-only. The rest of
    this computer, including any real photos, is not visible to it; networking is off.
  - The UI tests move the mouse and click inside the sandbox's own desktop: you can keep working meanwhile.
    The sandbox window is put behind your windows (and restored if minimized: the tests are unreliable while it is
    minimized). Do not close it or click into it; it closes itself when the tests are done.

Needs the Windows feature "Windows Sandbox" (Windows 11 Pro/Enterprise), enabled once by an administrator:
  Enable-WindowsOptionalFeature -Online -FeatureName Containers-DisposableClientVM -All
then a restart.

  powershell -ExecutionPolicy Bypass -File tools\run_tests_sandbox.ps1            # everything
  powershell -ExecutionPolicy Bypass -File tools\run_tests_sandbox.ps1 -UiOnly
  powershell -ExecutionPolicy Bypass -File tools\run_tests_sandbox.ps1 -CoreOnly
  powershell -ExecutionPolicy Bypass -File tools\run_tests_sandbox.ps1 -VolumesOnly

"Volumes": the tests on real exFAT volumes. Inside the sandbox, two virtual disks are created, formatted exFAT (one is
then attached read-only, like a write-protected card) and the RealVolume tests run on them. All of that happens on the
sandbox's own disposable disk: the sandbox cannot see, let alone format, any disk of this computer (its folders are
shared into it as folders, not disks). No administrator rights or UAC prompt are needed on this computer.
#>
param([switch]$CoreOnly, [switch]$UiOnly, [switch]$VolumesOnly, [int]$TimeoutMinutes = 50)

$ErrorActionPreference = "Stop"
$Repo = Split-Path -Parent $PSScriptRoot
$Sandbox = Join-Path $env:SystemRoot "System32\WindowsSandbox.exe"
if (-not (Test-Path $Sandbox)) {
    throw "Windows Sandbox is not enabled. As administrator: Enable-WindowsOptionalFeature -Online -FeatureName Containers-DisposableClientVM -All (then restart)"
}
if (Get-Process -Name "WindowsSandbox*" -ErrorAction SilentlyContinue) { throw "A Windows Sandbox is already running (only one can run at a time) - close it first" }

$Dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $Dotnet) { $Dotnet = "C:\Program Files\dotnet\dotnet.exe" }
$DotnetRoot = Split-Path -Parent $Dotnet
$all = -not ($CoreOnly -or $UiOnly -or $VolumesOnly)
$runCore = $all -or $CoreOnly
$runUi = $all -or $UiOnly
$runVolumes = $all -or $VolumesOnly

# Build here, as the current user; the sandbox only runs what was built.
& $Dotnet build (Join-Path $Repo "tests\PhotoShelver.Core.Tests") -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "build failed" }
& $Dotnet build (Join-Path $Repo "tests\PhotoShelver.UiTests") -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "build failed" }

$Session = Join-Path $env:TEMP ("PhotoShelverSandbox\" + (Get-Date -Format "yyyyMMdd-HHmmss"))
$Io = Join-Path $Session "io"
New-Item -ItemType Directory -Force $Io | Out-Null

# Inside the sandbox: C:\PS\repo\src and \tests are the read-only mapped folders; C:\PS\repo itself is the sandbox's
# own (writable) folder, so the solution marker the UI tests look for can be created there.
$inner = @'
$ErrorActionPreference = "Continue"
$env:DOTNET_ROOT = "C:\PS\dotnet"
$env:PATH = "C:\PS\dotnet;" + $env:PATH
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"; $env:DOTNET_NOLOGO = "1"; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
New-Item -ItemType File -Force "C:\PS\repo\PhotoShelver.sln" | Out-Null
Set-Location $env:TEMP
"started $(Get-Date -Format HH:mm:ss)" | Set-Content "C:\PS\io\status.txt"
if (__CORE__) {
    & dotnet test "C:\PS\repo\tests\PhotoShelver.Core.Tests\bin\Debug\net10.0-windows\PhotoShelver.Core.Tests.dll" --logger "console;verbosity=normal" *> "C:\PS\io\core.txt"
}
if (__UI__) {
    $env:PHOTOSHELVER_UI_TESTS = "1"
    & dotnet test "C:\PS\repo\tests\PhotoShelver.UiTests\bin\Debug\net10.0-windows\PhotoShelver.UiTests.dll" --blame-hang-timeout 5m --logger "console;verbosity=normal" *> "C:\PS\io\ui.txt"
}
if (__VOLUMES__) {
    # Two exFAT virtual disks on the sandbox's own disk (the same script as in CI).
    $log = "C:\PS\io\volumes.txt"
    try {
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\PS\repo\tools\create_test_volumes.ps1" `
            -Fixtures "C:\PS\repo\tests\PhotoShelver.Core.Tests\Fixtures\media" -Folder "C:\PS\volumes" *> $log
        if ($LASTEXITCODE -ne 0) { throw (Get-Content $log -Raw) }
        $env:PHOTOSHELVER_TEST_VOLUME = "X:\"
        $env:PHOTOSHELVER_TEST_READONLY_VOLUME = "Y:\"
        # Separate files: appending a test run's output to the log would mix two text encodings in one file.
        & dotnet test "C:\PS\repo\tests\PhotoShelver.Core.Tests\bin\Debug\net10.0-windows\PhotoShelver.Core.Tests.dll" --filter "Category=RealVolume" --blame-hang-timeout 5m --logger "console;verbosity=normal" *> "C:\PS\io\volumes-core.txt"
        $env:PHOTOSHELVER_UI_TESTS = "1"
        & dotnet test "C:\PS\repo\tests\PhotoShelver.UiTests\bin\Debug\net10.0-windows\PhotoShelver.UiTests.dll" --filter "Category=RealVolumeUi" --blame-hang-timeout 5m --logger "console;verbosity=normal" *> "C:\PS\io\volumes-ui.txt"
    }
    catch { "ERROR setting up the test volumes: $_" | Add-Content $log }
}
"done $(Get-Date -Format HH:mm:ss)" | Set-Content "C:\PS\io\done.txt"
shutdown /s /t 0
'@
$inner.Replace("__CORE__", ('$' + "$runCore".ToLower())).Replace("__UI__", ('$' + "$runUi".ToLower())).Replace("__VOLUMES__", ('$' + "$runVolumes".ToLower())) |
    Set-Content (Join-Path $Io "run.ps1") -Encoding UTF8

function Mapped([string]$hostFolder, [string]$sandboxFolder, [bool]$readOnly) {
    "    <MappedFolder><HostFolder>$hostFolder</HostFolder><SandboxFolder>$sandboxFolder</SandboxFolder><ReadOnly>$("$readOnly".ToLower())</ReadOnly></MappedFolder>"
}
$config = @"
<Configuration>
  <Networking>Disable</Networking>
  <ClipboardRedirection>Disable</ClipboardRedirection>
  <PrinterRedirection>Disable</PrinterRedirection>
  <AudioInput>Disable</AudioInput>
  <VideoInput>Disable</VideoInput>
  <ProtectedClient>Disable</ProtectedClient>
  <MemoryInMB>4096</MemoryInMB>
  <MappedFolders>
$(Mapped (Join-Path $Repo "src") "C:\PS\repo\src" $true)
$(Mapped (Join-Path $Repo "tests") "C:\PS\repo\tests" $true)
$(Mapped (Join-Path $Repo "tools") "C:\PS\repo\tools" $true)
$(Mapped $DotnetRoot "C:\PS\dotnet" $true)
$(Mapped $Io "C:\PS\io" $false)
  </MappedFolders>
  <LogonCommand><Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\PS\io\run.ps1</Command></LogonCommand>
</Configuration>
"@
$wsb = Join-Path $Session "tests.wsb"
$config | Set-Content $wsb -Encoding UTF8

Add-Type @"
using System; using System.Runtime.InteropServices;
public static class SandboxWindow {
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
"@
# The UI tests are unreliable while the sandbox window is minimized (its desktop is then partly suspended), but run
# fine while it is merely behind other windows. So it is kept behind other windows, and restored (without taking the
# focus) if it gets minimized.
function Keep-SandboxBehind([bool]$force) {
    $w = Get-Process -Name "WindowsSandboxRemoteSession", "WindowsSandboxClient", "WindowsSandbox" -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    if (-not $w) { return $false }
    $minimized = [SandboxWindow]::IsIconic($w.MainWindowHandle)
    if ($minimized) { [SandboxWindow]::ShowWindow($w.MainWindowHandle, 4) | Out-Null } # SW_SHOWNOACTIVATE
    if ($force -or $minimized) {
        # HWND_BOTTOM; SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE
        [SandboxWindow]::SetWindowPos($w.MainWindowHandle, [IntPtr]1, 0, 0, 0, 0, 0x13) | Out-Null
    }
    return $true
}

"Starting Windows Sandbox (results in $Io). Its window goes behind your windows: keep working, but do not minimize"
"or close it, and do not click into it. It closes itself when the tests are done."
Start-Process $Sandbox -ArgumentList "`"$wsb`""
$done = Join-Path $Io "done.txt"
$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
$sentBack = $false
while (-not (Test-Path $done) -and (Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 3
    if (-not $sentBack) { $sentBack = Keep-SandboxBehind $true; if ($sentBack) { Start-Sleep -Seconds 2; Keep-SandboxBehind $true | Out-Null } }
    else { Keep-SandboxBehind $false | Out-Null }
}
if (-not (Test-Path $done)) { "TIMEOUT after $TimeoutMinutes min - closing the sandbox" }
else {
    # The sandbox shuts itself down; give its window a moment to go away on its own.
    for ($i = 0; $i -lt 20 -and (Get-Process -Name "WindowsSandbox*" -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Seconds 1 }
}
# Its window can stay behind after the shutdown: close it (it was started by this script; nothing in it is kept anyway).
Get-Process -Name "WindowsSandbox*" -ErrorAction SilentlyContinue | Stop-Process -Force
# Every expected test run must have finished with a summary and no failures; anything else is reported as a problem
# (a missing or cut-off result is never taken for a pass).
$expected = @()
if ($runCore) { $expected += @{ Name = "core tests"; File = "core.txt" } }
if ($runUi) { $expected += @{ Name = "UI tests"; File = "ui.txt" } }
if ($runVolumes) {
    $expected += @{ Name = "volume tests (core)"; File = "volumes-core.txt" }
    $expected += @{ Name = "volume tests (UI)"; File = "volumes-ui.txt" }
}
$problems = @()
if ($runVolumes -and (Test-Path (Join-Path $Io "volumes.txt"))) { Get-Content (Join-Path $Io "volumes.txt") }
foreach ($part in $expected) {
    $file = Join-Path $Io $part.File
    "=== $($part.Name) ==="
    if (-not (Test-Path $file)) { "no result (the sandbox did not get that far)"; $problems += $part.Name; continue }
    $lines = Get-Content $file
    $lines | Where-Object { $_ -cmatch '^\s+Failed |Error Message|^\s+(Passed|Failed|Skipped):|Total tests|ERROR' }
    $total = $lines | Where-Object { $_ -match '^\s*Total tests:' }
    $failed = $lines | Where-Object { $_ -match '^\s+Failed:\s+[1-9]' }
    if (-not $total -or $failed) { $problems += $part.Name }
}
""
if ($problems.Count -eq 0) { "ALL PASSED" }
else { "PROBLEMS in: " + ($problems -join ", ") + "  (details in $Io)"; exit 1 }
