@echo off
REM BINA Sync Installer Build Script
REM This script builds the Release version of the plugin and creates the installer
REM
REM Prerequisites:
REM   - .NET 8 SDK
REM   - Inno Setup 6 (https://jrsoftware.org/isinfo.php)
REM   - Inno Setup should be in PATH or adjust ISCC path below
REM
REM Usage: Run this script from the installer folder

setlocal enabledelayedexpansion

echo ============================================
echo BINA Sync Installer Build Script
echo ============================================
echo.

REM Navigate to project root
cd /d "%~dp0\.."

REM Step 1: Build the Release version
echo [1/3] Building Release version...
dotnet build -c Release -p:Platform=x64
if errorlevel 1 (
    echo ERROR: Build failed!
    exit /b 1
)
echo Build successful!
echo.

REM Step 2: Check for Inno Setup Compiler
echo [2/3] Checking for Inno Setup Compiler...

REM Try common Inno Setup installation paths
set "ISCC="
if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" (
    set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
) else if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" (
    set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
) else (
    REM Try to find ISCC in PATH
    where ISCC.exe >nul 2>&1
    if !errorlevel! equ 0 (
        set "ISCC=ISCC.exe"
    )
)

if "%ISCC%"=="" (
    echo ERROR: Inno Setup Compiler (ISCC.exe) not found!
    echo.
    echo Please install Inno Setup 6 from: https://jrsoftware.org/isinfo.php
    echo Or add ISCC.exe to your PATH environment variable.
    exit /b 1
)
echo Found Inno Setup: %ISCC%
echo.

REM Step 3: Create the installer
echo [3/3] Creating installer...
cd installer
"%ISCC%" BinaSyncInstaller.iss
if errorlevel 1 (
    echo ERROR: Installer creation failed!
    exit /b 1
)

echo.
echo ============================================
echo SUCCESS! Installer created at:
echo   installer\output\BinaSyncInstaller-1.0.0.exe
echo ============================================
echo.

pause
