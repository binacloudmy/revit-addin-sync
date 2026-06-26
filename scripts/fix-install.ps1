# BINA Sync repair: removes stale pre-loader addin copies, purges dead MSI
# registrations left by the broken v0.0.1-v0.0.6 shell installers, then
# installs the newest release EXE and VERIFIES the loader actually landed.
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

# 2) Legacy bundles (user-level AND machine-level ApplicationPlugins).
foreach ($plugRoot in @("$env:APPDATA\Autodesk\ApplicationPlugins", "C:\ProgramData\Autodesk\ApplicationPlugins")) {
    Get-ChildItem $plugRoot -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match 'Bina|RevitWebAppSync|RevitCopilot' } |
        ForEach-Object {
            Write-Host "DELETING $($_.FullName)" -ForegroundColor Yellow
            Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
        }
}

# 3) Dead Windows Installer registrations from the fileless v0.0.1-v0.0.6
#    MSIs (they install nothing but register a product; later real installs
#    then die with 1603). Exit 1605 'not installed' here is fine.
foreach ($hive in @('HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*',
                    'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*')) {
    Get-ItemProperty $hive -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName -match 'Revit Copilot' } |
        ForEach-Object {
            Write-Host "UNREGISTERING $($_.DisplayName) $($_.DisplayVersion)" -ForegroundColor Yellow
            $code = $_.PSChildName
            Start-Process msiexec -ArgumentList "/x $code /qn" -Wait -ErrorAction SilentlyContinue
        }
}

# 4) Fresh install of the newest release EXE (per-user, silent, no Windows
#    Installer involved at all).
$exe = Join-Path $env:TEMP 'RevitCopilotSetup.exe'
Write-Host "Downloading newest installer..." -ForegroundColor Cyan
Invoke-WebRequest 'https://github.com/binacloudmy/revit-addin-sync/releases/latest/download/RevitCopilotSetup.exe' -OutFile $exe
$exeKB = [math]::Round((Get-Item $exe).Length / 1KB)
if ($exeKB -lt 1024) {
    Write-Host "Downloaded installer is only $exeKB KB - that release is a broken shell. Aborting; report this." -ForegroundColor Red
    return
}
Write-Host "Installing ($exeKB KB)..." -ForegroundColor Cyan
$p = Start-Process $exe -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES' -Wait -PassThru
Write-Host "installer exit code: $($p.ExitCode)" -ForegroundColor Cyan

# 5) VERIFY: the loader manifest must now exist in at least one Addins year.
$installed = Get-ChildItem "$env:APPDATA\Autodesk\Revit\Addins\*\BinaSync.addin" -ErrorAction SilentlyContinue
if ($installed) {
    Write-Host "VERIFIED installed:" -ForegroundColor Green
    $installed | ForEach-Object { Write-Host "  $($_.FullName)" -ForegroundColor Green }
} else {
    Write-Host "INSTALL FAILED - no BinaSync.addin found in any Addins folder. Report the exit code above." -ForegroundColor Red
    return
}

# 6) State dump - paste this output when reporting problems.
Write-Host "`n== State ==" -ForegroundColor Cyan
Write-Host "-- versions:"
Get-ChildItem "$env:LOCALAPPDATA\Bina\RevitSync\versions" -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name
Write-Host "-- loader.log:"
Get-Content "$env:LOCALAPPDATA\Bina\RevitSync\loader.log" -Tail 6 -ErrorAction SilentlyContinue
Write-Host "-- updater.log:"
Get-Content "$env:LOCALAPPDATA\Bina\RevitSync\updater.log" -Tail 8 -ErrorAction SilentlyContinue

Write-Host "`nDone. Start Revit -> 'Bina' tab -> AI panel -> AI Assistant." -ForegroundColor Green
