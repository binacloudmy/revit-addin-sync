; BINA Sync Revit Plugin Installer
; Inno Setup Script
;
; This script creates an installer for the BINA Sync Revit plugin.
; Build instructions: Run this script with Inno Setup Compiler (ISCC.exe)

#define MyAppName "BINA Sync for Revit"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "BINA Cloud"
#define MyAppURL "https://app.bina.cloud"

[Setup]
; Application metadata
AppId={{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}

; Installation settings
DefaultDirName={autopf}\BINA Sync
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=yes

; Output settings
OutputDir=output
OutputBaseFilename=BinaSyncInstaller-{#MyAppVersion}
; Uncomment the line below if you have a .ico file:
; SetupIconFile=..\Resources\revitSync.ico
Compression=lzma2/ultra64
SolidCompression=yes

; Appearance
WizardStyle=modern
WizardSizePercent=100

; Privileges (no admin required - installs to user AppData)
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

; Version info
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=BINA Sync Revit Plugin Installer
VersionInfoProductName={#MyAppName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Types]
Name: "full"; Description: "Install for all detected Revit versions"
Name: "custom"; Description: "Choose Revit versions"; Flags: iscustom

[Components]
Name: "revit2024"; Description: "Revit 2024"; Types: full; Check: RevitVersionInstalled('2024')
Name: "revit2025"; Description: "Revit 2025"; Types: full; Check: RevitVersionInstalled('2025')
Name: "revit2026"; Description: "Revit 2026"; Types: full; Check: RevitVersionInstalled('2026')

[Files]
; Main plugin files - Revit 2024
Source: "..\bin\Release\net8.0-windows\RevitWebAppSync.dll"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2024"; Components: revit2024; Flags: ignoreversion
Source: "..\bin\Release\net8.0-windows\RevitWebAppSync.dll.config"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2024"; Components: revit2024; Flags: ignoreversion
Source: "..\bin\Release\net8.0-windows\RevitWebAppSync.deps.json"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2024"; Components: revit2024; Flags: ignoreversion
Source: "..\RevitWebAppSync.addin"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2024"; Components: revit2024; Flags: ignoreversion

; Dependencies - Revit 2024
Source: "..\bin\Release\net8.0-windows\Newtonsoft.Json.dll"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2024"; Components: revit2024; Flags: ignoreversion
Source: "..\bin\Release\net8.0-windows\Microsoft.CodeAnalysis.dll"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2024"; Components: revit2024; Flags: ignoreversion
Source: "..\bin\Release\net8.0-windows\Microsoft.CodeAnalysis.CSharp.dll"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2024"; Components: revit2024; Flags: ignoreversion

; Localization resources - Revit 2024
Source: "..\bin\Release\net8.0-windows\cs\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2024\cs"; Components: revit2024; Flags: ignoreversion recursesubdirs
Source: "..\bin\Release\net8.0-windows\de\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2024\de"; Components: revit2024; Flags: ignoreversion recursesubdirs
Source: "..\bin\Release\net8.0-windows\es\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2024\es"; Components: revit2024; Flags: ignoreversion recursesubdirs
Source: "..\bin\Release\net8.0-windows\fr\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2024\fr"; Components: revit2024; Flags: ignoreversion recursesubdirs
Source: "..\bin\Release\net8.0-windows\it\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2024\it"; Components: revit2024; Flags: ignoreversion recursesubdirs
Source: "..\bin\Release\net8.0-windows\ja\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2024\ja"; Components: revit2024; Flags: ignoreversion recursesubdirs
Source: "..\bin\Release\net8.0-windows\ko\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2024\ko"; Components: revit2024; Flags: ignoreversion recursesubdirs
Source: "..\bin\Release\net8.0-windows\pl\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2024\pl"; Components: revit2024; Flags: ignoreversion recursesubdirs
Source: "..\bin\Release\net8.0-windows\pt-BR\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2024\pt-BR"; Components: revit2024; Flags: ignoreversion recursesubdirs
Source: "..\bin\Release\net8.0-windows\ru\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2024\ru"; Components: revit2024; Flags: ignoreversion recursesubdirs
Source: "..\bin\Release\net8.0-windows\tr\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2024\tr"; Components: revit2024; Flags: ignoreversion recursesubdirs
Source: "..\bin\Release\net8.0-windows\zh-Hans\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2024\zh-Hans"; Components: revit2024; Flags: ignoreversion recursesubdirs
Source: "..\bin\Release\net8.0-windows\zh-Hant\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2024\zh-Hant"; Components: revit2024; Flags: ignoreversion recursesubdirs

; Main plugin files - Revit 2025
Source: "..\bin\Release\net8.0-windows\RevitWebAppSync.dll"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2025"; Components: revit2025; Flags: ignoreversion
Source: "..\bin\Release\net8.0-windows\RevitWebAppSync.dll.config"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2025"; Components: revit2025; Flags: ignoreversion
Source: "..\bin\Release\net8.0-windows\RevitWebAppSync.deps.json"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2025"; Components: revit2025; Flags: ignoreversion
Source: "..\RevitWebAppSync.addin"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2025"; Components: revit2025; Flags: ignoreversion

; Dependencies - Revit 2025
Source: "..\bin\Release\net8.0-windows\Newtonsoft.Json.dll"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2025"; Components: revit2025; Flags: ignoreversion
Source: "..\bin\Release\net8.0-windows\Microsoft.CodeAnalysis.dll"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2025"; Components: revit2025; Flags: ignoreversion
Source: "..\bin\Release\net8.0-windows\Microsoft.CodeAnalysis.CSharp.dll"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2025"; Components: revit2025; Flags: ignoreversion

; Localization resources - Revit 2025
Source: "..\bin\Release\net8.0-windows\cs\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2025\cs"; Components: revit2025; Flags: ignoreversion recursesubdirs
Source: "..\bin\Release\net8.0-windows\de\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2025\de"; Components: revit2025; Flags: ignoreversion recursesubdirs
Source: "..\bin\Release\net8.0-windows\es\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2025\es"; Components: revit2025; Flags: ignoreversion recursesubdirs
Source: "..\bin\Release\net8.0-windows\fr\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2025\fr"; Components: revit2025; Flags: ignoreversion recursesubdirs
Source: "..\bin\Release\net8.0-windows\it\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2025\it"; Components: revit2025; Flags: ignoreversion recursesubdirs
Source: "..\bin\Release\net8.0-windows\ja\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2025\ja"; Components: revit2025; Flags: ignoreversion recursesubdirs
Source: "..\bin\Release\net8.0-windows\ko\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2025\ko"; Components: revit2025; Flags: ignoreversion recursesubdirs
Source: "..\bin\Release\net8.0-windows\pl\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2025\pl"; Components: revit2025; Flags: ignoreversion recursesubdirs
Source: "..\bin\Release\net8.0-windows\pt-BR\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2025\pt-BR"; Components: revit2025; Flags: ignoreversion recursesubdirs
Source: "..\bin\Release\net8.0-windows\ru\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2025\ru"; Components: revit2025; Flags: ignoreversion recursesubdirs
Source: "..\bin\Release\net8.0-windows\tr\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2025\tr"; Components: revit2025; Flags: ignoreversion recursesubdirs
Source: "..\bin\Release\net8.0-windows\zh-Hans\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2025\zh-Hans"; Components: revit2025; Flags: ignoreversion recursesubdirs
Source: "..\bin\Release\net8.0-windows\zh-Hant\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2025\zh-Hant"; Components: revit2025; Flags: ignoreversion recursesubdirs

; Main plugin files - Revit 2026
Source: "..\bin\Release\net8.0-windows\RevitWebAppSync.dll"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2026"; Components: revit2026; Flags: ignoreversion
Source: "..\bin\Release\net8.0-windows\RevitWebAppSync.dll.config"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2026"; Components: revit2026; Flags: ignoreversion
Source: "..\bin\Release\net8.0-windows\RevitWebAppSync.deps.json"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2026"; Components: revit2026; Flags: ignoreversion
Source: "..\RevitWebAppSync.addin"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2026"; Components: revit2026; Flags: ignoreversion

; Dependencies - Revit 2026
Source: "..\bin\Release\net8.0-windows\Newtonsoft.Json.dll"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2026"; Components: revit2026; Flags: ignoreversion
Source: "..\bin\Release\net8.0-windows\Microsoft.CodeAnalysis.dll"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2026"; Components: revit2026; Flags: ignoreversion
Source: "..\bin\Release\net8.0-windows\Microsoft.CodeAnalysis.CSharp.dll"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2026"; Components: revit2026; Flags: ignoreversion

; Localization resources - Revit 2026
Source: "..\bin\Release\net8.0-windows\cs\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2026\cs"; Components: revit2026; Flags: ignoreversion recursesubdirs
Source: "..\bin\Release\net8.0-windows\de\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2026\de"; Components: revit2026; Flags: ignoreversion recursesubdirs
Source: "..\bin\Release\net8.0-windows\es\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2026\es"; Components: revit2026; Flags: ignoreversion recursesubdirs
Source: "..\bin\Release\net8.0-windows\fr\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2026\fr"; Components: revit2026; Flags: ignoreversion recursesubdirs
Source: "..\bin\Release\net8.0-windows\it\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2026\it"; Components: revit2026; Flags: ignoreversion recursesubdirs
Source: "..\bin\Release\net8.0-windows\ja\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2026\ja"; Components: revit2026; Flags: ignoreversion recursesubdirs
Source: "..\bin\Release\net8.0-windows\ko\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2026\ko"; Components: revit2026; Flags: ignoreversion recursesubdirs
Source: "..\bin\Release\net8.0-windows\pl\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2026\pl"; Components: revit2026; Flags: ignoreversion recursesubdirs
Source: "..\bin\Release\net8.0-windows\pt-BR\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2026\pt-BR"; Components: revit2026; Flags: ignoreversion recursesubdirs
Source: "..\bin\Release\net8.0-windows\ru\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2026\ru"; Components: revit2026; Flags: ignoreversion recursesubdirs
Source: "..\bin\Release\net8.0-windows\tr\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2026\tr"; Components: revit2026; Flags: ignoreversion recursesubdirs
Source: "..\bin\Release\net8.0-windows\zh-Hans\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2026\zh-Hans"; Components: revit2026; Flags: ignoreversion recursesubdirs
Source: "..\bin\Release\net8.0-windows\zh-Hant\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2026\zh-Hant"; Components: revit2026; Flags: ignoreversion recursesubdirs

[UninstallDelete]
; Clean up plugin files on uninstall
Type: filesandordirs; Name: "{userappdata}\Autodesk\Revit\Addins\2024\RevitWebAppSync*"
Type: filesandordirs; Name: "{userappdata}\Autodesk\Revit\Addins\2024\Newtonsoft.Json.dll"
Type: filesandordirs; Name: "{userappdata}\Autodesk\Revit\Addins\2024\Microsoft.CodeAnalysis*.dll"
Type: filesandordirs; Name: "{userappdata}\Autodesk\Revit\Addins\2025\RevitWebAppSync*"
Type: filesandordirs; Name: "{userappdata}\Autodesk\Revit\Addins\2025\Newtonsoft.Json.dll"
Type: filesandordirs; Name: "{userappdata}\Autodesk\Revit\Addins\2025\Microsoft.CodeAnalysis*.dll"
Type: filesandordirs; Name: "{userappdata}\Autodesk\Revit\Addins\2026\RevitWebAppSync*"
Type: filesandordirs; Name: "{userappdata}\Autodesk\Revit\Addins\2026\Newtonsoft.Json.dll"
Type: filesandordirs; Name: "{userappdata}\Autodesk\Revit\Addins\2026\Microsoft.CodeAnalysis*.dll"

[Code]
// Check if a specific Revit version is installed
function RevitVersionInstalled(Version: String): Boolean;
var
  RevitPath: String;
begin
  Result := False;

  // Check common Revit installation paths
  RevitPath := ExpandConstant('{commonpf}\Autodesk\Revit ' + Version);
  if DirExists(RevitPath) then
  begin
    Result := True;
    Exit;
  end;

  // Check alternate path format
  RevitPath := ExpandConstant('{commonpf}\Autodesk\Revit' + Version);
  if DirExists(RevitPath) then
  begin
    Result := True;
    Exit;
  end;

  // Check if user has the Addins folder created (means they had Revit running before)
  RevitPath := ExpandConstant('{userappdata}\Autodesk\Revit\Addins\' + Version);
  if DirExists(RevitPath) then
  begin
    Result := True;
    Exit;
  end;
end;

// Check if at least one Revit version is installed
function InitializeSetup(): Boolean;
var
  HasRevit: Boolean;
begin
  HasRevit := RevitVersionInstalled('2024') or RevitVersionInstalled('2025') or RevitVersionInstalled('2026');

  if not HasRevit then
  begin
    MsgBox('No supported Revit version (2024, 2025, or 2026) was detected on this system.' + #13#10 + #13#10 +
           'The installer will continue, but you will need to manually select which Revit version(s) to install for.' + #13#10 + #13#10 +
           'Note: The Revit Addins folder will be created automatically.',
           mbInformation, MB_OK);
  end;

  Result := True;
end;

// Create addins folders if they don't exist
procedure CurStepChanged(CurStep: TSetupStep);
var
  AddinsFolder: String;
begin
  if CurStep = ssInstall then
  begin
    // Create Addins folders for selected components if they don't exist
    if IsComponentSelected('revit2024') then
    begin
      AddinsFolder := ExpandConstant('{userappdata}\Autodesk\Revit\Addins\2024');
      if not DirExists(AddinsFolder) then
        ForceDirectories(AddinsFolder);
    end;

    if IsComponentSelected('revit2025') then
    begin
      AddinsFolder := ExpandConstant('{userappdata}\Autodesk\Revit\Addins\2025');
      if not DirExists(AddinsFolder) then
        ForceDirectories(AddinsFolder);
    end;

    if IsComponentSelected('revit2026') then
    begin
      AddinsFolder := ExpandConstant('{userappdata}\Autodesk\Revit\Addins\2026');
      if not DirExists(AddinsFolder) then
        ForceDirectories(AddinsFolder);
    end;
  end;
end;

[Messages]
BeveledLabel=BINA Cloud
