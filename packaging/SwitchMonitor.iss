#define AppVersion "1.3.0"
#define Root SourcePath + ".."

[Setup]
AppId={{A153C1DB-CA20-448F-B320-37E0448E041A}
AppName=SwitchMonitor
AppVersion={#AppVersion}
AppPublisher=SwitchMonitor
DefaultDirName={%LOCALAPPDATA}\Programs\SwitchMonitor
DefaultGroupName=SwitchMonitor
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0
OutputDir={#Root}\dist
OutputBaseFilename=SwitchMonitor-Setup-{#AppVersion}
SetupIconFile={#Root}\monitor-switch.ico
UninstallDisplayIcon={app}\SwitchMonitor.exe
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
DisableProgramGroupPage=yes
AllowNoIcons=yes
CloseApplications=yes
CloseApplicationsFilter=SwitchMonitor.exe
RestartApplications=no
InfoBeforeFile={#Root}\packaging\ANTES-DE-INSTALAR.txt

[Languages]
Name: english; MessagesFile: compiler:Default.isl

[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"; Flags: unchecked
Name: startup; Description: "Start SwitchMonitor when I sign in to Windows"; Flags: unchecked

[Files]
Source: "{#Root}\dist\SwitchMonitor\*"; DestDir: "{app}"; Excludes: "portable.flag,data\*"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\SwitchMonitor"; Filename: "{app}\SwitchMonitor.exe"; WorkingDir: "{app}"
Name: "{group}\User guide"; Filename: "{app}\LEEME.txt"
Name: "{autodesktop}\SwitchMonitor"; Filename: "{app}\SwitchMonitor.exe"; Tasks: desktopicon
Name: "{userstartup}\SwitchMonitor"; Filename: "{app}\SwitchMonitor.exe"; Parameters: "--activate"; WorkingDir: "{app}"; Tasks: startup

[Run]
Filename: "{app}\SwitchMonitor.exe"; Description: "Open SwitchMonitor"; Flags: nowait postinstall skipifsilent

; User profiles in LocalAppData\SwitchMonitor are deliberately kept on uninstall.
