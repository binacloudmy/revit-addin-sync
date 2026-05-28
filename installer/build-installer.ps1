# Revit Copilot — MSI build script (run on Windows with .NET 8 SDK).
#
#   powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
#
# Optional: pass -Sign to code-sign the MSI (needs an EV/OV cert in the
# machine store; without signing, SmartScreen shows an "unknown publisher"
# warning but the install still works).
#
#   installer\build-installer.ps1 -Sign -Thumbprint <cert-thumbprint>

param(
    [string]$Configuration = "Release",
    [switch]$Sign,
    [string]$Thumbprint = "",
    [string]$TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

$publishDir = Join-Path $repo "bin\$Configuration\net8.0-windows"
$wxs        = Join-Path $repo "installer\RevitCopilot.wxs"
$msi        = Join-Path $repo "RevitCopilot.msi"

Write-Host "==> Building addin ($Configuration)..." -ForegroundColor Cyan
dotnet build -c $Configuration "RevitWebAppSync.csproj"

if (-not (Test-Path $publishDir)) {
    throw "Publish dir not found: $publishDir"
}

# The .addin manifest is not part of the normal build output — stage it next
# to the DLLs so the WiX <Files> wildcard ships it inside each version folder.
Write-Host "==> Staging .addin manifest..." -ForegroundColor Cyan
Copy-Item -Force (Join-Path $repo "RevitWebAppSync.addin") $publishDir

# Ensure the WiX toolset + UI extension are available.
if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
    Write-Host "==> Installing WiX global tool..." -ForegroundColor Cyan
    dotnet tool install --global wix
    $env:Path += ";$env:USERPROFILE\.dotnet\tools"
}
wix extension add -g WixToolset.UI.wixext 2>$null | Out-Null

Write-Host "==> Building MSI..." -ForegroundColor Cyan
wix build $wxs -ext WixToolset.UI.wixext -d "PublishDir=$publishDir" -o $msi

if ($Sign) {
    if (-not $Thumbprint) { throw "-Sign requires -Thumbprint <cert-thumbprint>" }
    Write-Host "==> Signing MSI..." -ForegroundColor Cyan
    signtool sign /sha1 $Thumbprint /tr $TimestampUrl /td sha256 /fd sha256 $msi
}

Write-Host "==> Done: $msi" -ForegroundColor Green
Write-Host "Install (per-user, no admin):  msiexec /i RevitCopilot.msi" -ForegroundColor Green
Write-Host "Silent (IT push):              msiexec /i RevitCopilot.msi /qn" -ForegroundColor Green
