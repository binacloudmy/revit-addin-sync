# BINA Vibe MSI installer

WiX v4-based MSI for distributing the Revit add-in to customers per PRD §10.11 (FR-INSTALL-01..07).

## Build

```powershell
# One-time
dotnet tool install --global wix

# Build the addin first (Release for all supported Revit versions)
dotnet build ..\RevitWebAppSync.csproj -c Release

# Build the MSI
wix build .\BinaVibe.wxs -ext WixToolset.UI.wixext -o BinaVibe.msi
```

## Sign

```powershell
signtool sign /tr http://timestamp.digicert.com /td sha256 /fd sha256 /a BinaVibe.msi
```

EV code-signing cert from DigiCert / Sectigo — ~$300/yr, ~1 week procurement (US persons faster, MY procurement may need attestation).

## Silent install (enterprise IT)

```powershell
msiexec /i BinaVibe.msi /quiet /norestart
```

## What's left for v1.0 GA

- **Heat.exe harvest** of NuGet runtime deps (Newtonsoft.Json, ClosedXML, Microsoft.CodeAnalysis.CSharp). Currently the .wxs lists only the .dll + .addin manually; harvest is needed for full deps.
- **Bundled .NET 8 runtime** check + auto-install (most Revit 2025+ users already have it).
- **Squirrel-style auto-update** (poll `https://bina.cloud/vibe/updates/latest.json` once per day, prompt user, swap DLL on next Revit close).
- **Per-user vs per-machine** decision — current scope=perMachine; some IT prefers per-user. Toggleable property `MSIINSTALLPERUSER=1` per WiX convention.
- **Telemetry hook** to count installs/uninstalls (opt-in).
- **Localization** (Bahasa Malaysia for first-run dialog per NFR-COMPAT-04).
