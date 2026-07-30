# Fully-signed release from a Windows box with the code-signing cert live
# (Certum SimplySign connected, or any cert signtool /a can find).
#
#   powershell -ExecutionPolicy Bypass -File installer\sign-release.ps1 -Tag v0.0.27-staging -Thumbprint <sha1>
#
# Pass -Thumbprint (cert in CurrentUser\My) so the installer also pre-trusts
# the publisher cert — without it Revit shows a one-time "Signed Add-In —
# Always Load?" prompt per user. Omit it to fall back to signtool /a
# auto-select (signs fine, but no pre-trust).
#
# Why this exists: CI (release.yml) has no cert, so its assets carry UNSIGNED
# RevitWebAppSync.dll payloads — Smart App Control / WDAC (Enforce) machines
# block them at load time (0x800711C7, 2026-07-21 incident). Signing the setup
# EXE afterwards does not touch the DLLs inside it or inside the OTA zip.
#
# This script rebuilds everything via build-installer.ps1 (which signs
# BinaLoader.dll, every RevitWebAppSync.dll payload, the setup EXE and the
# uninstaller BEFORE Inno packs them), then produces the OTA zip from the
# SIGNED payload tree, uploads the signed installer + OTA zip to TM One object
# storage, and flips the latest.json pointer the bina-ai /addin endpoints serve
# (sovereign delivery — no GitHub Release). This IS the go-live step.
#
# Requirements on this machine: git, dotnet 8 + 10 SDKs, Inno Setup 6, signtool
# in PATH, aws-cli, the cert available (connect SimplySign first), and TM One
# creds in env (TMONE_SERVER/BUCKET/ACCESS_KEY_ID/SECRET_ACCESS_KEY — the bucket
# you point at is the environment). Engine-channel releases: pass -EngineZip.

param(
    [Parameter(Mandatory = $true)][string]$Tag,   # v0.0.27 or v0.0.27-staging
    [string]$RepoDir = "",                        # default: the repo this script sits in
    [string]$TimestampUrl = "http://time.certum.pl",
    [string]$Thumbprint = "",                     # cert thumbprint (CurrentUser\My) — enables TrustedPublisher pre-trust
    [string]$EngineZip = "",
    [string]$GatewayUrl = "",
    [bool]$Mandatory = $true
)

$ErrorActionPreference = "Stop"

# Tags older than this script don't contain it, and copying it into the
# checkout would dirty the tree — so it runs from ANYWHERE: pass -RepoDir,
# or run the copy inside the repo (default). build-installer.ps1 is taken
# from the CHECKED-OUT tag's tree, never from next to this script.
$repo = if ($RepoDir) { (Resolve-Path $RepoDir).Path } else { Split-Path -Parent $PSScriptRoot }
Set-Location $repo
if (-not (Test-Path (Join-Path $repo "installer\build-installer.ps1"))) {
    throw "'$repo' has no installer\build-installer.ps1 — pass -RepoDir <repo clone>"
}

if ($Tag -notmatch '^v(\d+\.\d+\.\d+)(-staging)?$') {
    throw "Tag '$Tag' — want vMAJOR.MINOR.PATCH or vMAJOR.MINOR.PATCH-staging"
}
$version = $Matches[1]
# Staging tags build the Staging configuration (staging backend, updater
# disabled via .env.staging) exactly like release.yml's channel split.
$isStaging = [bool]$Matches[2]
$configuration = if ($isStaging) { "Staging" } else { "Release" }

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

# Cert selection. -Thumbprint is the preferred path: build-installer.ps1 can
# then export the public .cer and the installer pre-trusts the publisher
# (certutil -addstore TrustedPublisher), which removes Revit's one-time
# "Signed Add-In — Always Load?" prompt entirely. The SIGNTOOL_ARGS env path
# signs identically but build-installer cannot see the cert object, so no
# .cer export -> the prompt survives.
if ($Thumbprint) {
    if ($env:SIGNTOOL_ARGS) {
        # build-installer honors SIGNTOOL_ARGS over -SignCert — letting both
        # through would silently drop the pre-trust the caller asked for.
        throw "-Thumbprint and SIGNTOOL_ARGS are mutually exclusive — unset SIGNTOOL_ARGS to use pre-trust"
    }
    if (-not (Test-Path "Cert:\CurrentUser\My\$Thumbprint")) {
        throw "Thumbprint $Thumbprint not in Cert:\CurrentUser\My — connect SimplySign, then: Get-ChildItem Cert:\CurrentUser\My"
    }
} elseif (-not $env:SIGNTOOL_ARGS) {
    $env:SIGNTOOL_ARGS = "/a /fd SHA256 /tr $TimestampUrl /td SHA256"
    Write-Host "==> No -Thumbprint and SIGNTOOL_ARGS not set — '/a' auto-select, NO TrustedPublisher pre-trust (Revit shows the one-time Always Load prompt)" -ForegroundColor Yellow
}

# ─── Publish target, resolved BEFORE the build ──────────────────────────────
# Everything below depends only on the tag, so it costs nothing to run first —
# and running it first is the whole point. Two failures made that necessary:
#
#   * The immutability guard used to sit AFTER the two uploads it was meant to
#     gate. head-object inspected the keys the script had just written, so it
#     returned 0 every time and the throw fired on every run, including a clean
#     first release — leaving the pointer flip unreachable and the uploads
#     themselves completely unguarded. Exactly backwards: it could not prevent
#     an overwrite, only prevent a success.
#   * Credentials were validated at what was line 135, after build-installer.ps1
#     had already spent ~4 minutes producing a fully signed tree. release.yml
#     grew a whole preflight job to compensate for that ordering.
#
# Checked here, a republish or a missing variable costs a second and no
# SimplySign session.
#
# Creds live on THIS box only (never in GitHub secrets):
#   TMONE_SERVER  TMONE_BUCKET  TMONE_ACCESS_KEY_ID  TMONE_SECRET_ACCESS_KEY
#   (optional TMONE_REGION, REVIT_RELEASE_PREFIX; default prefix revit-copilot/releases)
# The bucket you point at IS the environment: staging tag -> staging creds/bucket,
# prod tag -> prod. Requires aws-cli (S3-compatible; OBS is S3-compatible).
foreach ($k in 'TMONE_SERVER','TMONE_BUCKET','TMONE_ACCESS_KEY_ID','TMONE_SECRET_ACCESS_KEY') {
    if (-not (Get-Item "env:$k" -ErrorAction SilentlyContinue)) {
        throw "$k not set — needed to publish to TM One"
    }
}
# Prefix and channel are DERIVED FROM THE TAG, not from the environment. The
# first staging release was published to the PROD prefix with its version
# recorded as plain "0.0.30" — a prod backend reading that bucket would have
# force-updated the whole fleet onto a staging build (mandatory defaults true).
# An env var you must remember to set is not a safety mechanism; the tag
# already carries the channel, so use it.
$channel  = if ($isStaging) { 'staging' } else { 'prod' }
$defaultPrefix = if ($isStaging) { 'revit-copilot/releases-staging' } else { 'revit-copilot/releases' }
$prefix   = if ($env:REVIT_RELEASE_PREFIX) { $env:REVIT_RELEASE_PREFIX.Trim('/') } else { $defaultPrefix }
# An override that contradicts the tag is a mistake, not an intention.
if ($env:REVIT_RELEASE_PREFIX -and $prefix -ne $defaultPrefix) {
    throw "REVIT_RELEASE_PREFIX '$prefix' contradicts tag $Tag (channel $channel, expected '$defaultPrefix') — unset it or fix the tag"
}
$endpoint = $env:TMONE_SERVER
$bucket   = $env:TMONE_BUCKET
$installerKey = "installers/RevitCopilot-$version-setup.exe"
$otaKey       = "ota/RevitWebAppSync-$version.zip"

# aws-cli reads AWS_* creds; SigV4 needs a region (derive from the OBS host,
# e.g. obs.my-kualalumpur-1.alphaedge… -> my-kualalumpur-1). when_required
# suppresses the CRC checksums Huawei OBS rejects (XAmzContentSHA256Mismatch).
$env:AWS_ACCESS_KEY_ID     = $env:TMONE_ACCESS_KEY_ID
$env:AWS_SECRET_ACCESS_KEY = $env:TMONE_SECRET_ACCESS_KEY
$env:AWS_REQUEST_CHECKSUM_CALCULATION = 'when_required'
if ($env:TMONE_REGION) { $env:AWS_DEFAULT_REGION = $env:TMONE_REGION }
elseif ($endpoint -match '^https?://obs\.([^.]+)\.') { $env:AWS_DEFAULT_REGION = $Matches[1] }
else { $env:AWS_DEFAULT_REGION = 'us-east-1' }

# Version keys must never be overwritten: rollback works by pointing
# latest.json back at an earlier version, which is only safe if the bytes under
# that version cannot have changed since. Also catches the honest mistake of
# re-running a release after a local edit.
Write-Host "==> Checking $prefix/ for an existing $version..." -ForegroundColor Cyan
foreach ($k in @($installerKey, $otaKey)) {
    aws s3api head-object --endpoint-url $endpoint --bucket $bucket --key "$prefix/$k" *> $null
    if ($LASTEXITCODE -eq 0) {
        throw "$prefix/$k already exists — version $version is published and immutable. Bump the version; do not overwrite."
    }
}

# PROD ONLY: the GitHub bridge must be possible BEFORE the signed build runs.
# Every installed 0.0.29 has github.com/.../releases/latest/download/version.json
# baked in and polls nothing else — a prod release that reaches TM One but not
# GitHub is invisible to the entire fleet, with no error anywhere: their update
# check keeps succeeding against a release that never changes. So a prod tag
# publishes to BOTH, and the GitHub half is validated here, where failing costs
# a second instead of a SimplySign session. (Staging has no GitHub half: the
# staging fleet polls the staging backend directly.)
if (-not $isStaging) {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        throw "gh CLI not on PATH — needed to publish the GitHub bridge release. winget install GitHub.cli, then: gh auth login"
    }
    gh auth status *> $null
    if ($LASTEXITCODE -ne 0) { throw "gh is not authenticated — run: gh auth login" }
    gh release view "v$version" *> $null
    if ($LASTEXITCODE -eq 0) {
        throw "GitHub release v$version already exists — same immutability rule as the version keys. Bump the version."
    }
}

# Full build: publishes all payload TFMs + loaders, SIGNS every addin DLL,
# builds + signs the installer EXE and uninstaller.
$buildArgs = @{ Version = $version; Configuration = $configuration; TimestampUrl = $TimestampUrl }
if ($Thumbprint) { $buildArgs.SignCert = $Thumbprint }
if ($EngineZip)  { $buildArgs.EngineZip  = $EngineZip }
if ($GatewayUrl) { $buildArgs.GatewayUrl = $GatewayUrl }
& (Join-Path $repo "installer\build-installer.ps1") @buildArgs

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

function S3Cp($localFile, $key, $contentType, $cacheControl) {
    # NB: not $args — that is PowerShell's automatic variable.
    $cpArgs = @('s3', 'cp', $localFile, "s3://$bucket/$prefix/$key",
                '--endpoint-url', $endpoint, '--content-type', $contentType)
    if ($cacheControl) { $cpArgs += @('--cache-control', $cacheControl) }
    aws @cpArgs
    if ($LASTEXITCODE -ne 0) { throw "aws s3 cp failed: $key" }
}

Write-Host "==> Uploading signed installer + OTA zip to TM One..." -ForegroundColor Cyan
S3Cp $setupExe $installerKey 'application/octet-stream' $null
S3Cp $zip      $otaKey       'application/zip'          $null

# Pointer LAST — the atomic go-live. Shape matches installer_release.read_pointer.
# Staging defaults to OPTIONAL: a forced restart mid-Revit-session is the wrong
# trade for a UAT build. Prod keeps the mandatory default, matching
# UpdateService, which treats a MISSING flag as mandatory. An explicit
# -Mandatory always wins.
if ($PSBoundParameters.ContainsKey('Mandatory')) { $mandatoryFlag = [bool]$Mandatory }
elseif ($isStaging)                              { $mandatoryFlag = $false }
else                                             { $mandatoryFlag = $true }

# Read the CURRENT pointer first so the new one records what to roll back to.
# Rollback is a pointer flip, and it should not depend on anyone remembering
# which version preceded this one.
$previous = $null
try {
    $prevJson = aws s3 cp "s3://$bucket/$prefix/latest.json" - --endpoint-url $endpoint 2>$null
    if ($LASTEXITCODE -eq 0 -and $prevJson) { $previous = ($prevJson | ConvertFrom-Json).version }
} catch { }

$pointer = [ordered]@{
    version          = $version
    channel          = $channel
    tag              = $Tag
    installer_key    = $installerKey
    ota_key          = $otaKey
    sha256           = $sha
    notes            = "BINA Sync $version"
    mandatory        = $mandatoryFlag
    previous_version = $previous
    published_at     = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
}
$pointerFile = Join-Path $repo 'latest.json'
$pointer | ConvertTo-Json -Depth 5 | Set-Content $pointerFile
Write-Host "==> Flipping latest.json pointer (go-live)..." -ForegroundColor Cyan
S3Cp $pointerFile 'latest.json' 'application/json' 'no-cache, must-revalidate'

# ─── GitHub bridge (PROD tags only) ─────────────────────────────────────────
# The installed 0.0.29 fleet polls releases/latest/download/version.json and
# nothing else, so a prod release must also land here — carrying the SIGNED
# bytes (CI's old auto-release shipped unsigned DLLs; WDAC blocked them at load,
# 0x800711C7, 2026-07-21). Once no 0.0.29 remains in telemetry this block can
# go; new builds poll the backend, which serves TM One.
#
# Two names are load-bearing, not conventions:
#   * the feed asset must be called exactly version.json — that filename is
#     baked into every 0.0.29's UPDATE_FEED_URL;
#   * feed keys are lowercase (version/url/sha256/notes/mandatory) — matching
#     UpdateService.UpdateFeed's JsonProperty attributes at v0.0.29.
# The url is the zip asset's public download URL, deterministic from tag+name,
# so the feed can be written before the upload happens.
if (-not $isStaging) {
    Write-Host "==> Publishing GitHub bridge release (the URL the 0.0.29 fleet polls)..." -ForegroundColor Cyan
    $ghZipUrl = "https://github.com/binacloudmy/revit-addin-sync/releases/download/$Tag/$zip"
    $ghFeed = [ordered]@{
        version   = $version
        url       = $ghZipUrl
        sha256    = $sha
        notes     = "BINA Sync $version"
        mandatory = $mandatoryFlag
    }
    $ghFeedFile = Join-Path $repo 'version.json'
    $ghFeed | ConvertTo-Json -Depth 5 | Set-Content $ghFeedFile
    gh release create $Tag $zip $setupExe $ghFeedFile `
        --title "BINA Sync $version" `
        --notes "Signed release. OTA payload + installer + update feed. Published to TM One and GitHub (bridge for pre-0.0.30 clients)." `
        --verify-tag
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed — TM One IS published ($version live there); fix gh and re-run ONLY the GitHub half by hand: gh release create $Tag $zip $setupExe $ghFeedFile --verify-tag" }
    Write-Host "  github releases/latest -> $version (fleet picks it up at next Revit launch)"
}

Write-Host ""
Write-Host "Done — $Tag published to TM One (bucket $bucket):" -ForegroundColor Green
Write-Host "  $prefix/$installerKey"
Write-Host "  $prefix/$otaKey (sha256 $sha)"
Write-Host "  $prefix/latest.json -> now serving $version"
