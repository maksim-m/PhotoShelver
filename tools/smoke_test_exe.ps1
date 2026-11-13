<#
Starts a published PhotoShelver.exe and checks that it really works as shipped: the main window opens, no error is
logged, it closes normally and saves its settings. Optionally checks the version written into the exe.
The app gets its own temp settings folder (PHOTOSHELVER_SETTINGS): the user's settings are never touched.

  powershell -ExecutionPolicy Bypass -File tools\smoke_test_exe.ps1 -Exe publish\PhotoShelver.exe [-ExpectedVersion 1.2.3]
Exit code 0 when everything is fine.
#>
param(
    [Parameter(Mandatory)] [string] $Exe,
    [string] $ExpectedVersion,
    [int] $TimeoutSeconds = 60
)
$ErrorActionPreference = "Stop"
$Exe = (Resolve-Path $Exe).Path

if ($ExpectedVersion) {
    $info = (Get-Item $Exe).VersionInfo
    # ProductVersion is the full version (1.2.3-beta.1+commit); FileVersion the numeric one (1.2.3.0).
    if ($info.ProductVersion -ne $ExpectedVersion -and -not $info.ProductVersion.StartsWith("$ExpectedVersion+")) {
        throw "exe version is '$($info.ProductVersion)', expected '$ExpectedVersion'"
    }
    Write-Host "version: $($info.ProductVersion) (file $($info.FileVersion))"
}

$dir = Join-Path ([IO.Path]::GetTempPath()) ("PhotoShelverSmoke-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory $dir | Out-Null
$env:PHOTOSHELVER_SETTINGS = Join-Path $dir "settings.json"
$process = $null
try {
    $process = Start-Process $Exe -PassThru
    $null = $process.Handle # keeps the handle open, so that ExitCode is available after the exit
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $process.Refresh()
        if ($process.HasExited) { throw "the app exited on its own (exit code $($process.ExitCode))" }
        if ($process.MainWindowHandle -ne 0 -and $process.MainWindowTitle -eq "PhotoShelver") { break }
        Start-Sleep -Milliseconds 500
    }
    if ($process.MainWindowTitle -ne "PhotoShelver") { throw "no main window within $TimeoutSeconds s" }
    Write-Host "main window open after $([int]((Get-Date) - $process.StartTime).TotalSeconds) s"
    Start-Sleep -Seconds 3 # let the start-up work (folder reading, timers) run

    $errors = Join-Path $dir "errors.log"
    if (Test-Path $errors) { throw "the app logged an error:`n$(Get-Content $errors -Raw)" }

    if (-not $process.CloseMainWindow()) { throw "could not ask the window to close" }
    if (-not $process.WaitForExit(15000)) { throw "the app did not exit within 15 s after closing its window" }
    if ($process.ExitCode -ne 0) { throw "exit code $($process.ExitCode)" }
    if (-not (Test-Path $env:PHOTOSHELVER_SETTINGS)) { throw "settings were not saved on close" }
    if (Test-Path $errors) { throw "the app logged an error:`n$(Get-Content $errors -Raw)" }
    Write-Host "smoke test passed"
}
finally {
    if ($process -and -not $process.HasExited) { $process.Kill() }
    Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue
}
