<#
.SYNOPSIS
  Fetch the engine bundle the release channel's latest.json points at, verify
  it, and expand it for the installer to seed.

.DESCRIPTION
  Every staging release before 2026-08-27 shipped addin-only: ENGINE_ZIP_URL
  was never set, RevitCopilot.iss skipped the engine entry via
  skipifsourcedoesntexist, and a fresh install had no engine on disk until an
  OTA cycle completed. Everything between install and that OTA failed silently
  - see docs/superpowers/specs/2026-08-27-engine-self-heal-design.md.

  This script makes the installer seed EXACTLY the engine the feed already
  serves. TM One's latest.json carries engine_version / engine_key /
  engine_sha256 (published by bina-ai's engine bundle flow and carried forward
  across addin pointer flips). We read that pointer, download the key, verify
  the sha, and expand into -OutDir. Same bytes on disk as the feed advertises,
  so UpdateService.CheckEngineAsync sees "engine up to date" and never
  re-downloads what the installer just laid down.

  FAILS LOUDLY. If the pointer names an engine and we cannot produce those
  exact bytes - download error, sha mismatch, manifest disagreement - the build
  stops. An installer that silently ships without the engine is the bug this
  exists to end. Only a pointer with NO engine fields is an addin-only build,
  and that is reported, not hidden.

.PARAMETER Prefix
  Bucket prefix for the channel, e.g. revit-copilot/releases-staging.

.PARAMETER OutDir
  Where the bundle is expanded (the installer's /DEngineDir).

.PARAMETER PointerFile
  Test hook: read latest.json from this path instead of the bucket.

.PARAMETER ZipFile
  Test hook: use this local zip instead of downloading engine_key.

.OUTPUTS
  Writes present/version/sha256/zip to $env:GITHUB_OUTPUT when set, and
  returns the same as an object for callers outside Actions.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Prefix,
    [Parameter(Mandatory)] [string]$OutDir,
    [string]$PointerFile,
    [string]$ZipFile
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

function Emit([hashtable]$Result) {
    if ($env:GITHUB_OUTPUT) {
        foreach ($k in $Result.Keys) { "$k=$($Result[$k])" >> $env:GITHUB_OUTPUT }
    }
    return [pscustomobject]$Result
}

function Get-Field($Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    $p = $Object.PSObject.Properties[$Name]
    if ($null -eq $p) { return $null }
    return $p.Value
}

# ---------------------------------------------------------------- pointer ----
$pointerJson = $null
if ($PointerFile) {
    $pointerJson = Get-Content -Raw -LiteralPath $PointerFile
} else {
    foreach ($k in 'TMONE_SERVER','TMONE_BUCKET','TMONE_ACCESS_KEY_ID','TMONE_SECRET_ACCESS_KEY') {
        if (-not (Get-Item "env:$k" -ErrorAction SilentlyContinue)) {
            throw "$k not set - needed to read the release pointer from TM One"
        }
    }
    # Same aws-cli conventions as publish-unsigned-staging.ps1: SigV4 needs a
    # region (derived from the OBS host); when_required suppresses the CRC
    # checksums Huawei OBS rejects. Load-bearing.
    $env:AWS_ACCESS_KEY_ID     = $env:TMONE_ACCESS_KEY_ID
    $env:AWS_SECRET_ACCESS_KEY = $env:TMONE_SECRET_ACCESS_KEY
    $env:AWS_REQUEST_CHECKSUM_CALCULATION = 'when_required'
    if (-not $env:AWS_RESPONSE_CHECKSUM_VALIDATION) { $env:AWS_RESPONSE_CHECKSUM_VALIDATION = 'when_required' }
    if ($env:TMONE_REGION) { $env:AWS_DEFAULT_REGION = $env:TMONE_REGION }
    elseif ($env:TMONE_SERVER -match '^https?://obs\.([^.]+)\.') { $env:AWS_DEFAULT_REGION = $Matches[1] }
    else { $env:AWS_DEFAULT_REGION = 'us-east-1' }

    $prefixClean = $Prefix.Trim('/')
    $pointerJson = aws s3 cp "s3://$env:TMONE_BUCKET/$prefixClean/latest.json" - --endpoint-url $env:TMONE_SERVER 2>&1
    if ($LASTEXITCODE -ne 0) {
        # No pointer at all = channel has never published. That is an
        # addin-only build by definition, not a failure.
        Write-Host "no latest.json under $prefixClean - addin-only release"
        return Emit @{ present = 'false' }
    }
}

$pointer = $pointerJson | ConvertFrom-Json
$engineVersion = [string](Get-Field $pointer 'engine_version')
$engineKey     = [string](Get-Field $pointer 'engine_key')
$engineSha     = [string](Get-Field $pointer 'engine_sha256')

if (-not $engineVersion -and -not $engineKey -and -not $engineSha) {
    Write-Host "pointer carries no engine channel - addin-only release"
    return Emit @{ present = 'false' }
}
# Half a pointer is a corrupt pointer. Refuse rather than guess.
if (-not $engineVersion -or -not $engineKey -or -not $engineSha) {
    throw "pointer names an engine but is missing a field (version='$engineVersion' key='$engineKey' sha='$engineSha') - refusing to build without it"
}
Write-Host "pointer names engine $engineVersion at $engineKey"

# ---------------------------------------------------------------- bundle -----
$zipPath = $ZipFile
if (-not $zipPath) {
    $zipPath = Join-Path ([System.IO.Path]::GetTempPath()) "bina-engine-$engineVersion.zip"
    $prefixClean = $Prefix.Trim('/')
    aws s3 cp "s3://$env:TMONE_BUCKET/$prefixClean/$engineKey" $zipPath --endpoint-url $env:TMONE_SERVER --only-show-errors
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $zipPath)) {
        throw "download of $engineKey failed - the pointer names an engine the bucket does not serve; refusing to build without it"
    }
}

$actualSha = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLower()
if ($actualSha -ne $engineSha.Trim().ToLower()) {
    throw "engine $engineVersion sha256 mismatch - pointer says $engineSha, bytes are $actualSha; refusing to seed corrupt or substituted bytes"
}

if (Test-Path -LiteralPath $OutDir) { Remove-Item -LiteralPath $OutDir -Recurse -Force }
Expand-Archive -LiteralPath $zipPath -DestinationPath $OutDir -Force

$manifestPath = Join-Path $OutDir 'engine-version.json'
if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw "engine-version.json not found at the root of the bundle - bad bundle"
}
$manifestVersion = [string](Get-Field (Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json) 'engine_version')
if ($manifestVersion -ne $engineVersion) {
    # The installer's DestDir is engine\<ver>\ and EngineManager scans by that
    # folder name; a disagreement here would seed a folder the runtime cannot
    # match to the feed.
    throw "pointer says engine $engineVersion but the bundle's engine-version.json says '$manifestVersion' - refusing to seed a mislabelled bundle"
}

Write-Host "engine $engineVersion verified (sha256 $actualSha) and expanded to $OutDir"
return Emit @{
    present = 'true'
    version = $engineVersion
    sha256  = $actualSha
    zip     = $zipPath
}
