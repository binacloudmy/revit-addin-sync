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
;     /DLoaderDir=..\artifacts\loader /DPluginDir=..\artifacts\plugin
;
; Layout installed (same as the MSI did):
;   %APPDATA%\Autodesk\Revit\Addins\<2025|2026|2027>\  BinaSync.addin + BinaLoader.dll
;   %LocalAppData%\Bina\RevitSync\versions\<ver>\      full plugin (seed build)

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef LoaderDir
  #define LoaderDir "..\artifacts\loader"
#endif
#ifndef PluginDir
  #define PluginDir "..\artifacts\plugin"
#endif

[Setup]
; AppId is permanent — same rule as an MSI UpgradeCode, never regenerate.
AppId={{9C4D7E12-3A86-4B5F-8D29-6E1F0B7A5C43}
AppName=Revit Copilot
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
UninstallDisplayName=Revit Copilot

[Files]
; Loader shim into every supported Revit year (only net8 hosts: 2025-2027).
Source: "{#LoaderDir}\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2025"; Flags: ignoreversion recursesubdirs
Source: "{#LoaderDir}\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2026"; Flags: ignoreversion recursesubdirs
Source: "{#LoaderDir}\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2027"; Flags: ignoreversion recursesubdirs
; Seed plugin build so the loader has something to boot before the first OTA.
Source: "{#PluginDir}\*"; DestDir: "{localappdata}\Bina\RevitSync\versions\{#AppVersion}"; Flags: ignoreversion recursesubdirs

[InstallDelete]
; Stale pre-loader direct-load manifests — a second live copy breaks startup.
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2025\RevitWebAppSync.addin"
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2026\RevitWebAppSync.addin"
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2027\RevitWebAppSync.addin"
; Legacy App-Store-era bundle (the old "BINA / Cloud Sync" tab).
Type: filesandordirs; Name: "{userappdata}\Autodesk\ApplicationPlugins\BinaConnector.bundle"

[UninstallDelete]
; Versions staged by the OTA updater after install (unknown to the uninstaller).
Type: filesandordirs; Name: "{localappdata}\Bina\RevitSync"
