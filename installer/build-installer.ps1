# BINA Sync Installer Build Script (PowerShell)
# This script builds the Release version of the plugin and creates the installer
#
# Prerequisites:
#   - .NET 8 SDK
#   - Inno Setup 6 (https://jrsoftware.org/isinfo.php)
#
# Usage: .\build-installer.ps1

param(
    [switch]$SkipBuild,
    [switch]$Verbose
)

$ErrorActionPreference = "Stop"

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "BINA Sync Installer Build Script" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""

# Get script directory and project root
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $scriptDir

# Step 1: Build the Release version
if (-not $SkipBuild) {
    Write-Host "[1/3] Building Release version..." -ForegroundColor Yellow
    Push-Location $projectRoot
    try {
        $buildResult = & dotnet build -c Release -p:Platform=x64
        if ($LASTEXITCODE -ne 0) {
            Write-Host "ERROR: Build failed!" -ForegroundColor Red
            exit 1
        }
        Write-Host "Build successful!" -ForegroundColor Green
    } finally {
        Pop-Location
    }
} else {
    Write-Host "[1/3] Skipping build (using existing Release build)..." -ForegroundColor Yellow
}
Write-Host ""

# Step 2: Check for Inno Setup Compiler
Write-Host "[2/3] Checking for Inno Setup Compiler..." -ForegroundColor Yellow

$isccPaths = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 5\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 5\ISCC.exe"
)

$iscc = $null
foreach ($path in $isccPaths) {
    if (Test-Path $path) {
        $iscc = $path
        break
    }
}

# Try PATH if not found in common locations
if (-not $iscc) {
    $isccInPath = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    if ($isccInPath) {
        $iscc = $isccInPath.Source
    }
}

if (-not $iscc) {
    Write-Host "ERROR: Inno Setup Compiler (ISCC.exe) not found!" -ForegroundColor Red
    Write-Host ""
    Write-Host "Please install Inno Setup 6 from: https://jrsoftware.org/isinfo.php" -ForegroundColor Yellow
    Write-Host "Or add ISCC.exe to your PATH environment variable." -ForegroundColor Yellow
    exit 1
}

Write-Host "Found Inno Setup: $iscc" -ForegroundColor Green
Write-Host ""

# Create output directory if it doesn't exist
$outputDir = Join-Path $scriptDir "output"
if (-not (Test-Path $outputDir)) {
    New-Item -ItemType Directory -Path $outputDir | Out-Null
}

# Step 3: Create the installer
Write-Host "[3/3] Creating installer..." -ForegroundColor Yellow
Push-Location $scriptDir
try {
    & $iscc "BinaSyncInstaller.iss"
    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: Installer creation failed!" -ForegroundColor Red
        exit 1
    }
} finally {
    Pop-Location
}

Write-Host ""
Write-Host "============================================" -ForegroundColor Green
Write-Host "SUCCESS! Installer created at:" -ForegroundColor Green
Write-Host "  $outputDir\BinaSyncInstaller-1.0.0.exe" -ForegroundColor White
Write-Host "============================================" -ForegroundColor Green
