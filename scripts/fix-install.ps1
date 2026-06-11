# BINA Sync repair: removes stale pre-loader addin copies (the parallel
# install that breaks startup), then installs the newest release MSI and
# VERIFIES the loader actually landed.
#
# ASCII-only on purpose: Windows PowerShell 5.1 misparses UTF-8-no-BOM
# files containing multibyte chars when run from disk.
#
# Run (any PowerShell):
#   irm https://raw.githubusercontent.com/binacloudmy/revit-addin-sync/main/scripts/fix-install.ps1 | iex

$ErrorActionPreference = 'Continue'
Write-Host "== BINA Sync repair ==" -ForegroundColor Cyan

if (Get-Process Revit -ErrorAction SilentlyContinue) {
    Write-Host "Revit is running - close it, then re-run this script." -ForegroundColor Red
    return
}

# 1) Stale direct-load manifests (loader's BinaSync.addin never matches -
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
$msiKB = [math]::Round((Get-Item $msi).Length / 1KB)
if ($msiKB -lt 1024) {
    Write-Host "Downloaded MSI is only $msiKB KB - that release is a broken shell. Aborting; report this." -ForegroundColor Red
    return
}
Write-Host "Installing ($msiKB KB)..." -ForegroundColor Cyan
$p = Start-Process msiexec -ArgumentList "/i `"$msi`" /qn" -Wait -PassThru
Write-Host "msiexec exit code: $($p.ExitCode)" -ForegroundColor Cyan

# 4) VERIFY: the loader manifest must now exist in at least one Addins year.
$installed = Get-ChildItem "$env:APPDATA\Autodesk\Revit\Addins\*\BinaSync.addin" -ErrorAction SilentlyContinue
if ($installed) {
    Write-Host "VERIFIED installed:" -ForegroundColor Green
    $installed | ForEach-Object { Write-Host "  $($_.FullName)" -ForegroundColor Green }
} else {
    Write-Host "INSTALL FAILED - no BinaSync.addin found in any Addins folder. Report the msiexec exit code above." -ForegroundColor Red
    return
}

# 5) State dump - paste this output when reporting problems.
Write-Host "`n== State ==" -ForegroundColor Cyan
Write-Host "-- versions:"
Get-ChildItem "$env:LOCALAPPDATA\Bina\RevitSync\versions" -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name
Write-Host "-- loader.log:"
Get-Content "$env:LOCALAPPDATA\Bina\RevitSync\loader.log" -Tail 6 -ErrorAction SilentlyContinue
Write-Host "-- updater.log:"
Get-Content "$env:LOCALAPPDATA\Bina\RevitSync\updater.log" -Tail 8 -ErrorAction SilentlyContinue

Write-Host "`nDone. Start Revit -> 'Bina' tab -> AI panel -> AI Assistant." -ForegroundColor Green
