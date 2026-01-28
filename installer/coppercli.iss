; Inno Setup script for coppercli
; Download Inno Setup from: https://jrsoftware.org/isinfo.php

#define MyAppName "coppercli"
#define MyAppVersion "0.3.1"
#define MyAppPublisher "coppercli"
#define MyAppURL "https://github.com/thomergil/coppercli"
#define MyAppExeName "coppercli.exe"

[Setup]
; Unique app identifier - generate a new GUID if you fork this project
AppId={{B8E3F2A1-7D4C-4E5B-9A1F-3C2D8E6B4A90}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
; Allow non-admin install to user's local app data
PrivilegesRequiredOverridesAllowed=dialog
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
OutputDir=output
OutputBaseFilename=coppercli-{#MyAppVersion}-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; Minimum Windows version (Windows 10)
MinVersion=10.0
; Application icon
SetupIconFile=coppercli.ico
UninstallDisplayIcon={app}\coppercli.ico

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
; Include all published files
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Include the icon for shortcuts
Source: "coppercli.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
; Start menu shortcut - runs in cmd.exe, closes terminal on exit
Name: "{group}\{#MyAppName}"; Filename: "{cmd}"; Parameters: "/c ""{app}\{#MyAppExeName}"""; WorkingDir: "{app}"; IconFilename: "{app}\coppercli.ico"; Comment: "PCB milling CLI tool"
; Desktop shortcut (optional, user can choose during install)
Name: "{autodesktop}\{#MyAppName}"; Filename: "{cmd}"; Parameters: "/c ""{app}\{#MyAppExeName}"""; WorkingDir: "{app}"; IconFilename: "{app}\coppercli.ico"; Tasks: desktopicon; Comment: "PCB milling CLI tool"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Run]
; Option to run app after install
Filename: "{cmd}"; Parameters: "/c ""{app}\{#MyAppExeName}"""; WorkingDir: "{app}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Clean up app data folder on uninstall (optional - ask user?)
; Type: filesandordirs; Name: "{userappdata}\{#MyAppName}"
