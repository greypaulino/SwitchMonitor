#ifndef AppVersion
#define AppVersion "0.1.1"
#endif
#define WpfRoot SourcePath + ".."

[Setup]
AppId={{EC388606-1A84-40DA-8B3F-4EA63E411764}
AppName=SwitchMonitor WPF
AppVersion={#AppVersion}
AppPublisher=SwitchMonitor
DefaultDirName={%LOCALAPPDATA}\Programs\SwitchMonitor WPF
DefaultGroupName=SwitchMonitor WPF
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0
OutputDir={#WpfRoot}\dist
OutputBaseFilename=SwitchMonitor-WPF-Setup-{#AppVersion}
SetupIconFile={#WpfRoot}\..\monitor-switch.ico
InfoBeforeFile={#WpfRoot}\..\PRIVACY.md
UninstallDisplayIcon={app}\SwitchMonitor.Wpf.exe
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
DisableProgramGroupPage=yes
CloseApplications=yes
CloseApplicationsFilter=SwitchMonitor.Wpf.exe
RestartApplications=no

[Languages]
Name: english; MessagesFile: compiler:Default.isl

[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"; Flags: unchecked
Name: startup; Description: "Start SwitchMonitor WPF when I sign in to Windows"; Flags: unchecked
Name: autoupdates; Description: "Check GitHub for updates automatically"

[Files]
Source: "{#WpfRoot}\dist\package-build\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\SwitchMonitor WPF"; Filename: "{app}\SwitchMonitor.Wpf.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\SwitchMonitor WPF"; Filename: "{app}\SwitchMonitor.Wpf.exe"; Tasks: desktopicon
Name: "{userstartup}\SwitchMonitor WPF"; Filename: "{app}\SwitchMonitor.Wpf.exe"; Parameters: "--background"; WorkingDir: "{app}"; Tasks: startup

[Run]
Filename: "{app}\SwitchMonitor.Wpf.exe"; Description: "Open SwitchMonitor WPF"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: files; Name: "{app}\installed-wpf.flag"
Type: files; Name: "{app}\disable-auto-updates.flag"

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    SaveStringToFile(ExpandConstant('{app}\installed-wpf.flag'), 'installed', False);
    if not FileExists(ExpandConstant('{localappdata}\SwitchMonitor-Wpf\settings.json')) then
    begin
      if WizardIsTaskSelected('autoupdates') then
        DeleteFile(ExpandConstant('{app}\disable-auto-updates.flag'))
      else
        SaveStringToFile(ExpandConstant('{app}\disable-auto-updates.flag'), 'disabled', False);
    end;
  end;
end;
