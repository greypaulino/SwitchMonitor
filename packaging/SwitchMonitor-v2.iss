; SwitchMonitor v2 installer. Same AppId and folder as the 1.x installer, so a 1.x install is replaced in place and the
; 1.x updater (which looks for SwitchMonitor-Setup-<version>.exe + SHA256.json) can update to this version by itself.
; Build with run-installer.ps1 (passes AppVersion, SourceDir and OutputDir).
#ifndef AppVersion
#define AppVersion "2.0.0"
#endif
#ifndef SourceDir
#define SourceDir SourcePath + "..\results\release-" + AppVersion + "\publish"
#endif
#ifndef OutputDir
#define OutputDir SourcePath + "..\results\release-" + AppVersion
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
OutputDir={#OutputDir}
OutputBaseFilename=SwitchMonitor-Setup-{#AppVersion}
SetupIconFile={#SourcePath}..\monitor-switch.ico
UninstallDisplayIcon={app}\SwitchMonitor.exe
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
DisableProgramGroupPage=yes
AllowNoIcons=yes
CloseApplications=yes
CloseApplicationsFilter=SwitchMonitor.exe
RestartApplications=no

[Languages]
Name: english; MessagesFile: compiler:Default.isl

[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"; Flags: unchecked

[InstallDelete]
; What a 1.x install left (the AutoHotkey scripts, its helper and ControlMyMonitor): the v2 is one self-contained exe.
Type: files; Name: "{app}\*.ahk"
Type: files; Name: "{app}\Update-Helper.ps1"
Type: files; Name: "{app}\portable.flag"
Type: filesandordirs; Name: "{app}\ControlMyMonitor"
Type: filesandordirs; Name: "{app}\source"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\SwitchMonitor"; Filename: "{app}\SwitchMonitor.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\SwitchMonitor"; Filename: "{app}\SwitchMonitor.exe"; Tasks: desktopicon

[Run]
; After a normal install the page offers to open it; after the silent update the app comes back by itself.
Filename: "{app}\SwitchMonitor.exe"; Description: "Open SwitchMonitor"; Flags: nowait postinstall skipifsilent
Filename: "{app}\SwitchMonitor.exe"; Parameters: "--updated"; Flags: nowait; Check: WizardSilent

[Code]
var
  WasInstalled: Boolean;

procedure CurStepChanged(CurStep: TSetupStep);
var
  DataDir: String;
begin
  if CurStep = ssInstall then
    WasInstalled := FileExists(ExpandConstant('{app}\installed.flag')) or FileExists(ExpandConstant('{app}\SwitchMonitor.exe'));
  if CurStep = ssPostInstall then
  begin
    { The installed edition updates itself; a portable copy does not have this file. }
    SaveStringToFile(ExpandConstant('{app}\installed.flag'), 'Installed edition: it updates itself.', False);
    if WasInstalled then
    begin
      { The app shows "installed successfully" once, the next time it starts. }
      DataDir := ExpandConstant('{localappdata}\SwitchMonitor-v2');
      if ForceDirectories(DataDir) then
        SaveStringToFile(DataDir + '\update-complete.ini', '[Update]'#13#10'Version={#AppVersion}'#13#10, False);
    end;
  end;
end;
