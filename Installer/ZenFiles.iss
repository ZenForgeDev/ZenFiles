; ZenFiles installer (Inno Setup). Bygges via build.bat, valg 2 -> installer\ZFSetup.exe
#define AppName "ZenFiles"
#define AppVersion "1.0.0"
#define AppPublisher "ZenForge"
#define AppExe "ZenFiles.exe"

[Setup]
AppId={{6F3B2C1E-5D7A-4E1B-9C84-1A2B3C4D5E6F}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
SourceDir=..
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=installer
OutputBaseFilename=ZFSetup
SetupIconFile=Assets\ZenFiles.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Languages]
Name: "norwegian"; MessagesFile: "compiler:Languages\Norwegian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "publish\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Hvis ZenFiles var satt som standard filbehandler: sett Windows Utforsker tilbake foer appen fjernes
Filename: "{app}\{#AppExe}"; Parameters: "--restore-default"; Flags: runhidden; RunOnceId: "RestoreExplorer"
