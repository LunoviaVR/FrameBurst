; FrameBurst installer (Inno Setup 6).
; Build with installer\build.ps1, which publishes a self-contained build and runs ISCC on this script.

#define AppName "FrameBurst"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppExe "FrameBurst.exe"
#define PublishDir "..\publish\win-x64"

[Setup]
AppId={{2D7A853A-D1E5-4116-9A5E-C52CBB496BFD}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Luno
VersionInfoVersion={#AppVersion}
VersionInfoDescription={#AppName} Setup
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Installs for the current user without admin rights; the user can choose "all users" instead.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
SetupIconFile=..\Assets\FrameBurst.ico
LicenseFile=..\LICENSE
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
OutputDir=..\publish
OutputBaseFilename=FrameBurst-Setup-{#AppVersion}
; Close a running FrameBurst before replacing its files, and restart it afterwards.
CloseApplications=yes
RestartApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startup"; Description: "Start {#AppName} when I sign in to Windows"; GroupDescription: "Startup:"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; \
  ValueData: """{app}\{#AppExe}"""; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#AppExe}"; Flags: runhidden; RunOnceId: "StopFrameBurst"
