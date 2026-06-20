# Revit Copilot — installer build script (run on Windows with .NET 8 SDK).
#
#   powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1 -Version 0.0.8
#
# Builds the OTA layout with Inno Setup: BinaLoader (goes into Revit Addins
# folders) plus the seed plugin build (goes into
# %LocalAppData%\Bina\RevitSync\versions\<ver>). CI does the same thing in
# .github/workflows/release.yml.
#
# Optional: pass -Sign to code-sign the EXE (needs an EV/OV cert in the
# machine store; without signing, SmartScreen shows an "unknown publisher"
# warning but the install still works).
#
#   installer\build-installer.ps1 -Version 0.0.8 -Sign -Thumbprint <cert-thumbprint>

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
$iss       = Join-Path $repo "installer\RevitCopilot.iss"
$exe       = Join-Path $repo "RevitCopilot-$Version-setup.exe"

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

# Inno Setup compiler.
$iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
if (-not (Test-Path $iscc)) {
    throw "Inno Setup 6 not found ($iscc). Install: winget install JRSoftware.InnoSetup"
}

Write-Host "==> Building installer EXE..." -ForegroundColor Cyan
& $iscc $iss /DAppVersion=$Version "/DLoaderDir=$loaderDir" "/DPluginDir=$pluginDir" "/O$repo"
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }

if ($Sign) {
    if (-not $Thumbprint) { throw "-Sign requires -Thumbprint <cert-thumbprint>" }
    Write-Host "==> Signing EXE..." -ForegroundColor Cyan
    signtool sign /sha1 $Thumbprint /tr $TimestampUrl /td sha256 /fd sha256 $exe
}

Write-Host "==> Done: $exe" -ForegroundColor Green
Write-Host "Install (per-user, no admin):  double-click $(Split-Path -Leaf $exe)" -ForegroundColor Green
Write-Host "Silent (IT push):              $(Split-Path -Leaf $exe) /VERYSILENT" -ForegroundColor Green
