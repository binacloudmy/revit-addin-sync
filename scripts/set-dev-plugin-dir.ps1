# Points BinaLoader at a local dev build instead of the installed release.
#
# BINA_SYNC_PLUGIN_DIR is applied to EVERY Revit year, so the folder must match
# the runtime of the Revit you actually launch, or the loader TFM-gates it
# ("dev override ... targets a runtime this host cannot load - skipped") and
# silently falls back to the installed build in %LOCALAPPDATA%\Bina\RevitSync.
#
#   Revit 2025 / 2026 / 2027 -> .NET 8   -> bin\Debug\net8.0-windows   (default)
#   Revit 2024 and older     -> .NET 4.8 -> bin\Debug\net48
#   (net10.0-windows builds load in NO Revit - dev/UiHarness only)
#
# Run (any PowerShell), then restart Revit - the var is read at process start:
#   scripts\set-dev-plugin-dir.ps1            # net8, default
#   scripts\set-dev-plugin-dir.ps1 -Tfm net48 # Revit 2024
#   scripts\set-dev-plugin-dir.ps1 -Clear     # back to the installed release

param(
    [ValidateSet('net8.0-windows', 'net48')]
    [string]$Tfm = 'net8.0-windows',   # default: Revit 2025+
    # [string]$Tfm = 'net48',          # uncomment (and comment the line above) for Revit 2024
    [string]$Configuration = 'Debug',
    [switch]$Clear
)

$ErrorActionPreference = 'Stop'

if ($Clear) {
    [Environment]::SetEnvironmentVariable('BINA_SYNC_PLUGIN_DIR', $null, 'User')
    Write-Host "BINA_SYNC_PLUGIN_DIR cleared - Revit will load the installed release." -ForegroundColor Green
    return
}

$repo = Split-Path $PSScriptRoot -Parent
$dir = Join-Path $repo "bin\$Configuration\$Tfm"

if (-not (Test-Path (Join-Path $dir 'RevitWebAppSync.dll'))) {
    Write-Host "No RevitWebAppSync.dll in $dir - build it first:" -ForegroundColor Red
    Write-Host "  dotnet build $repo\RevitWebAppSync.csproj -c $Configuration -f $Tfm" -ForegroundColor Red
    return
}

[Environment]::SetEnvironmentVariable('BINA_SYNC_PLUGIN_DIR', $dir, 'User')
Write-Host "BINA_SYNC_PLUGIN_DIR = $dir" -ForegroundColor Green
$revitYears = if ($Tfm -eq 'net48') { '2024 and older' } else { '2025 / 2026 / 2027' }
Write-Host "Loads in Revit $revitYears. Restart Revit, then check:" -ForegroundColor Cyan
Write-Host "  Get-Content `"$env:LOCALAPPDATA\Bina\RevitSync\loader.log`" -Tail 3" -ForegroundColor Cyan
