; DragDone installer — Inno Setup 6.
;
; Build: pwsh tools/build_installer.ps1
; Produces artifacts\DragDone-<version>-setup.exe, a single file that installs the app and the
; .NET runtime it needs, so nothing else has to be downloaded.

#define AppName "DragDone"
#define AppPublisher "Turki Alshaalan"
#define AppUrl "https://github.com/trookish/DragDone"

#ifndef Version
  #define Version "1.0.0"
#endif

#ifndef SourceDir
  #define SourceDir "..\artifacts\install-src"
#endif

[Setup]
; A stable AppId: upgrades replace the previous install instead of stacking up.
AppId={{AEEDDDF4-965A-40CA-A001-FE004E7571A1}
AppName={#AppName}
AppVersion={#Version}
AppVerName={#AppName} {#Version}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#Version}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} setup
VersionInfoProductName={#AppName}

; Installs for the current user by default — no UAC for a tray utility — with the option of an
; all-users install from the wizard.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
AllowNoIcons=yes
DisableProgramGroupPage=yes
DisableDirPage=auto

; Windows 10 1809, matching the app's SupportedOSPlatformVersion.
MinVersion=10.0.17763
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

LicenseFile=..\LICENSE
SetupIconFile=..\src\DragDone\Assets\DragDone.ico
UninstallDisplayIcon={app}\DragDone.exe
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
OutputDir=..\artifacts
OutputBaseFilename={#AppName}-{#Version}-setup
ChangesAssociations=no
; The app is copied whole; a half-written update is worse than a prompt.
CloseApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "arabic"; MessagesFile: "compiler:Languages\Arabic.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\DragDone.exe"; WorkingDir: "{app}"; Comment: "Drag • Press • Done"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\DragDone.exe"; WorkingDir: "{app}"; Comment: "Drag • Press • Done"; Tasks: desktopicon

[Run]
Filename: "{app}\DragDone.exe"; WorkingDir: "{app}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  RunValue = 'DragDone';

{ A tray app has no window to close, so it is terminated outright before its files are replaced. }
procedure StopRunning;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM DragDone.exe', '',
       SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(400);
end;

function InitializeSetup(): Boolean;
begin
  StopRunning;
  Result := True;
end;

{ The app adds its own Run entry the first time it starts; removing the app has to take it away,
  otherwise Windows would keep trying to start a path that no longer exists. }
procedure RemoveStartupEntry;
begin
  RegDeleteValue(HKEY_CURRENT_USER, RunKey, RunValue);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep <> usUninstall then Exit;

  StopRunning;
  RemoveStartupEntry;

  { Settings live outside the install folder and survive by default. Offer to clear them. }
  DataDir := ExpandConstant('{userappdata}\DragDone');
  if DirExists(DataDir) then
    if MsgBox('Remove your DragDone settings and theme as well?' + #13#10 + #13#10 +
              DataDir, mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(DataDir, True, True, True);
end;
