# BINA Sync repair: removes stale pre-loader addin copies (the parallel
# install that breaks startup), then installs the newest release MSI.
#
# Run (any PowerShell):
#   irm https://raw.githubusercontent.com/binacloudmy/revit-addin-sync/main/scripts/fix-install.ps1 | iex

$ErrorActionPreference = 'Continue'
Write-Host "== BINA Sync repair ==" -ForegroundColor Cyan

if (Get-Process Revit -ErrorAction SilentlyContinue) {
    Write-Host "Revit is running — close it, then re-run this script." -ForegroundColor Red
    return
}

# 1) Stale direct-load manifests (loader's BinaSync.addin never matches —
#    it references BinaLoader.dll, not RevitWebAppSync).
foreach ($root in @("$env:APPDATA\Autodesk\Revit\Addins", "C:\ProgramData\Autodesk\Revit\Addins")) {
    Get-ChildItem "$root\*\*.addin" -ErrorAction SilentlyContinue | ForEach-Object {
        $raw = Get-Content $_.FullName -Raw -ErrorAction SilentlyContinue
        if ($raw -match 'RevitWebAppSync') {
            Write-Host "DELETING $($_.FullName)" -ForegroundColor Yellow
            Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue
        }
    }
}

# 2) App-Store-era bundles.
Get-ChildItem "C:\ProgramData\Autodesk\ApplicationPlugins" -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match 'Bina|RevitWebAppSync|RevitCopilot' } |
    ForEach-Object {
        Write-Host "DELETING $($_.FullName)" -ForegroundColor Yellow
        Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
    }

# 3) Fresh install of the newest release (per-user, silent).
$msi = Join-Path $env:TEMP 'RevitCopilot.msi'
Write-Host "Downloading newest installer..." -ForegroundColor Cyan
Invoke-WebRequest 'https://github.com/binacloudmy/revit-addin-sync/releases/latest/download/RevitCopilot.msi' -OutFile $msi
Write-Host "Installing..." -ForegroundColor Cyan
Start-Process msiexec -ArgumentList "/i `"$msi`" /qn" -Wait

# 4) State dump — paste this output when reporting problems.
Write-Host "`n== State ==" -ForegroundColor Cyan
Write-Host "-- versions:"
Get-ChildItem "$env:LOCALAPPDATA\Bina\RevitSync\versions" -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name
Write-Host "-- loader.log:"
Get-Content "$env:LOCALAPPDATA\Bina\RevitSync\loader.log" -Tail 6 -ErrorAction SilentlyContinue
Write-Host "-- updater.log:"
Get-Content "$env:LOCALAPPDATA\Bina\RevitSync\updater.log" -Tail 8 -ErrorAction SilentlyContinue

Write-Host "`nDone. Start Revit -> 'Bina' tab -> AI panel -> AI Assistant." -ForegroundColor Green
