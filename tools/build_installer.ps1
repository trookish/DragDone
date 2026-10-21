# Builds the DragDone installer: publishes the app self-contained, then compiles installer\DragDone.iss
# into a single setup executable under artifacts\.
#
#   pwsh tools/build_installer.ps1
#   pwsh tools/build_installer.ps1 -SkipPublish      # reuse artifacts\install-src
#
# Needs the .NET 10 SDK and Inno Setup 6 (winget install JRSoftware.InnoSetup).

param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$csproj = Join-Path $root "src/DragDone/DragDone.csproj"

$version = ([xml](Get-Content $csproj)).Project.PropertyGroup |
    ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> in $csproj" }

$source = Join-Path $root "artifacts/install-src"
if (-not $SkipPublish) {
    if (Test-Path $source) { Remove-Item $source -Recurse -Force }
    Write-Host "publishing $version ($Runtime, self-contained) ..."
    dotnet publish $csproj -c $Configuration -r $Runtime --self-contained true -o $source --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
}

$iscc = @(
    (Join-Path $env:LOCALAPPDATA "Programs/Inno Setup 6/ISCC.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6/ISCC.exe"),
    (Join-Path $env:ProgramFiles "Inno Setup 6/ISCC.exe")
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

if (-not $iscc) {
    throw "Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup"
}

Write-Host "compiling the installer with $iscc ..."
& $iscc "/DVersion=$version" (Join-Path $root "installer/DragDone.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }

Get-ChildItem (Join-Path $root "artifacts") -Filter "DragDone-*-setup.exe" |
    Select-Object Name, @{ n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } }
