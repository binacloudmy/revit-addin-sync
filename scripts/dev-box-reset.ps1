# dev-box-reset.ps1 — one command to put a Revit box into a KNOWN addin state.
#
# Born from the 2026-08-18 archaeology session: the fleet loader
# (BinaSync.addin) and the dev build silently sabotage each other (the
# PostBuild collision guard skips deploying the dev manifest whenever the
# loader manifest exists), OTA versions\ copies shadow freshly-built DLLs,
# and persisted config.json URL overrides (stale ngrok tunnels, LAN login
# pages, AllowNgrok flags) outlive every rebuild — an hour of ghost-hunting
# that this script replaces with one line.
#
#   pwsh scripts/dev-box-reset.ps1                          # DevColocate (default)
#   pwsh scripts/dev-box-reset.ps1 -Mode Staging            # UAT: cloud staging, direct-load build
#   pwsh scripts/dev-box-reset.ps1 -Mode Production         # revert box to the installed fleet addin
#   pwsh scripts/dev-box-reset.ps1 -EngineSecret my-secret  # override the colocate secret
#
# Modes:
#   DevColocate — dev daily driver. Fleet manifests disabled, versions\
#                 purged, config.json = engine trio (EngineMode +
#                 localhost:48810), dev build deployed with -c Staging so
#                 AUTH/LOGIN go to the staging cloud while AI runs on the
#                 local engine. Pair with scripts/start-engine.ps1 in
#                 bina-ai (staging -DatabaseUrl + -EmbedderKey for RAG).
#   Staging     — behave like a staging-fleet machine but from source:
#                 same cleanup, EngineMode off, -c Staging build direct-load.
#   Production  — hand the box BACK to the installed fleet addin:
#                 re-enable BinaSync manifests, remove dev manifests, purge
#                 versions\ (the fleet OTA re-stages cleanly), strip engine
#                 fields. NEVER builds — production bits come signed from
#                 the release pipeline, not from a dev checkout.
#
# Always preserved in config.json: login/device tokens (wiping them would
# force the whole browser-login dance again). Always stripped: the poison
# URL overrides (BaseUrl / ApiBaseUrl / LoginWebUrl / LoginUrl /
# UpdateFeedUrl / AllowNgrok* / AllowBackendOverride).
param(
    [ValidateSet("DevColocate", "Staging", "Production")]
    [string]$Mode = "DevColocate",
    [string]$EngineSecret = "bina-dev-123",
    [int]$EnginePort = 48810
)

$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent

# ── Guard: Revit must be closed (it rewrites config.json on exit, which is
# exactly how hand-edits kept "not sticking") ────────────────────────────────
if (Get-Process -Name "Revit" -ErrorAction SilentlyContinue) {
    throw "Revit is running — close it first. (It saves config.json on exit and would overwrite this reset.)"
}

$addinRoots = @(
    (Join-Path $env:APPDATA "Autodesk\Revit\Addins"),
    (Join-Path $env:PROGRAMDATA "Autodesk\Revit\Addins")
)
$syncDir    = Join-Path $env:APPDATA "RevitWebAppSync"
$configPath = Join-Path $syncDir "config.json"
# OTA staging lives under LOCALAPPDATA\Bina\RevitSync (verified on-box
# 2026-08-18: the old APPDATA\RevitWebAppSync\versions guess was a no-op —
# three staged builds survived the "purge"). Old path kept as a defensive
# second target in case any legacy install ever used it.
$otaRoot     = Join-Path $env:LOCALAPPDATA "Bina\RevitSync"
$versionsDirs = @(
    (Join-Path $otaRoot "versions"),
    (Join-Path $syncDir "versions")
)
$engineDir   = Join-Path $otaRoot "engine"

function Disable-Manifests([string]$pattern) {
    foreach ($root in $addinRoots) {
        if (-not (Test-Path $root)) { continue }
        Get-ChildItem $root -Recurse -Filter $pattern -ErrorAction SilentlyContinue | ForEach-Object {
            try {
                # Idempotent: a .disabled twin already there means an earlier
                # reset ran — just remove the live one, never stack suffixes.
                if (Test-Path ($_.FullName + ".disabled")) {
                    Remove-Item $_.FullName -Force
                    Write-Host "  removed (already disabled once): $($_.FullName)"
                } else {
                    Rename-Item $_.FullName ($_.Name + ".disabled") -ErrorAction Stop
                    Write-Host "  disabled: $($_.FullName)"
                }
            } catch {
                Write-Warning "  could not disable $($_.FullName) (admin needed for ProgramData?) — $_"
            }
        }
    }
}

function Enable-Manifests([string]$pattern) {
    foreach ($root in $addinRoots) {
        if (-not (Test-Path $root)) { continue }
        Get-ChildItem $root -Recurse -Filter ($pattern + ".disabled") -ErrorAction SilentlyContinue | ForEach-Object {
            try {
                Rename-Item $_.FullName ($_.Name -replace "\.disabled$", "") -ErrorAction Stop
                Write-Host "  re-enabled: $($_.FullName)"
            } catch {
                Write-Warning "  could not re-enable $($_.FullName) — $_"
            }
        }
    }
}

function Remove-Manifests([string]$pattern) {
    foreach ($root in $addinRoots) {
        if (-not (Test-Path $root)) { continue }
        Get-ChildItem $root -Recurse -Filter $pattern -ErrorAction SilentlyContinue | ForEach-Object {
            try {
                Remove-Item $_.FullName -Force
                Write-Host "  removed: $($_.FullName)"
            } catch {
                Write-Warning "  could not remove $($_.FullName) — $_"
            }
        }
    }
}

# ── 1. OTA-staged copies always go: they shadow whatever should load next ──
foreach ($vd in $versionsDirs) {
    if (Test-Path $vd) {
        Remove-Item $vd -Recurse -Force
        Write-Host "purged: $vd"
    }
}
# DevColocate runs the engine from SOURCE on 48810 — a staged engine bundle
# that ever auto-spawns would contend for the port. Remove it; the fleet
# updater re-stages it whenever the box goes back to Staging/Production.
if ($Mode -eq "DevColocate" -and (Test-Path $engineDir)) {
    Remove-Item $engineDir -Recurse -Force
    Write-Host "purged staged engine: $engineDir"
}

# ── 2. Manifests per mode ──────────────────────────────────────────────────
Write-Host "manifests ($Mode):"
if ($Mode -eq "Production") {
    Enable-Manifests  "BinaSync.addin"
    Remove-Manifests  "RevitWebAppSync.addin"       # dev direct-load out of the way
} else {
    Disable-Manifests "BinaSync.addin"              # fleet loader out of the way
}

# ── 3. config.json surgery: strip poison, keep tokens ──────────────────────
$poisonKeys = @("BaseUrl", "ApiBaseUrl", "LoginWebUrl", "LoginUrl", "UpdateFeedUrl",
                "AllowNgrokAIBaseUrl", "AllowNgrokApiBaseUrl", "AllowBackendOverride")
$cfg = @{}
if (Test-Path $configPath) {
    try { $cfg = Get-Content $configPath -Raw | ConvertFrom-Json -AsHashtable } catch { $cfg = @{} }
}
foreach ($k in $poisonKeys) { $cfg.Remove($k) | Out-Null }

if ($Mode -eq "DevColocate") {
    $cfg["EngineMode"]   = $true
    $cfg["EngineSecret"] = $EngineSecret
    $cfg["AIBaseUrl"]    = "http://localhost:$EnginePort"
} else {
    $cfg["EngineMode"] = $false
    $cfg.Remove("EngineSecret") | Out-Null
    $cfg.Remove("AIBaseUrl")    | Out-Null
}
New-Item -ItemType Directory -Force -Path $syncDir | Out-Null
$cfg | ConvertTo-Json -Depth 5 | Set-Content $configPath
Write-Host "config.json reset for $Mode (tokens preserved): $configPath"

# ── 4. Build + deploy (never for Production) ───────────────────────────────
if ($Mode -ne "Production") {
    Write-Host "building -c Staging (staging auth/login; DevColocate overrides AI to the local engine)..."
    Push-Location $repo
    dotnet build RevitWebAppSync.csproj -c Staging
    if ($LASTEXITCODE -ne 0) { Pop-Location; throw "build failed" }
    Pop-Location
}

# ── 5. Next steps ──────────────────────────────────────────────────────────
Write-Host ""
Write-Host "── $Mode ready ──────────────────────────────────────────" -ForegroundColor Green
switch ($Mode) {
    "DevColocate" {
        Write-Host @"
1. Start the engine (bina-ai repo):
     pwsh scripts/start-engine.ps1 -Secret $EngineSecret ``
       -DatabaseUrl "<DATABASE_URL from bina-ai\.env.staging>" ``
       -EmbedderKey "<OPENAI_API_KEY from bina-ai\.env>"
   Confirm it prints: RAG: ON
   (Langfuse: put LANGFUSE_* keys in bina-ai\.env once — traces flow direct.)
2. Start Revit → login goes to the STAGING landing page.
3. ALWAYS start a fresh chat after an upgrade; SAVE the model after
   verified mutations (an unsaved fill cost us 352 walls once).
Engine terminal = your live backend log; python edits hot-reload on rerun.
"@
    }
    "Staging" {
        Write-Host "Start Revit → staging cloud end-to-end (no local engine). Login = staging landing page."
    }
    "Production" {
        Write-Host "Start Revit → the installed fleet addin runs; it will re-stage its OTA version on the next update check."
    }
}