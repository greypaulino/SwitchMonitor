#define AppVersion "1.6.5"
#define Root SourcePath + ".."
#ifndef Interpreted
#define Interpreted 0
#endif

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
#if Interpreted
Name: "{group}\SwitchMonitor"; Filename: "{app}\SwitchMonitor.exe"; Parameters: """{app}\switchMonitor.ahk"""; WorkingDir: "{app}"
Name: "{group}\User guide"; Filename: "{app}\LEEME.txt"
Name: "{autodesktop}\SwitchMonitor"; Filename: "{app}\SwitchMonitor.exe"; Parameters: """{app}\switchMonitor.ahk"""; Check: WantDesktopShortcut
Name: "{userstartup}\SwitchMonitor"; Filename: "{app}\SwitchMonitor.exe"; Parameters: """{app}\switchMonitor.ahk"" --activate"; WorkingDir: "{app}"; Check: WantStartupShortcut
#else
Name: "{group}\SwitchMonitor"; Filename: "{app}\SwitchMonitor.exe"; WorkingDir: "{app}"
Name: "{group}\User guide"; Filename: "{app}\LEEME.txt"
Name: "{autodesktop}\SwitchMonitor"; Filename: "{app}\SwitchMonitor.exe"; Tasks: desktopicon
Name: "{userstartup}\SwitchMonitor"; Filename: "{app}\SwitchMonitor.exe"; Parameters: "--activate"; WorkingDir: "{app}"; Tasks: startup
#endif

[Run]
#if Interpreted
Filename: "{app}\SwitchMonitor.exe"; Parameters: """{app}\switchMonitor.ahk"""; Description: "Open SwitchMonitor"; Flags: nowait postinstall skipifsilent
#else
Filename: "{app}\SwitchMonitor.exe"; Description: "Open SwitchMonitor"; Flags: nowait postinstall skipifsilent
#endif

[Code]
var
  WasInstalled: Boolean;

#if Interpreted
function WantDesktopShortcut: Boolean;
begin
  Result := WizardIsTaskSelected('desktopicon') or FileExists(ExpandConstant('{autodesktop}\SwitchMonitor.lnk'));
end;

function WantStartupShortcut: Boolean;
begin
  Result := WizardIsTaskSelected('startup') or FileExists(ExpandConstant('{userstartup}\SwitchMonitor.lnk'));
end;
#endif

procedure CurStepChanged(CurStep: TSetupStep);
var
  DataDir: String;
begin
  if CurStep = ssInstall then
    WasInstalled := FileExists(ExpandConstant('{app}\installed.flag'));
  if (CurStep = ssPostInstall) and WasInstalled then
  begin
    DataDir := ExpandConstant('{localappdata}\SwitchMonitor');
    if ForceDirectories(DataDir) then
      SaveStringToFile(DataDir + '\update-complete.ini',
        '[Update]'#13#10'Version={#AppVersion}'#13#10, False);
  end;
end;
