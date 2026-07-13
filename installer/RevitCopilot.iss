; Revit Copilot — Inno Setup installer (replaces the WiX MSI).
;
; Why EXE, not MSI: this install is plain per-user file copies. The MSI route
; dragged in Windows Installer's product database — six early fileless builds
; registered "products" there and later real installs died with 1603 trying to
; upgrade the corpses. Inno has no machine-wide database to corrupt and pure
; per-user installs never need admin.
;
; One-click: every wizard page is disabled, so double-click = progress bar =
; installed. Silent for IT rollout:  RevitCopilot-<ver>-setup.exe /VERYSILENT
;
; Build (CI does this in .github/workflows/release.yml):
;   ISCC installer\RevitCopilot.iss /DAppVersion=0.0.8 ^
;     /DLoaderNet8Dir=..\artifacts\loader-net8 /DPluginDir=..\artifacts\plugin
; Optional: /DLoaderNet48Dir=..\artifacts\loader-net48 registers Revit 2024
; (build-installer.ps1 passes it ONLY when a net48 payload was published — a
; loader with nothing to load would dead-end 2024 users on a reinstall dialog).
; Optional engine + signing flags (see installer\build-installer.ps1 for the
; wrapper that computes these): /DEngineDir=... /DEngineVersion=... and
; /Sbinasign=<signtool command> /DSignToolName=binasign
;
; Layout installed:
;   %APPDATA%\Autodesk\Revit\Addins\<2025|2026|2027>\  BinaSync.addin + BinaLoader.dll (net8)
;   %APPDATA%\Autodesk\Revit\Addins\2024\              same, net48 build (when defined)
;   %LocalAppData%\Bina\RevitSync\versions\<ver>\      root manifest.json (targets map)
;                                          \net8.0\    payload for Revit 2025+2026
;                                          \net10.0\   payload for Revit 2027
;                                          \net48\     payload for Revit 2024 (Phase B)

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef LoaderNet8Dir
  #define LoaderNet8Dir "..\artifacts\loader-net8"
#endif
#ifndef PluginDir
  #define PluginDir "..\artifacts\plugin"
#endif
; Phase 4: the packaged Copilot Engine (bina-engine.exe + _internal). Optional
; — define EngineDir + EngineVersion to seed it; omit to ship addin-only.
#ifndef EngineDir
  #define EngineDir "..\artifacts\engine"
#endif
#ifndef EngineVersion
  #define EngineVersion AppVersion
#endif
; Zero-config release (bina-defaults.json): now written by build-installer.ps1
; directly into each payload subfolder, so it rides the PluginDir copy below —
; no dedicated define/entry anymore.

[Setup]
; AppId is permanent — same rule as an MSI UpgradeCode, never regenerate.
AppId={{9C4D7E12-3A86-4B5F-8D29-6E1F0B7A5C43}
AppName=BINA AI Copilot
AppPublisher=Bina Cloudtech Sdn Bhd
AppPublisherURL=https://app.bina.cloud
AppVersion={#AppVersion}
DefaultDirName={localappdata}\Bina\RevitSync
PrivilegesRequired=lowest
OutputDir=.
OutputBaseFilename=RevitCopilot-{#AppVersion}-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableWelcomePage=yes
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
DisableFinishedPage=yes
Uninstallable=yes
UninstallDisplayName=BINA AI Copilot
; Optional code signing: build-installer.ps1 passes SignToolName (via /D) +
; a matching /S<name>=<signtool command> only when -SignCert/-SignPassword or
; SIGNTOOL_ARGS was given. SignTool signs the compiled setup EXE; pairing it
; with SignedUninstaller also signs the uninstaller stub embedded inside it
; (the only way to get a signed uninstaller — it can't be signed after the
; fact since Inno generates it fresh on the end user's machine at install
; time). Omit -> this whole block doesn't exist -> unsigned, unchanged build.
#ifdef SignToolName
SignTool={#SignToolName}
SignedUninstaller=yes
#endif

[Files]
; net8 loader shim into every net8+ Revit year (2025-2026 = .NET 8; 2027's
; .NET 10 host loads a net8 assembly fine).
Source: "{#LoaderNet8Dir}\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2025"; Flags: ignoreversion recursesubdirs
Source: "{#LoaderNet8Dir}\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2026"; Flags: ignoreversion recursesubdirs
Source: "{#LoaderNet8Dir}\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2027"; Flags: ignoreversion recursesubdirs
; net48 loader for Revit 2024 — only when the build ships a 2024 payload.
#ifdef LoaderNet48Dir
Source: "{#LoaderNet48Dir}\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2024"; Flags: ignoreversion recursesubdirs
#endif
; Seed plugin build (per-target subfolders + root manifest.json + .complete)
; so the loader has something to boot before the first OTA.
Source: "{#PluginDir}\*"; DestDir: "{localappdata}\Bina\RevitSync\versions\{#AppVersion}"; Flags: ignoreversion recursesubdirs
; Seed the packaged engine so EngineManager can spawn it before the first OTA.
; Optional: only if the build published artifacts\engine (Check skips it cleanly).
Source: "{#EngineDir}\*"; DestDir: "{localappdata}\Bina\RevitSync\engine\{#EngineVersion}"; Flags: ignoreversion recursesubdirs skipifsourcedoesntexist

[InstallDelete]
; Stale pre-loader direct-load manifests — a second live copy breaks startup.
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2024\RevitWebAppSync.addin"
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2025\RevitWebAppSync.addin"
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2026\RevitWebAppSync.addin"
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2027\RevitWebAppSync.addin"
; Legacy App-Store-era bundle (the old "BINA / Cloud Sync" tab).
Type: filesandordirs; Name: "{userappdata}\Autodesk\ApplicationPlugins\BinaConnector.bundle"

[UninstallDelete]
; Versions staged by the OTA updater after install (unknown to the uninstaller).
Type: filesandordirs; Name: "{localappdata}\Bina\RevitSync"
