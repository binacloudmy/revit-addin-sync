<#
.SYNOPSIS
  Dry-run of installer/fetch-engine-bundle.ps1's verification gate. No network:
  uses the -PointerFile / -ZipFile hooks with a synthetic bundle.

.DESCRIPTION
  Three cases, each the one production behaviour that would make it fail:

    1. pointer sha matches the bytes    -> present=true, version echoed
    2. pointer sha does NOT match       -> throws (the gate)
    3. pointer carries no engine fields -> present=false, no throw

  Plus: a pointer with a version that disagrees with the bundle's own
  engine-version.json must throw - the installer's DestDir is engine\<ver>\
  and EngineManager scans by that name.

  Run from the repo root:  installer\tests\fetch-engine-bundle.tests.ps1
  Exit code is the failure count.
#>
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$script = Join-Path $PSScriptRoot '..\fetch-engine-bundle.ps1'
$work = Join-Path ([System.IO.Path]::GetTempPath()) ("fetch-engine-tests-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$failures = 0

function Pass([string]$name) { Write-Host "PASS  $name" }
function Fail([string]$name, [string]$why) { $script:failures++; Write-Host "FAIL  $name`n      $why" }

# ---- synthetic bundle: engine-version.json + a payload file, zipped --------
$bundleSrc = Join-Path $work 'bundle-src'
New-Item -ItemType Directory -Path $bundleSrc | Out-Null
@{ engine_version = '9.9.9'; min_addin_version = '0.0.1' } | ConvertTo-Json | Set-Content (Join-Path $bundleSrc 'engine-version.json')
'@echo off' | Set-Content (Join-Path $bundleSrc 'run-engine.cmd')
$zip = Join-Path $work 'bina-engine-9.9.9.zip'
Compress-Archive -Path (Join-Path $bundleSrc '*') -DestinationPath $zip
$goodSha = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()

function Write-Pointer([string]$name, [hashtable]$fields) {
    $p = Join-Path $work "$name.json"
    $fields | ConvertTo-Json | Set-Content $p
    return $p
}

# ---- 1. good sha -> present ------------------------------------------------
try {
    $ptr = Write-Pointer 'good' @{ version = '0.0.58'; engine_version = '9.9.9'; engine_key = 'engine/x.zip'; engine_sha256 = $goodSha }
    $out = Join-Path $work 'out-good'
    $r = & $script -Prefix 'p' -OutDir $out -PointerFile $ptr -ZipFile $zip
    if ($r.present -eq 'true' -and $r.version -eq '9.9.9' -and $r.sha256 -eq $goodSha -and (Test-Path (Join-Path $out 'run-engine.cmd'))) {
        Pass 'good sha -> present=true, version echoed, bundle expanded'
    } else {
        Fail 'good sha' "unexpected result: $($r | ConvertTo-Json -Compress)"
    }
} catch { Fail 'good sha' $_.Exception.Message }

# ---- 2. bad sha -> throws --------------------------------------------------
try {
    $ptr = Write-Pointer 'bad' @{ version = '0.0.58'; engine_version = '9.9.9'; engine_key = 'engine/x.zip'; engine_sha256 = ('0' * 64) }
    $out = Join-Path $work 'out-bad'
    $null = & $script -Prefix 'p' -OutDir $out -PointerFile $ptr -ZipFile $zip
    Fail 'bad sha' 'did NOT throw - the gate is open'
} catch {
    if ($_.Exception.Message -match 'sha256 mismatch') { Pass 'bad sha -> throws with "sha256 mismatch"' }
    else { Fail 'bad sha' "threw the wrong thing: $($_.Exception.Message)" }
}

# ---- 3. no engine fields -> addin-only, no throw ----------------------------
try {
    $ptr = Write-Pointer 'none' @{ version = '0.0.58'; ota_key = 'ota/x.zip' }
    $out = Join-Path $work 'out-none'
    $r = & $script -Prefix 'p' -OutDir $out -PointerFile $ptr -ZipFile $zip
    if ($r.present -eq 'false') { Pass 'no engine fields -> present=false, no throw' }
    else { Fail 'no engine fields' "expected present=false, got $($r | ConvertTo-Json -Compress)" }
} catch { Fail 'no engine fields' "threw: $($_.Exception.Message)" }

# ---- 4. half a pointer -> throws -------------------------------------------
try {
    $ptr = Write-Pointer 'half' @{ version = '0.0.58'; engine_version = '9.9.9'; engine_key = 'engine/x.zip' }
    $out = Join-Path $work 'out-half'
    $null = & $script -Prefix 'p' -OutDir $out -PointerFile $ptr -ZipFile $zip
    Fail 'half pointer' 'did NOT throw'
} catch {
    if ($_.Exception.Message -match 'missing a field') { Pass 'half pointer -> throws' }
    else { Fail 'half pointer' "threw the wrong thing: $($_.Exception.Message)" }
}

# ---- 5. version disagrees with the bundle -> throws --------------------------
try {
    $ptr = Write-Pointer 'mislabel' @{ version = '0.0.58'; engine_version = '1.2.3'; engine_key = 'engine/x.zip'; engine_sha256 = $goodSha }
    $out = Join-Path $work 'out-mislabel'
    $null = & $script -Prefix 'p' -OutDir $out -PointerFile $ptr -ZipFile $zip
    Fail 'mislabelled bundle' 'did NOT throw'
} catch {
    if ($_.Exception.Message -match 'mislabelled') { Pass 'pointer/bundle version disagree -> throws' }
    else { Fail 'mislabelled bundle' "threw the wrong thing: $($_.Exception.Message)" }
}

Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
Write-Host ""
Write-Host "$(5 - $failures) passed, $failures failed"
exit $failures
