#requires -Version 5.1
<#
.SYNOPSIS
    Builds the BINA Platform Connector and assembles the Autodesk App Store bundle.

.DESCRIPTION
    1. Builds BinaConnector.csproj for net8.0-windows (Revit 2025/2026/2027).
    2. Assembles BinaConnector.bundle/ from build outputs + bundle-templates/.
    3. Validates the bundle structure.
    4. Zips the result as BinaConnector.bundle.zip ready for submission.

    Revit 2024 support is deferred (requires .NET Framework 4.8 + WPF, which only
    builds reliably with Visual Studio Build Tools' MSBuild rather than the
    .NET SDK's MSBuild). Re-introduce by adding a net48 target back to the csproj
    and building with msbuild.exe instead of dotnet build.

.PARAMETER SkipBuild
    Re-package using existing build outputs without re-running dotnet build.

.PARAMETER IncludePdb
    Include .pdb symbol files in the bundle (off by default; symbols are not normally shipped).

.PARAMETER Configuration
    Build configuration. Defaults to Release.

.EXAMPLE
    pwsh ./build-bundle.ps1
    # Full build + bundle + zip.

.EXAMPLE
    pwsh ./build-bundle.ps1 -SkipBuild
    # Re-zip after editing templates without rebuilding the DLL.
#>
[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [switch]$IncludePdb,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $repoRoot

$csproj      = Join-Path $repoRoot 'BinaConnector.csproj'
$bundleRoot  = Join-Path $repoRoot 'BinaConnector.bundle'
$bundleZip   = Join-Path $repoRoot 'BinaConnector.bundle.zip'
$templates   = Join-Path $repoRoot 'bundle-templates'
$net8Out     = Join-Path $repoRoot ('bin/' + $Configuration + '/net8.0-windows')

function Step($message) { Write-Host "==> $message" -ForegroundColor Cyan }
function Fail($message) { Write-Host "ERROR: $message" -ForegroundColor Red; exit 1 }

# 1. Build
if (-not $SkipBuild) {
    Step "Cleaning previous build outputs"
    & dotnet clean $csproj -c $Configuration | Out-Null

    Step "Building net8.0-windows (Revit 2025/2026/2027)"
    & dotnet build $csproj -c $Configuration -f net8.0-windows
    if ($LASTEXITCODE -ne 0) { Fail "net8.0-windows build failed" }
}

# 2. Verify expected DLL exists
$dllNet8 = Join-Path $net8Out 'BinaConnector.dll'
if (-not (Test-Path $dllNet8)) { Fail "Missing build output: $dllNet8" }

# 3. Assemble bundle
Step "Assembling BinaConnector.bundle/"
if (Test-Path $bundleRoot) { Remove-Item $bundleRoot -Recurse -Force }
$contents = Join-Path $bundleRoot 'Contents'
New-Item -ItemType Directory -Path (Join-Path $contents '2025')         | Out-Null
New-Item -ItemType Directory -Path (Join-Path $contents '2026')         | Out-Null
New-Item -ItemType Directory -Path (Join-Path $contents '2027')         | Out-Null
New-Item -ItemType Directory -Path (Join-Path $contents 'Resources/icons') | Out-Null
New-Item -ItemType Directory -Path (Join-Path $contents 'Resources/help')  | Out-Null

# DLL (and optionally PDB) — same net8 binary into the 2025, 2026 and 2027 folders
Copy-Item $dllNet8 (Join-Path $contents '2025/BinaConnector.dll')
Copy-Item $dllNet8 (Join-Path $contents '2026/BinaConnector.dll')
Copy-Item $dllNet8 (Join-Path $contents '2027/BinaConnector.dll')
if ($IncludePdb) {
    $pdbNet8 = Join-Path $net8Out 'BinaConnector.pdb'
    if (Test-Path $pdbNet8) {
        Copy-Item $pdbNet8 (Join-Path $contents '2025/BinaConnector.pdb')
        Copy-Item $pdbNet8 (Join-Path $contents '2026/BinaConnector.pdb')
        Copy-Item $pdbNet8 (Join-Path $contents '2027/BinaConnector.pdb')
    }
}

# Per-version .addin manifests (pinned GUIDs)
Copy-Item (Join-Path $templates '2025.addin') (Join-Path $contents '2025/BinaConnector.addin')
Copy-Item (Join-Path $templates '2026.addin') (Join-Path $contents '2026/BinaConnector.addin')
Copy-Item (Join-Path $templates '2027.addin') (Join-Path $contents '2027/BinaConnector.addin')

# Top-level package manifest
Copy-Item (Join-Path $templates 'PackageContents.xml') (Join-Path $bundleRoot 'PackageContents.xml')

# Resources: EULA, help, icons
Copy-Item (Join-Path $templates 'EULA.html') (Join-Path $contents 'Resources/EULA.html')
Copy-Item (Join-Path $templates 'help/index.html') (Join-Path $contents 'Resources/help/index.html')
Get-ChildItem (Join-Path $templates 'icons') -Filter *.png | ForEach-Object {
    Copy-Item $_.FullName (Join-Path $contents 'Resources/icons')
}

# 4. Validate
Step "Validating bundle"
$required = @(
    'PackageContents.xml',
    'Contents/2025/BinaConnector.addin', 'Contents/2025/BinaConnector.dll',
    'Contents/2026/BinaConnector.addin', 'Contents/2026/BinaConnector.dll',
    'Contents/2027/BinaConnector.addin', 'Contents/2027/BinaConnector.dll',
    'Contents/Resources/EULA.html',
    'Contents/Resources/help/index.html',
    'Contents/Resources/icons/upload_16.png',
    'Contents/Resources/icons/upload_32.png',
    'Contents/Resources/icons/settings_16.png',
    'Contents/Resources/icons/settings_32.png',
    'Contents/Resources/icons/account_16.png',
    'Contents/Resources/icons/account_32.png'
)
foreach ($rel in $required) {
    $abs = Join-Path $bundleRoot $rel
    if (-not (Test-Path $abs)) { Fail "Missing required bundle file: $rel" }
}
try {
    [xml]$pkg = Get-Content (Join-Path $bundleRoot 'PackageContents.xml') -ErrorAction Stop
    if (-not $pkg.ApplicationPackage) { Fail "PackageContents.xml is missing <ApplicationPackage>" }
} catch {
    Fail "PackageContents.xml is not well-formed: $_"
}

# Warn on unfilled placeholders
$placeholders = Select-String -Path (Join-Path $bundleRoot 'PackageContents.xml'),
                                    (Join-Path $bundleRoot 'Contents/2025/BinaConnector.addin'),
                                    (Join-Path $bundleRoot 'Contents/2026/BinaConnector.addin'),
                                    (Join-Path $bundleRoot 'Contents/2027/BinaConnector.addin'),
                                    (Join-Path $bundleRoot 'Contents/Resources/EULA.html'),
                                    (Join-Path $bundleRoot 'Contents/Resources/help/index.html') `
                                -Pattern '\[(SUPPORT_URL|SUPPORT_EMAIL|BINA_API_BASE_URL_PLACEHOLDER|BINA_WEB_APP_URL_PLACEHOLDER)\]' `
                                -SimpleMatch:$false 2>$null
if ($placeholders) {
    Write-Host ""
    Write-Host "WARNING: bundle still contains unfilled placeholders:" -ForegroundColor Yellow
    $placeholders | Select-Object -First 10 | ForEach-Object { Write-Host "  $($_.Path):$($_.LineNumber)  $($_.Matches.Value)" -ForegroundColor Yellow }
    Write-Host "Replace before submitting to the App Store. See docs/pre-submission-checklist.md" -ForegroundColor Yellow
}

# 5. Zip
Step "Compressing to BinaConnector.bundle.zip"
if (Test-Path $bundleZip) { Remove-Item $bundleZip -Force }
Compress-Archive -Path (Join-Path $bundleRoot '*') -DestinationPath $bundleZip -Force

# 6. Summary
$fileCount = (Get-ChildItem $bundleRoot -Recurse -File).Count
$totalSize = "{0:N1} KB" -f ((Get-ChildItem $bundleRoot -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1KB)
$zipSize   = "{0:N1} KB" -f ((Get-Item $bundleZip).Length / 1KB)

Write-Host ""
Write-Host "Bundle ready." -ForegroundColor Green
Write-Host "  Bundle dir : $bundleRoot"
Write-Host "  Files      : $fileCount ($totalSize)"
Write-Host "  Zip        : $bundleZip ($zipSize)"
Write-Host ""
Write-Host "Next: walk through docs/pre-submission-checklist.md before uploading to:"
Write-Host "  https://aps.autodesk.com/app-store/publisher-center/revit"
