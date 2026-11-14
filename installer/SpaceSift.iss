; SpaceSift installer (Inno Setup 6)
; Builds SpaceSift-Setup-<version>-x64.exe for 64-bit Windows 10 (1809+) and Windows 11.
; The app is a self-contained single-file exe, so the user does not need .NET installed.
;
; Build:  ISCC.exe installer\SpaceSift.iss   (after: dotnet publish ... -o publish)

#define AppName      "SpaceSift"
#define AppVersion   "1.2.0"
#define AppPublisher "mercyblack011"
#define AppURL       "https://github.com/mercyblack011/SpaceSift"
#define AppExe       "SpaceSift.exe"

[Setup]
AppId={{6F3C2B1E-8A4D-4C6B-9E2F-5A1D7C3B9E10}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}/issues
AppUpdatesURL={#AppURL}/releases
VersionInfoVersion={#AppVersion}.0
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} Setup

; 64-bit Windows 10 1809 (build 17763) and Windows 11 only
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763

; Installs for the current user without an admin prompt; the user can choose "all users" instead
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes

; Look
WizardStyle=modern
SetupIconFile=..\SpaceSift\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}

; Output
OutputDir=..\dist
OutputBaseFilename=SpaceSift-Setup-{#AppVersion}-x64
Compression=lzma2/ultra64
SolidCompression=yes

; Close a running copy before updating / uninstalling
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\publish\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; settings and error log the app writes for this user
Type: filesandordirs; Name: "{localappdata}\SpaceSift"
