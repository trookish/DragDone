<#
.SYNOPSIS
    Installs the built MSIX, proves a packaged DragDone works, then removes it.

.DESCRIPTION
    Checks the things that only break in a package: package identity at runtime, the virtualised
    settings folder, the startup task instead of the Run key, and a real FFmpeg job started from
    inside the package. Leaves no package, no certificate and no Run key behind.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File packaging/verify_msix.ps1
#>
[CmdletBinding()]
param(
    [string]$Package = '',
    [string]$WorkDir = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$identityName = (Get-Content (Join-Path $root 'packaging\identity.json') -Raw | ConvertFrom-Json).identityName
if (-not $Package) {
    $Package = Get-ChildItem (Join-Path $root 'artifacts') -Filter 'DragDone-*-x64.msix' |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $Package -or -not (Test-Path $Package)) { throw "no package found; run tools/build_msix.ps1 -Sign first" }
if (-not $WorkDir) { $WorkDir = Join-Path $env:TEMP 'dragdone-msix-verify' }
if (Test-Path $WorkDir) { Remove-Item $WorkDir -Recurse -Force }
New-Item -ItemType Directory -Path $WorkDir -Force | Out-Null

$results = New-Object System.Collections.Generic.List[object]
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    $results.Add([pscustomobject]@{ Check = $name; Pass = $ok; Detail = $detail })
    $tag = if ($ok) { 'PASS' } else { 'FAIL' }
    Write-Host ("  {0}  {1,-46} {2}" -f $tag, $name, $detail)
}

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$hadRunKey = $null -ne (Get-ItemProperty -Path $runKey -Name DragDone -ErrorAction SilentlyContinue)
if ($hadRunKey) {
    Remove-ItemProperty -Path $runKey -Name DragDone -Force
    Write-Host '  (cleared an existing Run key left by the installer channel)'
}

Write-Host "installing $(Split-Path -Leaf $Package)..."
$signed = (Get-AuthenticodeSignature $Package).Status -eq 'Valid'
$installed = $true
try {
    Add-AppxPackage -Path $Package -ForceApplicationShutdown
} catch {
    $installed = $false
}
if (-not $installed) {
    # Two ways this fails without elevation: a self-signed certificate is trusted per-user but
    # deployment checks machine-wide stores, and -AllowUnsigned refuses a publisher outside the
    # unsigned namespace. Registering the layout gives the app real package identity without
    # either, which is what Developer Mode is for and is enough to test everything below.
    $layout = Get-ChildItem (Join-Path $root 'artifacts\msix-layout') -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending | Select-Object -First 1
    if (-not $layout) { throw 'no layout folder to register; rebuild with tools/build_msix.ps1' }
    Write-Host "  (package not installable without elevation; registering $($layout.FullName))"
    Get-AppxPackage | Where-Object { $_.Name -eq $identityName } | Remove-AppxPackage -ErrorAction SilentlyContinue
    Add-AppxPackage -Register (Join-Path $layout.FullName 'AppxManifest.xml') -ForceApplicationShutdown
    Check 'layout registered with package identity' $true 'developer-mode registration'
}
$pkg = Get-AppxPackage | Where-Object { $_.Name -eq $identityName }
Check 'package installed' ($null -ne $pkg) $($pkg.PackageFullName)
$pfn = $pkg.PackageFamilyName

# First launch, exactly how Windows starts it: no arguments, so it lands in the tray.
Start-Process "shell:AppsFolder\$pfn!App"
$proc = $null
for ($i = 0; $i -lt 40 -and -not $proc; $i++) {
    Start-Sleep -Milliseconds 250
    $proc = Get-Process -Name DragDone -ErrorAction SilentlyContinue | Select-Object -First 1
}
Check 'packaged app runs' ($null -ne $proc) $(if ($proc) { "pid $($proc.Id)" } else { 'no process' })

# Settings must land in the package's virtualised AppData, not the plain %APPDATA%\DragDone.
$virtual = Join-Path $env:LOCALAPPDATA "Packages\$pfn\LocalCache\Roaming\DragDone\settings.json"
for ($i = 0; $i -lt 20 -and -not (Test-Path $virtual); $i++) { Start-Sleep -Milliseconds 250 }
Check 'settings are virtualised' (Test-Path $virtual) $virtual

# The packaged branch must not have written a Run key (it would never be read).
$runNow = $null -ne (Get-ItemProperty -Path $runKey -Name DragDone -ErrorAction SilentlyContinue)
Check 'no Run key from a packaged app' (-not $runNow)

Start-Sleep -Seconds 2
$identityOut = Join-Path $WorkDir 'identity.txt'
Invoke-CommandInDesktopPackage -PackageFamilyName $pfn -AppId 'App' -Command 'DragDone.exe' `
    -Args "--identity --out `"$identityOut`"" -PreventBreakaway | Out-Null
for ($i = 0; $i -lt 40 -and -not (Test-Path $identityOut); $i++) { Start-Sleep -Milliseconds 250 }
if (Test-Path $identityOut) {
    $identity = Get-Content $identityOut -Raw
    Write-Host $identity
    Check 'runtime reports package identity' ($identity -match 'packaged\s*=\s*True') ''
    Check 'family name matches' ($identity -match [regex]::Escape($pfn)) ''
    Check 'startup uses a startup task' ($identity -match 'startup\s*=\s*startupTask=Enabled') ''
} else {
    Check 'identity report written' $false 'Invoke-CommandInDesktopPackage produced nothing'
}

# A real job, launched inside the package: the thing that used to fail with a dead working directory.
$ffmpeg = Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Links\ffmpeg.exe'
if (Test-Path $ffmpeg) {
    $clip = Join-Path $WorkDir 'clip.mp4'
    & $ffmpeg -y -v error -f lavfi -i 'testsrc2=duration=2:size=320x240:rate=30' `
        -f lavfi -i 'sine=frequency=440:duration=2' -shortest -c:v libx264 -preset ultrafast -c:a aac $clip
    $report = Join-Path $WorkDir 'job.json'
    Invoke-CommandInDesktopPackage -PackageFamilyName $pfn -AppId 'App' -Command 'DragDone.exe' `
        -Args "--run vid.mute `"$clip`" --report `"$report`"" -PreventBreakaway | Out-Null
    for ($i = 0; $i -lt 60 -and -not (Test-Path $report); $i++) { Start-Sleep -Milliseconds 500 }
    $ok = (Test-Path $report) -and ((Get-Content $report -Raw | ConvertFrom-Json).outcome -eq 'Succeeded')
    $made = Get-ChildItem $WorkDir -Filter '*muted*' -ErrorAction SilentlyContinue
    Check 'video job runs inside the package' ($ok -and $made) $(if ($made) { $made[0].Name } else { 'no output' })
} else {
    Write-Host '  (ffmpeg not installed, skipping the conversion check)' -ForegroundColor DarkGray
}

Get-Process -Name DragDone -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1
Remove-AppxPackage -Package $pkg.PackageFullName
Check 'package uninstalled' ($null -eq (Get-AppxPackage | Where-Object { $_.Name -eq $identityName }))
Check 'virtualised settings removed with it' (-not (Test-Path (Join-Path $env:LOCALAPPDATA "Packages\$pfn")))
if ($hadRunKey) { Write-Host '  (installer channel Run key was cleared for the test)' }

$failed = @($results | Where-Object { -not $_.Pass })
Write-Host ''
if ($failed.Count -eq 0) {
    Write-Host "msix verification: ok ($($results.Count)/$($results.Count))" -ForegroundColor Green
    exit 0
}
Write-Host "msix verification: $($failed.Count) of $($results.Count) failed" -ForegroundColor Red
$failed | ForEach-Object { Write-Host "  - $($_.Check): $($_.Detail)" }
exit 1
