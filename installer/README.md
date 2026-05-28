# Revit Copilot MSI installer

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

## Install

```powershell
# Per-user, no admin (drafter double-clicks the .msi, or:)
msiexec /i RevitCopilot.msi

# Silent (enterprise IT push)
msiexec /i RevitCopilot.msi /qn
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
