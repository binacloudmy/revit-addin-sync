# Revit Copilot — MSI build script (run on Windows with .NET 8 SDK).
#
#   powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1 -Version 0.0.1
#
# Builds the OTA layout: BinaLoader (goes into Revit Addins folders) plus the
# seed plugin build (goes into %LocalAppData%\Bina\RevitSync\versions\<ver>).
# CI does the same thing in .github/workflows/release.yml.
#
# Optional: pass -Sign to code-sign the MSI (needs an EV/OV cert in the
# machine store; without signing, SmartScreen shows an "unknown publisher"
# warning but the install still works).
#
#   installer\build-installer.ps1 -Version 0.0.1 -Sign -Thumbprint <cert-thumbprint>

param(
    [string]$Version = "0.0.1",
    [string]$Configuration = "Release",
    [switch]$Sign,
    [string]$Thumbprint = "",
    [string]$TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version '$Version' must be MAJOR.MINOR.PATCH"
}

$pluginDir = Join-Path $repo "artifacts\plugin"
$loaderDir = Join-Path $repo "artifacts\loader"
$wxs       = Join-Path $repo "installer\RevitCopilot.wxs"
$msi       = Join-Path $repo "RevitCopilot-$Version.msi"

Remove-Item -Recurse -Force (Join-Path $repo "artifacts") -ErrorAction SilentlyContinue

Write-Host "==> Publishing plugin $Version ($Configuration)..." -ForegroundColor Cyan
dotnet publish "RevitWebAppSync.csproj" -c $Configuration -o $pluginDir -p:Version=$Version

Write-Host "==> Publishing loader..." -ForegroundColor Cyan
dotnet publish "BinaLoader\BinaLoader.csproj" -c $Configuration -o $loaderDir -p:Version=$Version
Copy-Item -Force (Join-Path $repo "BinaLoader\BinaSync.addin") $loaderDir

# Loader metadata + completeness marker — the seed folder must look exactly
# like one staged by UpdateService.
@{ version = $Version; assembly = 'RevitWebAppSync.dll'; entryType = 'RevitWebAppSync.App' } |
    ConvertTo-Json | Set-Content (Join-Path $pluginDir "manifest.json")
Set-Content (Join-Path $pluginDir ".complete") $Version

# Ensure the WiX toolset + UI extension are available.
if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
    Write-Host "==> Installing WiX global tool..." -ForegroundColor Cyan
    dotnet tool install --global wix
    $env:Path += ";$env:USERPROFILE\.dotnet\tools"
}
wix extension add -g WixToolset.UI.wixext 2>$null | Out-Null

Write-Host "==> Building MSI..." -ForegroundColor Cyan
wix build $wxs -ext WixToolset.UI.wixext `
    -d "LoaderDir=$loaderDir" `
    -d "PluginDir=$pluginDir" `
    -d "SeedVersion=$Version" `
    -d "ProductVersion=$Version" `
    -o $msi

if ($Sign) {
    if (-not $Thumbprint) { throw "-Sign requires -Thumbprint <cert-thumbprint>" }
    Write-Host "==> Signing MSI..." -ForegroundColor Cyan
    signtool sign /sha1 $Thumbprint /tr $TimestampUrl /td sha256 /fd sha256 $msi
}

Write-Host "==> Done: $msi" -ForegroundColor Green
Write-Host "Install (per-user, no admin):  msiexec /i $(Split-Path -Leaf $msi)" -ForegroundColor Green
Write-Host "Silent (IT push):              msiexec /i $(Split-Path -Leaf $msi) /qn" -ForegroundColor Green
