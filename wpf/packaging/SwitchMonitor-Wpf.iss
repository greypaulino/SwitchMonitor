#ifndef AppVersion
#define AppVersion "0.1.0"
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

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    SaveStringToFile(ExpandConstant('{app}\installed-wpf.flag'), 'installed', False);
end;
