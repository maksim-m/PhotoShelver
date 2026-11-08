<#
Runs the RealVolume tests on two temporary exFAT virtual disks (like SD cards):
  - a writable one (copy to/from a different volume, unbuffered reads on exFAT, volume detection);
  - a read-only one with the fixtures on it (a write-protected card: move must keep every source).
The disks are always detached and their files deleted at the end.

Safety: only two new files are created, in %TEMP%\PhotoImporterRealVolume. Every disk operation
(initialize, partition, format) addresses the disk by the number Windows reports for *that image file*,
after checking it is a file-backed virtual disk whose image path is ours and whose size is <= 300 MB.
Real disks are never selected, partitioned or formatted. diskpart is only used to create the image file.

Needs administrator rights. From a normal PowerShell:
  powershell -ExecutionPolicy Bypass -File tools\run_real_volume_tests.ps1
It re-launches itself elevated (UAC prompt) and prints the results.

To test a real SD card / USB disk instead, see the header of RealVolumeTests.cs.
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

$ErrorActionPreference = "Stop"
function Log($m) { "$(Get-Date -Format HH:mm:ss) $m" | Add-Content $Log }

function New-ImageFile([string]$path) {
    # diskpart only creates the .vhdx file here; it never selects or modifies a disk.
    $script = Join-Path $Work "create.txt"
    "create vdisk file=`"$path`" maximum=256 type=expandable" | Set-Content $script -Encoding ascii
    $out = & "$env:SystemRoot\System32\diskpart.exe" /s $script 2>&1 | Out-String
    if (-not (Test-Path $path)) { throw "could not create $path`n$out" }
}

function Get-OwnVirtualDisk([string]$path) {
    # The disk Windows attached for THIS image file - and nothing else.
    $image = Get-DiskImage -ImagePath $path
    if (-not $image.Attached) { throw "$path is not attached" }
    $disk = Get-Disk -Number $image.Number
    if ($disk.BusType -ne 'File Backed Virtual') { throw "disk $($disk.Number) is not a virtual disk ($($disk.BusType)) - refusing" }
    if ($disk.Location -ne $path) { throw "disk $($disk.Number) belongs to '$($disk.Location)', not '$path' - refusing" }
    if ($disk.Size -gt 300MB) { throw "disk $($disk.Number) is $($disk.Size) bytes - too large for our test image, refusing" }
    return $disk
}

function New-TestVolume([string]$path, [string]$label) {
    New-ImageFile $path
    Mount-DiskImage -ImagePath $path -Access ReadWrite -NoDriveLetter | Out-Null
    $disk = Get-OwnVirtualDisk $path
    if ($disk.PartitionStyle -ne 'RAW') { throw "disk $($disk.Number) is already initialized - refusing" }
    Initialize-Disk -Number $disk.Number -PartitionStyle MBR
    $partition = New-Partition -DiskNumber $disk.Number -UseMaximumSize -AssignDriveLetter
    $volume = Format-Volume -Partition $partition -FileSystem exFAT -NewFileSystemLabel $label -Confirm:$false
    $letter = (Get-Partition -DiskNumber $disk.Number -PartitionNumber $partition.PartitionNumber).DriveLetter
    Log "created $label on virtual disk $($disk.Number) as ${letter}: ($($volume.FileSystem), $([math]::Round($disk.Size/1MB)) MB)"
    return [string]$letter
}

$rw = Join-Path $Work "pi_rw.vhdx"
$ro = Join-Path $Work "pi_ro.vhdx"
try {
    foreach ($v in $rw, $ro) {
        if (Test-Path $v) { Dismount-DiskImage -ImagePath $v -ErrorAction SilentlyContinue | Out-Null; Remove-Item $v }
    }
    $X = New-TestVolume $rw "PI_RW"
    $Y = New-TestVolume $ro "PI_RO"

    Copy-Item (Join-Path $Repo "tests\PhotoImporter.Core.Tests\Fixtures\media") "${Y}:\card" -Recurse
    Dismount-DiskImage -ImagePath $ro | Out-Null
    Mount-DiskImage -ImagePath $ro -Access ReadOnly | Out-Null
    $disk = Get-OwnVirtualDisk $ro
    $Y = [string]((Get-Partition -DiskNumber $disk.Number | Where-Object DriveLetter) | Select-Object -First 1).DriveLetter
    if (-not $Y) { throw "read-only test disk got no drive letter" }
    Log "re-attached PI_RO read-only as ${Y}:"
    $wrote = $true
    try { "x" | Set-Content "${Y}:\probe.txt" -ErrorAction Stop } catch { $wrote = $false }
    if ($wrote) { throw "read-only test disk accepted a write" }
    Log "read-only disk refuses writes: OK"

    $env:PHOTOIMPORTER_TEST_VOLUME = "${X}:\"
    $env:PHOTOIMPORTER_TEST_READONLY_VOLUME = "${Y}:\"
    cmd /c "`"$Dotnet`" test `"$Repo\tests\PhotoImporter.Core.Tests`" --filter Category=RealVolume --no-build -nologo --logger `"console;verbosity=detailed`" > `"$TestOut`" 2>&1"
    Log "tests finished, exit code $LASTEXITCODE"
}
catch { Log ("ERROR: " + $_) }
finally {
    foreach ($v in $rw, $ro) {
        if (Test-Path $v) {
            Dismount-DiskImage -ImagePath $v -ErrorAction SilentlyContinue | Out-Null
            Remove-Item $v -ErrorAction SilentlyContinue
        }
    }
    Log ("cleanup done; virtual disk files left: " + ((Test-Path $rw) -or (Test-Path $ro)))
}
