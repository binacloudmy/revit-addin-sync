# Bina AI Copilot MSI installer

WiX v4/v5-based MSI for distributing the Revit add-in to customers per PRD §10.11 (FR-INSTALL-01..07).

## Build (one command)

```powershell
powershell -ExecutionPolicy Bypass -File .\build-installer.ps1
```

This builds the addin (Release), stages the `.addin` manifest, installs the WiX
tool if missing, and produces `RevitCopilot.msi` in the repo root.

### Or by hand

```powershell
# One-time
dotnet tool install --global wix
wix extension add -g WixToolset.UI.wixext

# Build the addin first
dotnet build ..\RevitWebAppSync.csproj -c Release
copy ..\RevitWebAppSync.addin ..\bin\Release\net8.0-windows\

# Build the MSI
wix build .\RevitCopilot.wxs -ext WixToolset.UI.wixext `
  -d PublishDir=..\bin\Release\net8.0-windows -o RevitCopilot.msi
```

## Sign

```powershell
.\build-installer.ps1 -Sign -Thumbprint <cert-thumbprint>
# or by hand:
signtool sign /tr http://timestamp.digicert.com /td sha256 /fd sha256 /a RevitCopilot.msi
```

EV code-signing cert from DigiCert / Sectigo — ~$300/yr, ~1 week procurement
(MY procurement may need attestation). Without it the install still works but
Windows SmartScreen shows an "unknown publisher" warning.

## Build with the engine bundle

> The sections above describe an older WiX/MSI build. The installer is
> actually built today by `RevitCopilot.iss` via Inno Setup (see
> `build-installer.ps1`), producing `RevitCopilot-<ver>-setup.exe`, not an
> `.msi`. Kept above for reference; the commands below are the current ones.

The addin can spawn a colocated Copilot Engine (bina-ai's `app/engine`,
packaged by bina-ai's `scripts/build-engine-bundle.ps1` into
`dist/bina-engine-<ver>.zip`). Pass the zip to seed it alongside the addin:

```powershell
installer\build-installer.ps1 -Version 0.0.8 -EngineZip dist\bina-engine-1.0.0.zip
```

Omit `-EngineZip` and the installer ships addin-only — byte-identical output
to a build without this flag.

## Sign (current build script)

`build-installer.ps1` signs both the setup EXE and the embedded uninstaller
in one pass via Inno Setup's native `SignTool` mechanism. Cert material is
never committed — pass it as a parameter or an env var:

```powershell
# cert-store thumbprint
installer\build-installer.ps1 -Version 0.0.8 -SignCert <cert-thumbprint>

# PFX file + password
installer\build-installer.ps1 -Version 0.0.8 -SignCert C:\path\cert.pfx -SignPassword <pw>

# keep the password off the command line entirely (e.g. a CI secret):
$env:SIGNTOOL_ARGS = '/f C:\path\cert.pfx /p <pw> /fd SHA256 /tr http://timestamp.digicert.com /td SHA256'
installer\build-installer.ps1 -Version 0.0.8
```

## Signed release (one command, replaces CI assets)

CI has no cert, so `release.yml` assets ship **unsigned** `RevitWebAppSync.dll`
payloads — Smart App Control / WDAC (Enforce) machines block them at load
(`0x800711C7`). Post-signing the setup EXE does not fix the DLLs inside it or
the OTA zip. Until CI gets a signing service (see ClickUp 86eyc61fy), release
from a Windows box with the cert live:

```powershell
# 1. Connect SimplySign Desktop (Certum cloud cert lands in the store)
# 2. Build at the exact tag:
git checkout v0.0.27-staging
powershell -ExecutionPolicy Bypass -File installer\sign-release.ps1 -Tag v0.0.27-staging
```

`sign-release.ps1` rebuilds via `build-installer.ps1` (signs loader, every
payload DLL, EXE, uninstaller), re-creates the OTA zip from the signed tree,
regenerates `version.json` (sha256 + url), verifies every signature, and
`gh release upload --clobber`s the tag's assets.

## Install

```powershell
# Per-user, no admin (drafter double-clicks the setup exe, or:)
RevitCopilot-<ver>-setup.exe

# Silent (enterprise IT push)
RevitCopilot-<ver>-setup.exe /VERYSILENT
```

## Done

- **Dependency harvest** — `<Files>` wildcard picks up the .dll, .addin manifest,
  and all NuGet runtime deps (Newtonsoft.Json, ClosedXML, Microsoft.CodeAnalysis.*)
  automatically. No manual file list, no heat.exe.
- **Per-user scope** — installs to `%APPDATA%\Autodesk\Revit\Addins\<year>\`, no
  admin elevation.
- **Supported versions** — Revit 2025/2026/2027 (net8.0-windows; Revit <=2024 on
  .NET 4.8 can't load the assembly).

## What's left for v1.0 GA

- **Code-signing cert** procurement (removes SmartScreen warning).
- **Squirrel-style auto-update** (poll an updates feed daily, swap DLL on next Revit close).
- **Telemetry hook** to count installs/uninstalls (opt-in).
- **Localization** (Bahasa Malaysia for first-run dialog per NFR-COMPAT-04).
