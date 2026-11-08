<#
Runs the RealVolume tests on two temporary exFAT virtual disks (like SD cards):
  - a writable one (copy to/from a different volume, unbuffered reads on exFAT, volume detection);
  - a read-only one with the fixtures on it (a write-protected card: move must keep every source).
The disks are always detached and deleted at the end.

Needs administrator rights (diskpart). From a normal PowerShell:
  powershell -ExecutionPolicy Bypass -File tools\run_real_volume_tests.ps1
It re-launches itself elevated (UAC prompt) and prints the results.

To test a real SD card / USB disk instead, set PHOTOIMPORTER_TEST_VOLUME (and optionally
PHOTOIMPORTER_TEST_READONLY_VOLUME with fixtures in <root>\card) and run:
  dotnet test tests\PhotoImporter.Core.Tests --filter Category=RealVolume
#>
param([switch]$Elevated)

$Repo = Split-Path -Parent $PSScriptRoot
$Work = Join-Path $env:TEMP "PhotoImporterRealVolume"
$Log = Join-Path $Work "run.log"
$TestOut = Join-Path $Work "tests.txt"
$Dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $Dotnet) { $Dotnet = "C:\Program Files\dotnet\dotnet.exe" }

if (-not $Elevated) {
    New-Item -ItemType Directory -Force $Work | Out-Null
    Remove-Item $Log, $TestOut -ErrorAction SilentlyContinue
    # Build as the current user so the elevated run does not create admin-owned files in the repo.
    & $Dotnet build (Join-Path $Repo "tests\PhotoImporter.Core.Tests") -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "build failed" }
    Start-Process powershell.exe -Verb RunAs -Wait -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Elevated')
    Get-Content $Log | Where-Object { $_ -match '^\d\d:\d\d' }
    "=== tests ==="
    Get-Content $TestOut -ErrorAction SilentlyContinue |
        Where-Object { $_ -match '^\s+(Passed|Failed|Skipped) |Assert|Expected|Actual|line \d+|Total tests' }
    return
}

function Log($m) { "$(Get-Date -Format HH:mm:ss) $m" | Add-Content $Log }
function Invoke-DiskPartScript([string[]]$lines) {
    $f = Join-Path $Work "diskpart.txt"
    $lines | Set-Content $f -Encoding ascii
    $out = & "$env:SystemRoot\System32\diskpart.exe" /s $f 2>&1 | Out-String
    Log ("diskpart: " + ($lines -join " | "))
    if ($out -notmatch "successfully") { Log $out.Trim() }
}

$rw = Join-Path $Work "pi_rw.vhdx"
$ro = Join-Path $Work "pi_ro.vhdx"
$used = (Get-PSDrive -PSProvider FileSystem).Name
$free = [char[]](90..70) | Where-Object { $used -notcontains [string]$_ } | Select-Object -First 2
$X = [string]$free[0]; $Y = [string]$free[1]
Log "letters: writable=$X read-only=$Y"
try {
    Remove-Item $rw, $ro -ErrorAction SilentlyContinue
    Invoke-DiskPartScript @("create vdisk file=`"$rw`" maximum=256 type=expandable", "select vdisk file=`"$rw`"", "attach vdisk",
                            "create partition primary", "format fs=exfat quick label=PI_RW", "assign letter=$X")
    Invoke-DiskPartScript @("create vdisk file=`"$ro`" maximum=256 type=expandable", "select vdisk file=`"$ro`"", "attach vdisk",
                            "create partition primary", "format fs=exfat quick label=PI_RO", "assign letter=$Y")
    Copy-Item (Join-Path $Repo "tests\PhotoImporter.Core.Tests\Fixtures\media") "${Y}:\card" -Recurse
    Invoke-DiskPartScript @("select vdisk file=`"$ro`"", "detach vdisk")
    Invoke-DiskPartScript @("select vdisk file=`"$ro`"", "attach vdisk readonly")
    Start-Sleep -Seconds 2
    if (-not (Test-Path "${Y}:\card")) {
        Invoke-DiskPartScript @("select vdisk file=`"$ro`"", "select partition 1", "assign letter=$Y")
        Start-Sleep -Seconds 2
    }
    try { "x" | Set-Content "${Y}:\probe.txt" -ErrorAction Stop; Log "WARNING: read-only disk accepted a write" }
    catch { Log "read-only disk refuses writes: OK" }

    $env:PHOTOIMPORTER_TEST_VOLUME = "${X}:\"
    $env:PHOTOIMPORTER_TEST_READONLY_VOLUME = "${Y}:\"
    cmd /c "`"$Dotnet`" test `"$Repo\tests\PhotoImporter.Core.Tests`" --filter Category=RealVolume --no-build -nologo --logger `"console;verbosity=detailed`" > `"$TestOut`" 2>&1"
    Log "tests finished, exit code $LASTEXITCODE"
}
catch { Log ("ERROR: " + $_) }
finally {
    foreach ($v in $rw, $ro) {
        if (Test-Path $v) { Invoke-DiskPartScript @("select vdisk file=`"$v`"", "detach vdisk"); Remove-Item $v -ErrorAction SilentlyContinue }
    }
    Log ("cleanup done; virtual disks left: " + ((Test-Path $rw) -or (Test-Path $ro)))
}
