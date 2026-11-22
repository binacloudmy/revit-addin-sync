; Bina AI Copilot — Inno Setup installer (replaces the WiX MSI).
;
; Why EXE, not MSI: this install is plain per-user file copies. The MSI route
; dragged in Windows Installer's product database — six early fileless builds
; registered "products" there and later real installs died with 1603 trying to
; upgrade the corpses. Inno has no machine-wide database to corrupt and pure
; per-user installs never need admin.
;
; Short wizard: Ready page, progress bar, then a Finish page that says BINA is
; installed (14zefhy1x71). Silent for IT rollout and the OTA self-reinstall:
; RevitCopilot-<ver>-setup.exe /VERYSILENT shows no page at all.
;
; Build (CI does this in .github/workflows/release.yml):
;   ISCC installer\RevitCopilot.iss /DAppVersion=0.0.8 ^
;     /DLoaderNet8Dir=..\artifacts\loader-net8 /DPluginDir=..\artifacts\plugin
; Optional: /DLoaderNet48Dir=..\artifacts\loader-net48 registers Revit 2024
; (build-installer.ps1 passes it ONLY when a net48 payload was published — a
; loader with nothing to load would dead-end 2024 users on a reinstall dialog).
; Optional signing flags (see installer\build-installer.ps1 for the wrapper
; that computes these): /Sbinasign=<signtool command> /DSignToolName=binasign
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
; Ready page ON for the diagnostics consent line (OTA self-heal F6, see
; [Messages]). Silent OTA reinstalls (/VERYSILENT) never show it.
DisableReadyPage=no
; Finish page ON: the user gets a clear "installed" message and a Finish button.
; /SILENT and /VERYSILENT (IT rollout, OTA self-reinstall) never show it.
DisableFinishedPage=no
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

[Messages]
; OTA self-heal F6 consent: the add-in uploads scrubbed logs by itself after a
; startup/update/reinstall failure or when BINA support requests them
; (DIAGNOSTICS_AUTO; per-machine opt-out "DiagnosticsAuto": false in
; %APPDATA%\RevitWebAppSync\config.json). The Ready page shows ReadyLabel2b
; (no memo: dir/group pages are disabled); 2a covers the memo layout.
ReadyLabel2a=BINA sends anonymous error logs to help support you. They never include file contents.%n%nClick Install to continue with the installation, or click Back if you want to review or change any settings.
ReadyLabel2b=BINA sends anonymous error logs to help support you. They never include file contents.%n%nClick Install to continue with the installation.
; Finish page (nothing is launched from here).
FinishedHeadingLabel=BINA AI Copilot is installed
FinishedLabel=BINA AI Copilot was installed for your Windows account.%n%nIf Revit is open, close it and start it again so BINA loads.
FinishedLabelNoIcons=BINA AI Copilot was installed for your Windows account.%n%nIf Revit is open, close it and start it again so BINA loads.

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
; ONLY IF MISSING: this installer also runs silently as the OTA self-reinstall
; (plugin SelfReinstall, /VERYSILENT after Revit exits) on machines that already
; have this version staged. Overwriting it would clobber the crash-guard markers
; (.healthy / .bad, see Services\VersionHealth.cs) and re-bless a build the
; loader proved bad. A folder without .complete (never staged, or half-pruned
; around a locked DLL) is re-seeded. NeedSeed ([Code]) decides ONCE at setup
; start: a per-file FileExists check would flip to false the moment the
; payload's own .complete is copied and skip the rest of the tree.
Source: "{#PluginDir}\*"; DestDir: "{localappdata}\Bina\RevitSync\versions\{#AppVersion}"; Flags: ignoreversion recursesubdirs; Check: NeedSeed
; Publisher public cert (exported by build-installer.ps1 only on signed
; builds). Pre-trusting it below removes even the one-time "Signed Add-In —
; Always Load?" prompt. Unsigned builds have no .cer -> both entries skip.
Source: "..\artifacts\bina-cloudtech.cer"; DestDir: "{localappdata}\Bina\RevitSync"; Flags: skipifsourcedoesntexist

[Run]
; Per-user TrustedPublisher store (no admin) — Revit checks it before showing
; the addin security dialog. Idempotent: re-adding an existing cert is a no-op.
Filename: "certutil"; Parameters: "-user -addstore TrustedPublisher ""{localappdata}\Bina\RevitSync\bina-cloudtech.cer"""; Flags: runhidden; Check: FileExists(ExpandConstant('{localappdata}\Bina\RevitSync\bina-cloudtech.cer'))
; The colocated Copilot Engine is retired (the copilot runs in BINA's cloud).
; Earlier installs registered an ONLOGON task that launched it and staged its
; bundle under engine\: stop it, drop the task and remove the folder, so no
; zombie launcher fires at logon. Each step is a no-op when nothing is there.
Filename: "taskkill.exe"; Parameters: "/F /IM bina-engine.exe"; Flags: runhidden
Filename: "schtasks.exe"; Parameters: "/Delete /TN ""BINA Copilot Engine"" /F"; Flags: runhidden
Filename: "{cmd}"; Parameters: "/C rmdir /S /Q ""{localappdata}\Bina\RevitSync\engine"""; Flags: runhidden; Check: DirExists(ExpandConstant('{localappdata}\Bina\RevitSync\engine'))

; Same clean-up on uninstall, for a machine that never ran the upgrade above.
[UninstallRun]
Filename: "schtasks.exe"; Parameters: "/Delete /TN ""BINA Copilot Engine"" /F"; Flags: runhidden

[InstallDelete]
; Upgrade / self-reinstall contract: NOTHING under {localappdata}\Bina\RevitSync
; is deleted on install — versions\ (OTA-staged builds + crash-guard markers),
; update-gate.json, reinstall.json, feed.json, bad-versions.json and
; telemetry.id all survive. Everything else in this script (loaders for every
; Revit year, BinaSync.addin, the TrustedPublisher cert) is re-applied
; idempotently on every run; the retired engine's folder and logon task are
; removed by [Run] above.
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

[Code]
var
  SeedNeeded: Boolean;

function InitializeSetup(): Boolean;
begin
  // Evaluated before any file is copied — see the seed [Files] entry.
  SeedNeeded := not FileExists(ExpandConstant('{localappdata}\Bina\RevitSync\versions\{#AppVersion}\.complete'));
  Result := True;
end;

function NeedSeed(): Boolean;
begin
  Result := SeedNeeded;
end;
