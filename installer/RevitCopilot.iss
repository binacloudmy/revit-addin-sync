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
; net48 loader for Revit 2023/2024 — only when the build ships a net48 payload.
#ifdef LoaderNet48Dir
Source: "{#LoaderNet48Dir}\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2023"; Flags: ignoreversion recursesubdirs
Source: "{#LoaderNet48Dir}\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2024"; Flags: ignoreversion recursesubdirs
#endif
; Seed plugin build (per-target subfolders + root manifest.json + .complete)
; so the loader has something to boot before the first OTA.
Source: "{#PluginDir}\*"; DestDir: "{localappdata}\Bina\RevitSync\versions\{#AppVersion}"; Flags: ignoreversion recursesubdirs
; Seed the packaged engine so EngineManager can spawn it before the first OTA.
; Optional: only if the build published artifacts\engine (Check skips it cleanly).
Source: "{#EngineDir}\*"; DestDir: "{localappdata}\Bina\RevitSync\engine\{#EngineVersion}"; Flags: ignoreversion recursesubdirs skipifsourcedoesntexist
; Publisher public cert (exported by build-installer.ps1 only on signed
; builds). Pre-trusting it below removes even the one-time "Signed Add-In —
; Always Load?" prompt. Unsigned builds have no .cer -> both entries skip.
Source: "..\artifacts\bina-cloudtech.cer"; DestDir: "{localappdata}\Bina\RevitSync"; Flags: skipifsourcedoesntexist
; Boot-time engine launcher (ONLOGON Scheduled Task handler). Stable, NON-versioned
; path so the task registration survives engine OTA updates (it replays the
; add-in's own engine-boot.json handoff, which the add-in rewrites on every
; spawn). Without it, a reboot leaves the engine down until a human opens Revit.
; Always shipped (it is also the -Unregister handler); the task registration
; below is what's gated on an engine bundle actually being present.
Source: "engine-boot.ps1"; DestDir: "{localappdata}\Bina\RevitSync\engine"; Flags: ignoreversion

[Run]
; Per-user TrustedPublisher store (no admin) — Revit checks it before showing
; the addin security dialog. Idempotent: re-adding an existing cert is a no-op.
Filename: "certutil"; Parameters: "-user -addstore TrustedPublisher ""{localappdata}\Bina\RevitSync\bina-cloudtech.cer"""; Flags: runhidden; Check: FileExists(ExpandConstant('{localappdata}\Bina\RevitSync\bina-cloudtech.cer'))
; Auto-start the engine at every Windows logon — this is the "survives reboot"
; guarantee. ONLOGON (not ONSTARTUP): the engine runs as the signed-in user and
; writes its session DB under that user's home, so it must start in their session.
;
; The script registers its OWN task (-Register) instead of us calling
; `schtasks /Create /TR "..."` here. schtasks needs the inner -File path escaped
; as \" inside an already-quoted /TR value, which breaks for every drafter whose
; Windows profile contains a space; Register-ScheduledTask takes the argument
; string as data and has nothing to escape. Idempotent via -Force.
;
; Check: only when an engine bundle actually shipped. The versioned payload dir
; is created only by the (skipifsourcedoesntexist) engine [Files] entry above, so
; its presence is an exact install-time test — an addin-only cloud build has
; nothing to boot and gets no logon task. Everything runs hidden: runhidden here,
; -WindowStyle Hidden on the task, CreateNoWindow on the engine itself. The end
; user never sees a terminal.
Filename: "powershell.exe"; Parameters: "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File ""{localappdata}\Bina\RevitSync\engine\engine-boot.ps1"" -Register"; Flags: runhidden; Check: DirExists(ExpandConstant('{localappdata}\Bina\RevitSync\engine\{#EngineVersion}'))

; Remove the scheduled task on uninstall so we never leave a zombie launcher that
; fires at every logon after the product is gone. [UninstallRun] executes BEFORE
; files are deleted, so the script is still on disk to run its own -Unregister.
[UninstallRun]
Filename: "powershell.exe"; Parameters: "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File ""{localappdata}\Bina\RevitSync\engine\engine-boot.ps1"" -Unregister"; Flags: runhidden; Check: FileExists(ExpandConstant('{localappdata}\Bina\RevitSync\engine\engine-boot.ps1'))

[InstallDelete]
; Stale pre-loader direct-load manifests — a second live copy breaks startup.
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2023\RevitWebAppSync.addin"
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2024\RevitWebAppSync.addin"
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2025\RevitWebAppSync.addin"
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2026\RevitWebAppSync.addin"
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2027\RevitWebAppSync.addin"
; Legacy App-Store-era bundle (the old "BINA / Cloud Sync" tab).
Type: filesandordirs; Name: "{userappdata}\Autodesk\ApplicationPlugins\BinaConnector.bundle"

[UninstallDelete]
; Versions staged by the OTA updater after install (unknown to the uninstaller).
Type: filesandordirs; Name: "{localappdata}\Bina\RevitSync"
