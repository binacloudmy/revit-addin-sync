# Bina AI Copilot installer (Inno Setup)

The installer that ships today is an Inno Setup EXE, `RevitCopilot-<ver>-setup.exe`,
built by `build-installer.ps1`. A WiX/MSI build predates it and is kept further
down for reference only.

## Build (current)

```powershell
powershell -ExecutionPolicy Bypass -File .\build-installer.ps1
```

This builds the addin (Release) and produces `RevitCopilot-<ver>-setup.exe` with
Inno Setup. See `build-installer.ps1` for `-Version`, `-EngineZip`, `-SignCert`
and `-GatewayUrl`.

## Historical WiX/MSI build (not used)

The installer used to be a WiX v4/v5 MSI (PRD §10.11, FR-INSTALL-01..07). The
steps below are retained for reference; they do NOT produce the current
installer.

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

## Historical sign steps (not used)

```powershell
.\build-installer.ps1 -Sign -Thumbprint <cert-thumbprint>
# or by hand:
signtool sign /tr http://timestamp.digicert.com /td sha256 /fd sha256 /a RevitCopilot.msi
```

Code signing provides a verified publisher and Authenticode trust. It does NOT,
on its own, eliminate Windows SmartScreen: SmartScreen keys on application
reputation, which is separate from signature validity (see "SmartScreen and
signing" below).

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

## Signed production release (current)

`release.yml`'s `release` job builds UNSIGNED artifacts and uploads them only as
workflow artifacts; it publishes nothing. Two separate jobs can publish STAGING
tags: `sign-staging` (when a self-hosted signer is registered) and
`publish-staging-unsigned` (unsigned, via `installer/publish-unsigned-staging.ps1`).
Production signing is a separate, authorized process on the sovereign signing box
with the cert live (Certum SimplySign); it is never done by the build job:

```powershell
# 1. Connect SimplySign Desktop (Certum cloud cert lands in the store)
# 2. From the tag checkout, in-process under PowerShell 7 (pwsh) so -Mandatory
#    binds as a real boolean (see PR #157):
pwsh -NoProfile
git checkout <tag>
.\installer\sign-release.ps1 -Tag <tag> -Thumbprint <sha1>
```

`sign-release.ps1` rebuilds via `build-installer.ps1` (signs BinaLoader.dll,
every RevitWebAppSync.dll payload, the setup EXE and the embedded uninstaller),
then re-creates the OTA zip from the SIGNED tree, uploads the signed installer +
OTA zip to TM One, flips `latest.json` (go-live), and for prod tags publishes the
signed GitHub bridge release.

Note: `build-installer.ps1` signs the embedded uninstaller (Inno
`SignedUninstaller`), but `sign-release.ps1`'s verification loop verifies only the
first-party DLLs and the setup EXE on disk - it does not separately verify the
uninstaller's signature.

## Install

```powershell
# Per-user, no admin (drafter double-clicks the setup exe, or:)
RevitCopilot-<ver>-setup.exe

# Silent (enterprise IT push)
RevitCopilot-<ver>-setup.exe /VERYSILENT
```

## SmartScreen and signing

Windows SmartScreen builds application reputation per publisher/file over time,
independently of whether a binary is validly signed. A newly released, correctly
signed `RevitCopilot-<ver>-setup.exe` can therefore still show "Windows
protected your PC" for a user who has never run it.

To verify the official installer before running it:
- The publisher must read **BINA CLOUDTECH SDN BHD** (Right-click the file ->
  Properties -> Digital Signatures).
- Check the release-specific SHA-256 published with that version, e.g.
  `Get-FileHash .\RevitCopilot-<ver>-setup.exe -Algorithm SHA256`, and compare
  it against the digest on the official download page for that version.
- PowerShell: `Get-AuthenticodeSignature .\RevitCopilot-<ver>-setup.exe |
  Format-List Status, @{n='Signer';e={$_.SignerCertificate.Subject}},
  TimeStamperCertificate`

If a SmartScreen prompt appears and the publisher and SHA-256 match, the
standard user step is **More info -> Run anyway**.

Do NOT disable Microsoft Defender SmartScreen and do NOT try to bypass your
organisation's security policy. On a managed/company PC, contact your IT
administrator if the app is blocked.

## Done

- **Dependency harvest** — `<Files>` wildcard picks up the .dll, .addin manifest,
  and all NuGet runtime deps (Newtonsoft.Json, ClosedXML, Microsoft.CodeAnalysis.*)
  automatically. No manual file list, no heat.exe.
- **Per-user scope** — installs to `%APPDATA%\Autodesk\Revit\Addins\<year>\`, no
  admin elevation.
- **Supported versions** — Revit 2025/2026/2027 (net8.0-windows; Revit <=2024 on
  .NET 4.8 can't load the assembly).

## What's left for v1.0 GA

- **SmartScreen application reputation** for the signed installer (an OV cert
  does not grant it automatically); interim guidance is in "SmartScreen and
  signing" above.
- **Squirrel-style auto-update** (poll an updates feed daily, swap DLL on next Revit close).
- **Telemetry hook** to count installs/uninstalls (opt-in).
- **Localization** (Bahasa Malaysia for first-run dialog per NFR-COMPAT-04).
