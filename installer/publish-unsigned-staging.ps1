# Unsigned staging publish, straight from CI (no signing box).
#
#   powershell -File installer\publish-unsigned-staging.ps1 -Tag v0.0.31-staging -PayloadDir artifact
#
# Operator decision 2026-08-04: staging-channel releases publish to TM One
# WITHOUT signing, from GitHub-hosted CI — prod keeps the human-gated signing
# box (installer\sign-release.ps1, run via the sign-prod workflow job)
# completely untouched. This script is the staging-only counterpart: it does
# not build, does not sign, and never touches prod's prefix or pointer.
#
# Why this is safe for staging and would NOT be for prod:
#   * staging mandatory defaults FALSE — UpdateService never force-restarts a
#     tester's Revit session over an unsigned build (Services/UpdateService.cs);
#   * the staging fleet is a handful of known testers, not the whole drafter
#     population;
#   * Smart App Control / WDAC (Enforce) machines block an unsigned DLL at
#     load (0x800711C7, 2026-07-21 incident) — an accepted, known risk for
#     that population, never for prod.
# Prod feeds are mandatory:true and reach every drafter at their next Revit
# launch — that path stays fully human-gated (SimplySign session + signtool
# + `environment: production` approval). See .github/workflows/release.yml
# (sign-prod) and installer\sign-release.ps1.
#
# No rebuild, no re-sign: release.yml's "Zip payload + feed json" step already
# produces RevitWebAppSync-<version>.zip from the pdb-stripped
# artifacts/plugin tree, with no .complete seed marker (that marker is only
# written afterwards, in the later "Build installer" step) — byte-identical
# in shape to what installer\sign-release.ps1 zips from its own signed build,
# just unsigned. This script reuses that zip AS-IS rather than recomposing
# it; it never touches artifacts/plugin itself.
#
# -PublishInstaller (CI passes it): also publishes the setup EXE, so fresh
# staging installs land on the CURRENT version instead of whatever version
# the last SIGNED prod/staging release happened to be. This does NOT compile
# anything here — release.yml's "Build installer (Inno Setup EXE)" step (has
# existed since 2026-06-11, long before this script) already runs
# unconditionally for every tag, staging or not, and its output
# (RevitCopilot-<version>-setup.exe) is already globbed into the SAME
# `release-<version>` artifact this script's zip comes from — see the
# `path:` list on the `release` job's "Upload build artifacts" step. So the
# unsigned setup EXE this switch publishes is not rebuilt, just picked up
# from -PayloadDir and shipped as-is, exactly like the OTA zip above.
# Recompiling the .iss a second time on this job (installing Inno Setup here
# too) would just reproduce byte-identical output for no benefit and one
# more thing that can flake in CI — so this script never invokes ISCC.
# Without the switch, installer_key is still carried forward unchanged from
# the previous latest.json (the pre-existing behavior).
#
# Unsigned EXE reality, accepted for staging only (operator decision
# 2026-08-04): the setup EXE has no Authenticode signature, so Windows
# SmartScreen shows an "unrecognized app" warning on download/run — the same
# accepted risk as the unsigned OTA DLLs above, scoped to the staging
# tester population, never prod (prod installers only ever come from the
# signed sign-release.ps1 path).
#
# Requirements on the runner: aws-cli (present by default on windows-latest),
# and TM One creds in env (TMONE_SERVER/BUCKET/ACCESS_KEY_ID/SECRET_ACCESS_KEY
# — the bucket you point at is the environment; this script only ever
# resolves the STAGING prefix, enforced by the -Tag format check below).

param(
    [Parameter(Mandatory = $true)][string]$Tag,        # vMAJOR.MINOR.PATCH-staging — no other shape accepted
    [Parameter(Mandatory = $true)][string]$PayloadDir, # dir holding the downloaded CI artifact (release-<version>), containing RevitWebAppSync-<version>.zip
    [switch]$PublishInstaller,                         # also ship RevitCopilot-<version>-setup.exe from -PayloadDir (already built unsigned by release.yml)
    [bool]$Mandatory = $false                          # staging default; explicit -Mandatory always wins (same convention as sign-release.ps1)
)

$ErrorActionPreference = "Stop"

$repo = Split-Path -Parent $PSScriptRoot

# Staging only — refuse anything else outright. This is the one guard that
# makes "unsigned, straight from CI" safe: a prod tag must NEVER take this
# path, where there is no signing, no cert, and no human approval gate.
if ($Tag -notmatch '^v(\d+\.\d+\.\d+)-staging$') {
    throw "Tag '$Tag' — this script only publishes STAGING tags (want vMAJOR.MINOR.PATCH-staging). Prod publishes through installer\sign-release.ps1 on the signing box, never here."
}
$version = $Matches[1]
$channel = 'staging'

if (-not (Test-Path $PayloadDir)) {
    throw "-PayloadDir '$PayloadDir' does not exist"
}
$zip = Join-Path $PayloadDir "RevitWebAppSync-$version.zip"
if (-not (Test-Path $zip)) {
    throw "'$zip' not found under -PayloadDir '$PayloadDir' — expected release.yml's 'Zip payload + feed json' step to have produced it in the downloaded artifact"
}
$installerExe = $null
if ($PublishInstaller) {
    $installerExe = Join-Path $PayloadDir "RevitCopilot-$version-setup.exe"
    if (-not (Test-Path $installerExe)) {
        throw "-PublishInstaller was passed but '$installerExe' not found under -PayloadDir '$PayloadDir' — expected release.yml's 'Build installer (Inno Setup EXE)' step to have produced it in the downloaded artifact"
    }
}

# ─── Creds + target, resolved before anything else touches the network ─────
# Same ordering lesson as sign-release.ps1: check everything cheap first, so
# a missing secret costs nothing instead of a half-finished publish.
foreach ($k in 'TMONE_SERVER','TMONE_BUCKET','TMONE_ACCESS_KEY_ID','TMONE_SECRET_ACCESS_KEY') {
    if (-not (Get-Item "env:$k" -ErrorAction SilentlyContinue)) {
        throw "$k not set — needed to publish to TM One"
    }
}
# Prefix is DERIVED FROM THE TAG, not from the environment — same reasoning as
# sign-release.ps1: the tag already carries the channel (and this script only
# ever sees a staging tag, enforced above), so an override that contradicts
# it is a mistake, not an intention.
$defaultPrefix = 'revit-copilot/releases-staging'
$prefix = if ($env:REVIT_RELEASE_PREFIX) { $env:REVIT_RELEASE_PREFIX.Trim('/') } else { $defaultPrefix }
if ($env:REVIT_RELEASE_PREFIX -and $prefix -ne $defaultPrefix) {
    throw "REVIT_RELEASE_PREFIX '$prefix' contradicts tag $Tag (channel $channel, expected '$defaultPrefix') — unset it or fix the tag"
}
$endpoint = $env:TMONE_SERVER
$bucket   = $env:TMONE_BUCKET
$otaKey       = "ota/RevitWebAppSync-$version.zip"
# Exact same convention as sign-release.ps1's $installerKey — kept identical
# so a version's installer lands at the same key shape regardless of which
# script published it (signed via sign-release.ps1, or unsigned via this one).
$installerKey = "installers/RevitCopilot-$version-setup.exe"

# aws-cli reads AWS_* creds; SigV4 needs a region (derive from the OBS host,
# e.g. obs.my-kualalumpur-1.alphaedge… -> my-kualalumpur-1). when_required
# suppresses the CRC checksums Huawei OBS rejects (XAmzContentSHA256Mismatch)
# — load-bearing, same trap sign-release.ps1 and bina-ai's
# app/services/family_library.py both work around.
$env:AWS_ACCESS_KEY_ID     = $env:TMONE_ACCESS_KEY_ID
$env:AWS_SECRET_ACCESS_KEY = $env:TMONE_SECRET_ACCESS_KEY
$env:AWS_REQUEST_CHECKSUM_CALCULATION = 'when_required'
if (-not $env:AWS_RESPONSE_CHECKSUM_VALIDATION) { $env:AWS_RESPONSE_CHECKSUM_VALIDATION = 'when_required' }
if ($env:TMONE_REGION) { $env:AWS_DEFAULT_REGION = $env:TMONE_REGION }
elseif ($endpoint -match '^https?://obs\.([^.]+)\.') { $env:AWS_DEFAULT_REGION = $Matches[1] }
else { $env:AWS_DEFAULT_REGION = 'us-east-1' }

# Immutability guard — identical rule to sign-release.ps1: a version key must
# never be overwritten, so rollback (pointing latest.json back at an earlier
# version) stays safe. The OTA key is always checked; the installer key is
# only checked when -PublishInstaller is writing one (without the switch this
# script writes no installer_key of its own, same as before).
$keysToCheck = @($otaKey)
if ($PublishInstaller) { $keysToCheck += $installerKey }
foreach ($k in $keysToCheck) {
    Write-Host "==> Checking $prefix/$k ..." -ForegroundColor Cyan
    aws s3api head-object --endpoint-url $endpoint --bucket $bucket --key "$prefix/$k" *> $null
    if ($LASTEXITCODE -eq 0) {
        throw "$prefix/$k already exists — version $version is published and immutable. Bump the version; do not overwrite."
    }
}

$sha = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()

function S3Cp($localFile, $key, $contentType, $cacheControl) {
    # NB: not $args — that is PowerShell's automatic variable.
    $cpArgs = @('s3', 'cp', $localFile, "s3://$bucket/$prefix/$key",
                '--endpoint-url', $endpoint, '--content-type', $contentType)
    if ($cacheControl) { $cpArgs += @('--cache-control', $cacheControl) }
    aws @cpArgs
    if ($LASTEXITCODE -ne 0) { throw "aws s3 cp failed: $key" }
}

Write-Host "==> Uploading UNSIGNED OTA zip to TM One..." -ForegroundColor Cyan
S3Cp $zip $otaKey 'application/zip' $null

if ($PublishInstaller) {
    Write-Host "==> Uploading UNSIGNED installer EXE to TM One..." -ForegroundColor Cyan
    S3Cp $installerExe $installerKey 'application/octet-stream' $null
}

# Read the CURRENT pointer first: previous_version for rollback provenance
# (same as sign-release.ps1), AND installer_key — used as the carry-forward
# fallback when -PublishInstaller is NOT passed (this script writes no setup
# EXE of its own in that case; overwriting installer_key with $null would
# break fresh installs the moment this runs, so /addin/download keeps
# redirecting to the last SIGNED installer while OTA jumps ahead). When
# -PublishInstaller IS passed, the freshly-uploaded $installerKey wins
# instead — fresh installs then get the CURRENT version too.
$previous = $null
$carriedInstallerKey = $null
try {
    $prevJson = aws s3 cp "s3://$bucket/$prefix/latest.json" - --endpoint-url $endpoint 2>$null
    if ($LASTEXITCODE -eq 0 -and $prevJson) {
        $prevObj = $prevJson | ConvertFrom-Json
        $previous = $prevObj.version
        $carriedInstallerKey = $prevObj.installer_key
    }
} catch { }
$effectiveInstallerKey = if ($PublishInstaller) { $installerKey } else { $carriedInstallerKey }

# Pointer shape is IDENTICAL to sign-release.ps1's — same fields, same order,
# same construction.
$pointer = [ordered]@{
    version          = $version
    channel          = $channel
    tag              = $Tag
    installer_key    = $effectiveInstallerKey
    ota_key          = $otaKey
    sha256           = $sha
    notes            = "BINA Sync $version (unsigned, CI-published)"
    mandatory        = $Mandatory
    previous_version = $previous
    published_at     = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
}
$pointerFile = Join-Path $repo 'latest.json'
$pointer | ConvertTo-Json -Depth 5 | Set-Content $pointerFile
Write-Host "==> Flipping latest.json pointer (go-live, staging, UNSIGNED)..." -ForegroundColor Cyan
S3Cp $pointerFile 'latest.json' 'application/json' 'no-cache, must-revalidate'

Write-Host ""
Write-Host "Done — $Tag published to TM One (bucket $bucket), UNSIGNED:" -ForegroundColor Green
if ($PublishInstaller) {
    Write-Host "  $prefix/$installerKey"
}
Write-Host "  $prefix/$otaKey (sha256 $sha)"
if ($PublishInstaller) {
    Write-Host "  $prefix/latest.json -> now serving $version (installer_key updated to THIS version's unsigned EXE)"
} else {
    Write-Host "  $prefix/latest.json -> now serving $version (installer_key carried forward: $carriedInstallerKey)"
    Write-Host "  No setup EXE published — fresh installs still come from the last signed installer."
}
