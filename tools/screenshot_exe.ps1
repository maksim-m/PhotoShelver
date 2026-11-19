<#
Starts PhotoShelver.exe on a demo card and library (tools/make_demo_library.py) and saves a screenshot of its window:
to see how the app looks on another Windows version (CI runs it on several), in the light or the dark theme.
The app gets its own temp settings folder (PHOTOSHELVER_SETTINGS): the user's settings are never touched.
-Theme Dark switches the apps' theme of the current Windows user (and back afterwards): meant for CI runners and VMs.

  powershell -ExecutionPolicy Bypass -File tools\screenshot_exe.ps1 -Exe publish\PhotoShelver.exe -Demo <folder> -Out shot.png [-Theme Dark]
#>
param(
    [Parameter(Mandatory)] [string] $Exe,
    [Parameter(Mandatory)] [string] $Demo, # made by make_demo_library.py: <Demo>\card and <Demo>\library
    [Parameter(Mandatory)] [string] $Out,
    [ValidateSet("Light", "Dark")] [string] $Theme = "Light",
    [int] $TimeoutSeconds = 60
)
$ErrorActionPreference = "Stop"
$Exe = (Resolve-Path $Exe).Path
$Demo = (Resolve-Path $Demo).Path
$Out = [IO.Path]::GetFullPath($Out)

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Win {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT rect, int size);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
"@
[Win]::SetProcessDPIAware() | Out-Null # window size in real pixels (on a scaled screen too)

$personalize = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"
$oldTheme = (Get-ItemProperty $personalize -ErrorAction SilentlyContinue).AppsUseLightTheme
$dir = Join-Path ([IO.Path]::GetTempPath()) ("PhotoShelverShot-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory $dir | Out-Null
$env:PHOTOSHELVER_SETTINGS = Join-Path $dir "settings.json"
@{
    SourcePath = Join-Path $Demo "card"
    TargetPath = Join-Path $Demo "library"
    WindowWidth = 1000
    WindowHeight = 700
} | ConvertTo-Json | Set-Content -Encoding utf8 $env:PHOTOSHELVER_SETTINGS

$process = $null
try {
    if (-not (Test-Path $personalize)) { New-Item $personalize -Force | Out-Null }
    Set-ItemProperty $personalize AppsUseLightTheme ([int]($Theme -eq "Light"))

    $process = Start-Process $Exe -PassThru
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $process.Refresh()
        if ($process.HasExited) { throw "the app exited on its own (exit code $($process.ExitCode))" }
        if ($process.MainWindowHandle -ne 0 -and $process.MainWindowTitle -eq "PhotoShelver") { break }
        Start-Sleep -Milliseconds 500
    }
    if ($process.MainWindowTitle -ne "PhotoShelver") { throw "no main window within $TimeoutSeconds s" }
    Start-Sleep -Seconds 8 # scan, thumbnails, folder tree
    [Win]::SetForegroundWindow($process.MainWindowHandle) | Out-Null # in front: the screenshot is taken from the screen
    Start-Sleep -Milliseconds 500

    $hwnd = $process.MainWindowHandle
    # What is really on the screen (the title bar exactly as Windows draws it), within the visible frame of the window
    # (DWMWA_EXTENDED_FRAME_BOUNDS: without the invisible resize border around it).
    $r = New-Object Win+RECT
    if ([Win]::DwmGetWindowAttribute($hwnd, 9, [ref]$r, 16) -ne 0) { [Win]::GetWindowRect($hwnd, [ref]$r) | Out-Null }
    $bitmap = New-Object System.Drawing.Bitmap ($r.Right - $r.Left), ($r.Bottom - $r.Top)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bitmap.Size)
    $g.Dispose()
    New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
    $bitmap.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    Write-Host "screenshot ($Theme): $Out"

    $errors = Join-Path $dir "errors.log"
    if (Test-Path $errors) { throw "the app logged an error:`n$(Get-Content $errors -Raw)" }
}
finally {
    if ($process -and -not $process.HasExited) { $process.Kill(); $process.WaitForExit(5000) | Out-Null }
    if ($null -ne $oldTheme) { Set-ItemProperty $personalize AppsUseLightTheme $oldTheme }
    Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue
}
