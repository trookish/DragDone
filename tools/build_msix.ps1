<#
.SYNOPSIS
    Builds the MSIX package for the Microsoft Store channel.

.DESCRIPTION
    Publishes DragDone self-contained for win-x64, lays it out with AppxManifest.xml and the tile
    assets, then packs it with MakeAppx. The installer channel (tools/build_installer.ps1) is
    untouched: both channels build from the same source.

    Unsigned output is what the Store wants — Microsoft re-signs the package during publishing.
    For a local install test, pass -Sign to sign it with a self-signed certificate and trust it.

.EXAMPLE
    pwsh tools/build_msix.ps1
    pwsh tools/build_msix.ps1 -Sign
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$IdentityFile = '',
    [string]$OutDir = '',
    # Sign with a self-signed certificate so the package can be installed on this machine.
    [switch]$Sign,
    # Sign with your own certificate instead (Store submission does not need this).
    [string]$Pfx = '',
    [string]$PfxPassword = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $IdentityFile) { $IdentityFile = Join-Path $root 'packaging\identity.json' }
if (-not $OutDir) { $OutDir = Join-Path $root 'artifacts' }

function Find-SdkTool([string]$name) {
    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $found = Get-ChildItem -Path $kitsRoot -Filter $name -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } |
        Sort-Object { [version]($_.Directory.Parent.Name) } -Descending |
        Select-Object -First 1
    if (-not $found) { throw "$name not found under $kitsRoot. Install the Windows 10/11 SDK." }
    return $found.FullName
}

$makeappx = Find-SdkTool 'makeappx.exe'
$signtool = if ($Sign -or $Pfx) { Find-SdkTool 'signtool.exe' } else { $null }

# Version comes from the project so both channels always agree.
[xml]$proj = Get-Content (Join-Path $root 'src\DragDone\DragDone.csproj')
$version = $proj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'Version missing from DragDone.csproj' }
$parts = $version.Split('.')
while ($parts.Count -lt 4) { $parts += '0' }
$packageVersion = ($parts[0..3] -join '.')

$identity = Get-Content $IdentityFile -Raw | ConvertFrom-Json
$layout = Join-Path $root ("artifacts\msix-layout\$packageVersion")
$out = Join-Path $OutDir ("DragDone-$version-$($identity.architecture).msix")

if (Test-Path $layout) { Remove-Item $layout -Recurse -Force }
New-Item -ItemType Directory -Path $layout -Force | Out-Null
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

Write-Host "publishing self-contained win-x64..." -ForegroundColor Cyan
& dotnet publish (Join-Path $root 'src\DragDone\DragDone.csproj') `
    -c $Configuration -r win-x64 --self-contained true `
    -p:DebugType=none -p:GenerateDocumentationFile=false `
    -o $layout
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

Copy-Item (Join-Path $root 'packaging\Assets') -Destination $layout -Recurse -Force

$manifest = Get-Content (Join-Path $root 'packaging\AppxManifest.xml') -Raw
$tokens = @{
    '__IDENTITY_NAME__'        = $identity.identityName
    '__PUBLISHER__'            = $identity.publisher
    '__PUBLISHER_DISPLAY_NAME__' = $identity.publisherDisplayName
    '__DISPLAY_NAME__'         = $identity.displayName
    '__DESCRIPTION__'          = $identity.description
    '__ARCHITECTURE__'         = $identity.architecture
    '__MIN_VERSION__'          = $identity.minVersion
    '__MAX_VERSION__'          = $identity.maxVersionTested
    '__VERSION__'              = $packageVersion
}
foreach ($key in $tokens.Keys) { $manifest = $manifest.Replace($key, $tokens[$key]) }
if ($manifest -match '__[A-Z_]+__') { throw "unfilled manifest tokens: $($Matches[0])" }
$utf8 = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText((Join-Path $layout 'AppxManifest.xml'), $manifest, $utf8)

Write-Host "packing $out..." -ForegroundColor Cyan
& $makeappx pack /d $layout /p $out /o
if ($LASTEXITCODE -ne 0) { throw "makeappx failed ($LASTEXITCODE)" }

if ($Sign -or $Pfx) {
    if (-not $Pfx) {
        $subject = $identity.publisher
        $existing = Get-ChildItem Cert:\CurrentUser\My |
            Where-Object { $_.Subject -eq $subject -and $_.HasPrivateKey } |
            Sort-Object NotAfter -Descending | Select-Object -First 1
        if ($existing) {
            $cert = $existing
            Write-Host "reusing the dev certificate ($($cert.Thumbprint))..." -ForegroundColor Cyan
        } else {
            Write-Host "creating a self-signed certificate for '$subject'..." -ForegroundColor Cyan
            $cert = New-SelfSignedCertificate -Type Custom -Subject $subject `
                -KeyUsage DigitalSignature -FriendlyName 'DragDone MSIX (dev)' `
                -CertStoreLocation 'Cert:\CurrentUser\My' `
                -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}') `
                -NotAfter (Get-Date).AddYears(3)
        }
        $password = ConvertTo-SecureString -String 'dragdone' -Force -AsPlainText
        $Pfx = Join-Path $OutDir 'DragDone-dev.pfx'
        Export-PfxCertificate -Cert $cert -FilePath $Pfx -Password $password | Out-Null
        $PfxPassword = 'dragdone'
        # Trust it for the current user only, which is enough for Add-AppxPackage. A self-signed
        # certificate is its own root, so it has to go into Root as well as TrustedPeople or the
        # deployment fails with 0x800B0109 "root certificate of the signature must be trusted".
        foreach ($storeName in @('Root', 'TrustedPeople')) {
            $store = New-Object System.Security.Cryptography.X509Certificates.X509Store($storeName, 'CurrentUser')
            $store.Open('ReadWrite')
            $store.Add($cert)
            $store.Close()
        }
        Write-Host "  certificate thumbprint: $($cert.Thumbprint)" -ForegroundColor DarkGray
    }
    Write-Host "signing..." -ForegroundColor Cyan
    & $signtool sign /fd SHA256 /f $Pfx /p $PfxPassword $out
    if ($LASTEXITCODE -ne 0) { throw "signtool failed ($LASTEXITCODE)" }
}

$size = [math]::Round((Get-Item $out).Length / 1MB, 1)
Write-Host ""
Write-Host "package: $out ($size MB)" -ForegroundColor Green
Write-Host "identity: $($identity.identityName) / $($identity.publisher) / $packageVersion"
if (-not ($Sign -or $Pfx)) {
    Write-Host "unsigned: upload this to Partner Center, which re-signs it. Use -Sign to install locally." -ForegroundColor DarkGray
}
