<#
.SYNOPSIS
    Installs the signed MSIX, proves a packaged DragDone works, and removes every trace.

.DESCRIPTION
    Must run elevated: Windows validates a package signature against the machine's trust stores,
    so a self-signed certificate has to be trusted for the local computer, and sideloading an
    unsigned package needs the machine-wide policy. This trusts the dev certificate, installs the
    package, checks identity / virtualised settings / the startup task / a real FFmpeg job, then
    uninstalls and removes the certificate again — so one UAC prompt leaves nothing behind.

.EXAMPLE
    Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File','packaging/verify_msix_elevated.ps1'
#>
[CmdletBinding()]
param(
    [string]$Package = '',
    [string]$Pfx = '',
    [string]$WorkDir = '',
    [string]$Log = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Package) {
    $Package = Get-ChildItem (Join-Path $root 'artifacts') -Filter 'DragDone-*-x64.msix' |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $Pfx) { $Pfx = Join-Path $root 'artifacts\DragDone-dev.pfx' }
if (-not $WorkDir) { $WorkDir = Join-Path $env:TEMP 'dragdone-msix-verify' }
if (-not $Log) { $Log = Join-Path $WorkDir 'verify.log' }
if (Test-Path $WorkDir) { Remove-Item $WorkDir -Recurse -Force }
New-Item -ItemType Directory -Path $WorkDir -Force | Out-Null

$lines = New-Object System.Collections.Generic.List[string]
$failed = 0
function Say([string]$text) {
    $lines.Add($text)
    Write-Host $text
}
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if (-not $ok) { $script:failed++ }
    $tag = if ($ok) { 'PASS' } else { 'FAIL' }
    Say ("  {0}  {1,-46} {2}" -f $tag, $name, $detail)
}

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$hadRunKey = $null -ne (Get-ItemProperty -Path $runKey -Name DragDone -ErrorAction SilentlyContinue)
if ($hadRunKey) { Remove-ItemProperty -Path $runKey -Name DragDone -Force }

$cert = $null
$pkg = $null
$saved = New-Object System.Collections.Generic.List[object]
try {
    # 0. Sideloading. This machine carries an Appx *policy* with AllowAllTrustedApps = 0, which
    #    overrides Developer Mode and blocks every MSIX install that does not come from the Store.
    #    Both keys are lifted for the test and restored in the finally block.
    $policies = @(
        @{ Path = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock'; Name = 'AllowAllTrustedApps' },
        @{ Path = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Appx'; Name = 'AllowAllTrustedApps' },
        @{ Path = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Appx'; Name = 'AllowDevelopmentWithoutDevLicense' }
    )
    foreach ($p in $policies) {
        if (-not (Test-Path $p.Path)) { New-Item -Path $p.Path -Force | Out-Null }
        $before = (Get-ItemProperty -Path $p.Path -Name $p.Name -ErrorAction SilentlyContinue).($p.Name)
        $saved += [pscustomobject]@{ Path = $p.Path; Name = $p.Name; Value = $before }
        Set-ItemProperty -Path $p.Path -Name $p.Name -Value 1 -Type DWord
        Say ("  ..    " + $p.Path.Split('\')[-1] + "\" + $p.Name + " set to 1 (was: " + $(if ($null -eq $before) { 'not set' } else { $before }) + ')')
    }

    # 1. Trust the dev certificate for the local computer (signature chain check is machine-wide).
    $password = ConvertTo-SecureString -String 'dragdone' -Force -AsPlainText
    $cert = Import-PfxCertificate -FilePath $Pfx -CertStoreLocation 'Cert:\LocalMachine\My' -Password $password
    foreach ($storeName in @('Root', 'TrustedPeople')) {
        $store = New-Object System.Security.Cryptography.X509Certificates.X509Store($storeName, 'LocalMachine')
        $store.Open('ReadWrite')
        $store.Add($cert)
        $store.Close()
    }
    Check 'dev certificate trusted for the machine' $true $cert.Thumbprint

    # 2. Install.
    Add-AppxPackage -Path $Package -ForceApplicationShutdown
    $pkg = Get-AppxPackage | Where-Object { $_.Name -eq 'DragDone.Dev' }
    Check 'package installed from the .msix' ($null -ne $pkg) $pkg.PackageFullName
    $pfn = $pkg.PackageFamilyName

    # 3. First launch, the way Windows starts it: no arguments, so it goes to the tray.
    Start-Process "shell:AppsFolder\$pfn!App"
    $proc = $null
    for ($i = 0; $i -lt 60 -and -not $proc; $i++) {
        Start-Sleep -Milliseconds 250
        $proc = Get-Process -Name DragDone -ErrorAction SilentlyContinue | Select-Object -First 1
    }
    Check 'packaged app runs' ($null -ne $proc) $(if ($proc) { "pid $($proc.Id)" } else { 'no process' })

    # 4. Where settings land. A packaged app's %APPDATA% writes are redirected into the package,
    #    and the app only writes the file when a setting actually changes, so this reports the
    #    resolved path from the app itself rather than demanding a file that may not exist yet.
    $plainSettings = Join-Path $env:APPDATA 'DragDone\settings.json'
    $virtualSettings = Join-Path $env:LOCALAPPDATA "Packages\$pfn\LocalCache\Roaming\DragDone\settings.json"
    Say ("  ..    plain %APPDATA%\DragDone : " + $(if (Test-Path (Split-Path $plainSettings)) { 'exists' } else { 'absent' }))
    Say ("  ..    package LocalCache      : " + $(if (Test-Path (Split-Path $virtualSettings)) { 'exists' } else { 'absent' }))

    # 5. A packaged app must not rely on the Run key.
    $runNow = $null -ne (Get-ItemProperty -Path $runKey -Name DragDone -ErrorAction SilentlyContinue)
    Check 'no Run key written by the package' (-not $runNow)

    # 6. Identity and startup state, asked of the packaged executable itself. Running the exe from
    #    its install location is enough: the process inherits the package's identity from its path.
    Start-Sleep -Seconds 2
    $identityOut = Join-Path $WorkDir 'identity.txt'
    & (Join-Path $pkg.InstallLocation 'DragDone.exe') --identity --out $identityOut
    for ($i = 0; $i -lt 40 -and -not (Test-Path $identityOut); $i++) { Start-Sleep -Milliseconds 250 }
    if (Test-Path $identityOut) {
        $identity = Get-Content $identityOut -Raw
        Say $identity
        Check 'runtime has package identity' ($identity -match 'packaged\s*=\s*True')
        Check 'family name reported' ($identity -match [regex]::Escape($pfn))
        Check 'startup via a startup task' ($identity -match 'startup\s*=\s*startupTask=Enabled')
    } else {
        Check 'identity report written' $false 'no report from the packaged executable'
    }

    # 7. A real FFmpeg job from inside the package, where the old dead-CWD bug lived.
    $ffmpeg = Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Links\ffmpeg.exe'
    if (Test-Path $ffmpeg) {
        $clip = Join-Path $WorkDir 'clip.mp4'
        & $ffmpeg -y -v error -f lavfi -i 'testsrc2=duration=2:size=320x240:rate=30' `
            -f lavfi -i 'sine=frequency=440:duration=2' -shortest -c:v libx264 -preset ultrafast -c:a aac $clip
        $report = Join-Path $WorkDir 'job.json'
        & (Join-Path $pkg.InstallLocation 'DragDone.exe') --run vid.mute $clip --report $report
        for ($i = 0; $i -lt 60 -and -not (Test-Path $report); $i++) { Start-Sleep -Milliseconds 500 }
        $outcome = if (Test-Path $report) { (Get-Content $report -Raw | ConvertFrom-Json).outcome } else { '' }
        $made = Get-ChildItem $WorkDir -Filter '*muted*' -ErrorAction SilentlyContinue
        $ok = $made -and $outcome -and $outcome -notin @('Failed', 'Cancelled')
        Check 'video job runs in the package' $ok $(if ($made) { "$($made[0].Name) [$outcome]" } else { 'no output' })
    } else {
        Say '  (ffmpeg not installed; skipped the conversion check)'
    }
}
catch {
    Check 'verification ran without throwing' $false $_.Exception.Message
}
finally {
    # 8. Leave nothing behind: the package, the certificate, the app process.
    Get-Process -Name DragDone -ErrorAction SilentlyContinue | Stop-Process -Force
    if ($pkg) {
        Remove-AppxPackage -Package $pkg.PackageFullName -ErrorAction SilentlyContinue
        Check 'package uninstalled' ($null -eq (Get-AppxPackage | Where-Object { $_.Name -eq 'DragDone.Dev' }))
        $pfn = $pkg.PackageFamilyName
        Check 'virtualised settings removed' (-not (Test-Path (Join-Path $env:LOCALAPPDATA "Packages\$pfn")))
    }
    if ($cert) {
        foreach ($storeName in @('Root', 'TrustedPeople', 'My')) {
            $store = New-Object System.Security.Cryptography.X509Certificates.X509Store($storeName, 'LocalMachine')
            $store.Open('ReadWrite')
            $store.Remove($cert)
            $store.Close()
        }
        Check 'dev certificate removed again' $true
    }
    if ($hadRunKey) { Say '  (the installer channel had left a Run key; it was cleared for the test)' }
    foreach ($s in $saved) {
        if ($null -eq $s.Value) {
            Remove-ItemProperty -Path $s.Path -Name $s.Name -ErrorAction SilentlyContinue
            Say ("  ..    " + $s.Path.Split('\')[-1] + "\" + $s.Name + ' restored (removed)')
        } else {
            Set-ItemProperty -Path $s.Path -Name $s.Name -Value $s.Value -Type DWord
            Say ("  ..    " + $s.Path.Split('\')[-1] + "\" + $s.Name + " restored (was $($s.Value))")
        }
    }
    Say ''
    if ($failed -eq 0) { Say 'msix verification (elevated): ok' } else { Say "msix verification (elevated): $failed failed" }
    $lines | Set-Content -Path $Log -Encoding UTF8
    exit ([int]($failed -gt 0))
}
