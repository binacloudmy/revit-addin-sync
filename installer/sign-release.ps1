# Fully-signed release from a Windows box with the code-signing cert live
# (Certum SimplySign connected, or any cert signtool /a can find).
#
#   powershell -ExecutionPolicy Bypass -File installer\sign-release.ps1 -Tag v0.0.27-staging
#
# Why this exists: CI (release.yml) has no cert, so its assets carry UNSIGNED
# RevitWebAppSync.dll payloads — Smart App Control / WDAC (Enforce) machines
# block them at load time (0x800711C7, 2026-07-21 incident). Signing the setup
# EXE afterwards does not touch the DLLs inside it or inside the OTA zip.
#
# This script rebuilds everything via build-installer.ps1 (which signs
# BinaLoader.dll, every RevitWebAppSync.dll payload, the setup EXE and the
# uninstaller BEFORE Inno packs them), then produces the OTA zip + version.json
# from the SIGNED payload tree and clobber-uploads all assets onto the tag's
# GitHub release. Replaces every CI asset except auto-generated release notes.
#
# Requirements on this machine: git, gh (authed), dotnet 8 + 10 SDKs,
# Inno Setup 6, signtool in PATH, and the cert available (connect SimplySign
# first). Engine-channel releases: pass -EngineZip like build-installer.ps1.

param(
    [Parameter(Mandatory = $true)][string]$Tag,   # v0.0.27 or v0.0.27-staging
    [string]$TimestampUrl = "http://time.certum.pl",
    [string]$EngineZip = "",
    [string]$GatewayUrl = "",
    [bool]$Mandatory = $true
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

if ($Tag -notmatch '^v(\d+\.\d+\.\d+)(-staging)?$') {
    throw "Tag '$Tag' — want vMAJOR.MINOR.PATCH or vMAJOR.MINOR.PATCH-staging"
}
$version = $Matches[1]
# Staging tags build the Staging configuration (staging backend, updater
# disabled via .env.staging) exactly like release.yml's channel split.
$configuration = if ($Matches[2]) { "Staging" } else { "Release" }

# Refuse to build anything but the tag's exact tree — a dirty or drifted
# checkout would ship bytes that don't match the tag.
git fetch --tags --quiet
$tagSha = git rev-list -n 1 $Tag
if (-not $tagSha) { throw "Tag '$Tag' not found (after git fetch --tags)" }
$headSha = git rev-parse HEAD
if ($headSha -ne $tagSha) {
    throw "HEAD ($headSha) is not tag $Tag ($tagSha) — run: git checkout $Tag"
}
if (git status --porcelain) {
    throw "Working tree is dirty — stash or discard changes before a release build"
}

# Cert: honor a caller-set SIGNTOOL_ARGS; default to /a (best cert in store —
# with SimplySign connected that is the Certum cert).
if (-not $env:SIGNTOOL_ARGS) {
    $env:SIGNTOOL_ARGS = "/a /fd SHA256 /tr $TimestampUrl /td SHA256"
    Write-Host "==> SIGNTOOL_ARGS not set — using '/a' auto-select (connect SimplySign first)" -ForegroundColor Yellow
}

# Full build: publishes all payload TFMs + loaders, SIGNS every addin DLL,
# builds + signs the installer EXE and uninstaller.
$buildArgs = @{ Version = $version; Configuration = $configuration; TimestampUrl = $TimestampUrl }
if ($EngineZip)  { $buildArgs.EngineZip  = $EngineZip }
if ($GatewayUrl) { $buildArgs.GatewayUrl = $GatewayUrl }
& (Join-Path $PSScriptRoot "build-installer.ps1") @buildArgs

$setupExe = Join-Path $repo "RevitCopilot-$version-setup.exe"
if (-not (Test-Path $setupExe)) { throw "build-installer.ps1 did not produce $setupExe" }

# OTA zip from the SIGNED payload tree. Strip pdbs (CI parity) and the
# .complete seed marker — the updater writes its own only after a verified
# extract; shipping one would bless half-staged folders.
$pluginDir = Join-Path $repo "artifacts\plugin"
Get-ChildItem $pluginDir -Recurse -Filter *.pdb | Remove-Item -ErrorAction SilentlyContinue
Remove-Item (Join-Path $pluginDir ".complete") -ErrorAction SilentlyContinue

Write-Host "==> Verifying payload signatures..." -ForegroundColor Cyan
$payloadDlls = Get-ChildItem $pluginDir -Recurse -Filter "RevitWebAppSync.dll"
if (-not $payloadDlls) { throw "no RevitWebAppSync.dll found under artifacts\plugin" }
foreach ($dll in $payloadDlls + (Get-Item $setupExe)) {
    signtool verify /pa /q $dll.FullName
    if ($LASTEXITCODE -ne 0) { throw "signature verify failed: $($dll.FullName)" }
}

$zip = "RevitWebAppSync-$version.zip"
Remove-Item $zip -ErrorAction SilentlyContinue
Compress-Archive -Path "$pluginDir\*" -DestinationPath $zip
$sha = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()

# Update feed the addin polls. Note: url carries the FULL tag — release.yml
# writes the numeric-only path, which is broken for -staging tags (harmless
# there only because staging builds disable the updater).
$repoSlug = (git remote get-url origin) -replace '.*[:/]([^/]+/[^/.]+)(\.git)?$', '$1'
$feed = [ordered]@{
    version   = $version
    url       = "https://github.com/$repoSlug/releases/download/$Tag/$zip"
    sha256    = $sha
    notes     = "BINA Sync $version"
    mandatory = $Mandatory
}
$feed | ConvertTo-Json -Depth 5 | Set-Content version.json

Copy-Item -Force $setupExe (Join-Path $repo "RevitCopilotSetup.exe")

Write-Host "==> Uploading signed assets onto release $Tag (clobber)..." -ForegroundColor Cyan
gh release upload $Tag $zip version.json $setupExe RevitCopilotSetup.exe --clobber
if ($LASTEXITCODE -ne 0) { throw "gh release upload failed" }

Write-Host ""
Write-Host "Done — $Tag now serves fully-signed assets:" -ForegroundColor Green
Write-Host "  $zip (sha256 $sha)"
Write-Host "  RevitCopilot-$version-setup.exe + RevitCopilotSetup.exe"
Write-Host "  version.json"
