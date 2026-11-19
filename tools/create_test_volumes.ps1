<#
Creates the two exFAT test volumes of the RealVolume tests, like SD cards: a writable one and a write-protected one
(attached read-only) that holds a copy of the test fixtures in \card. Needs administrator rights (diskpart).

ONLY FOR A DISPOSABLE MACHINE: Windows Sandbox (tools\run_tests_sandbox.ps1) or a CI runner. It creates new virtual
disks and formats only those, but it is not meant to be run on a computer with real data.

  powershell -ExecutionPolicy Bypass -File tools\create_test_volumes.ps1 -Fixtures <tests\...\Fixtures\media> -Folder <for the .vhdx files>
Then: PHOTOSHELVER_TEST_VOLUME=X:\ and PHOTOSHELVER_TEST_READONLY_VOLUME=Y:\ (or the letters given).
Diskpart is used because the storage cmdlets are not available in Windows Sandbox.
#>
param(
    [Parameter(Mandatory)] [string] $Fixtures,
    [Parameter(Mandatory)] [string] $Folder,
    [string] $WritableLetter = "X",
    [string] $ReadOnlyLetter = "Y"
)
$ErrorActionPreference = "Stop"

foreach ($letter in @($WritableLetter, $ReadOnlyLetter)) {
    if (Test-Path "${letter}:\") { throw "drive ${letter}: already exists: choose free letters" }
}
New-Item -ItemType Directory -Force $Folder | Out-Null
$script = Join-Path $Folder "diskpart.txt"

function Run-DiskPart([string[]] $lines) {
    $lines | Set-Content $script -Encoding ascii
    $output = & diskpart.exe /s $script 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "diskpart failed:`n$output" }
}

$rw = Join-Path $Folder "pi_rw.vhdx"
$ro = Join-Path $Folder "pi_ro.vhdx"
foreach ($v in @(@{ File = $rw; Label = "PI_RW"; Letter = $WritableLetter }, @{ File = $ro; Label = "PI_RO"; Letter = $ReadOnlyLetter })) {
    if (Test-Path $v.File) { throw "$($v.File) already exists" }
    Run-DiskPart @("create vdisk file=`"$($v.File)`" maximum=256 type=expandable", "select vdisk file=`"$($v.File)`"", "attach vdisk",
        "create partition primary", "format fs=exfat label=$($v.Label) quick", "assign letter=$($v.Letter)")
    if (-not (Test-Path "$($v.Letter):\")) { throw "virtual disk $($v.Label) did not get the letter $($v.Letter)" }
}

Copy-Item $Fixtures "${ReadOnlyLetter}:\card" -Recurse
# The write-protected card: the same disk attached again, read-only.
Run-DiskPart @("select vdisk file=`"$ro`"", "detach vdisk", "attach vdisk readonly")
for ($i = 0; $i -lt 40 -and -not (Test-Path "${ReadOnlyLetter}:\card"); $i++) { Start-Sleep -Milliseconds 500 }
if (-not (Test-Path "${ReadOnlyLetter}:\card")) { throw "the read-only disk is not back as ${ReadOnlyLetter}:" }
$wrote = $true
try { "x" | Set-Content "${ReadOnlyLetter}:\probe.txt" -ErrorAction Stop } catch { $wrote = $false }
if ($wrote) { throw "the read-only test disk accepted a write" }

# Not in Windows Sandbox (no storage cmdlets there): diskpart's "format fs=exfat" is trusted then.
if (Get-Command Get-Volume -ErrorAction SilentlyContinue) {
    foreach ($letter in @($WritableLetter, $ReadOnlyLetter)) {
        $fs = (Get-Volume -DriveLetter $letter).FileSystem
        if ($fs -ne "exFAT") { throw "${letter}: is '$fs', not exFAT" }
    }
}
"volumes ready: ${WritableLetter}: exFAT writable, ${ReadOnlyLetter}: exFAT read-only"
