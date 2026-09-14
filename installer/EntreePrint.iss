#ifndef PackageRoot
  #error PackageRoot is required. Use scripts/build-installer.ps1.
#endif
#ifndef OutputRoot
  #error OutputRoot is required. Use scripts/build-installer.ps1.
#endif
#define ProductVersion "0.0.1-beta"

[Setup]
AppId={{523F3547-20CD-4F58-A10A-180A7FBBFCF7}
AppName=Entree Print
AppVersion={#ProductVersion}
AppVerName=Entree Print {#ProductVersion}
AppPublisher=Entree
AppPublisherURL=https://github.com/EntreePOS/entree-print
AppSupportURL=https://github.com/EntreePOS/entree-print/issues
AppUpdatesURL=https://github.com/EntreePOS/entree-print/releases
DefaultDirName={autopf}\Entree Print
DefaultGroupName=Entree Print
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
WizardStyle=modern
SetupIconFile=..\assets\entree-print.ico
UninstallDisplayIcon={app}\tray\EntreePrintTray.exe
VersionInfoVersion=0.0.1.0
VersionInfoProductVersion=0.0.1.0
VersionInfoProductTextVersion={#ProductVersion}
OutputDir={#OutputRoot}
OutputBaseFilename=EntreePrint-{#ProductVersion}-win-x64-setup
Compression=lzma2
SolidCompression=yes
AppMutex=Local\EntreePrintPlugin.Tray
SetupMutex=EntreePrint.Setup
CloseApplications=no
RestartApplications=no
Uninstallable=yes
InfoBeforeFile=INSTALL-NOTES.txt

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#PackageRoot}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "service-lifecycle.ps1"; DestDir: "{app}\setup"; Flags: ignoreversion
Source: "service-lifecycle.ps1"; DestName: "entree-service-check.ps1"; Flags: dontcopy

[Icons]
Name: "{group}\Entree Print"; Filename: "{app}\tray\EntreePrintTray.exe"; WorkingDir: "{app}\tray"
Name: "{group}\Usage guide"; Filename: "https://entreepos.github.io/entree-print/"
Name: "{group}\Playground"; Filename: "https://entreepos.github.io/entree-print/playground.html"
Name: "{group}\Uninstall Entree Print"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Entree Print"; Filename: "{app}\tray\EntreePrintTray.exe"; WorkingDir: "{app}\tray"; Tasks: desktopicon

[Run]
Filename: "{app}\tray\EntreePrintTray.exe"; Description: "Open Entree Print settings"; Flags: postinstall nowait skipifsilent runasoriginaluser; Check: StorageReady

[Code]
var
  StorageInitialized: Boolean;

function StorageReady(): Boolean;
begin
  Result := StorageInitialized;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
begin
  if CurStep <> ssPostInstall then Exit;
  StorageInitialized := Exec(ExpandConstant('{app}\tray\EntreePrintTray.exe'), '--initialize-storage', '', SW_HIDE, ewWaitUntilTerminated, Code);
  StorageInitialized := StorageInitialized and (Code = 0);
  if not StorageInitialized then begin
    Log('Protected data storage could not be initialized. Application files are installed, but printing was not enabled.');
    if not WizardSilent then MsgBox('Entree Print could not prepare administrator-protected settings and receipt storage in ' + ExpandConstant('{commonappdata}\EntreePrintPlugin') + '. Existing data was not adopted or reset. Review its ownership and permissions before using this installation.', mbError, MB_OK);
  end;
end;

function GetCustomSetupExitCode(): Integer;
begin
  if StorageInitialized then Result := 0 else Result := 12;
end;

function RunServiceCheck(const ScriptPath, Action: String): String;
var
  ErrorPath, Params: String;
  Details: AnsiString;
  Code: Integer;
begin
  Result := '';
  ErrorPath := ExpandConstant('{tmp}\entree-setup-error.txt');
  DeleteFile(ErrorPath);
  Params := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + ScriptPath +
    '" -Action ' + Action + ' -InstallRoot "' + ExpandConstant('{app}') + '" -ErrorFile "' + ErrorPath + '"';
  if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Params, '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Result := 'Windows could not run the Entree Print service check.'
  else if Code <> 0 then begin
    if LoadStringFromFile(ErrorPath, Details) then Result := Details
    else Result := 'The service check failed. Setup has not removed any application files.';
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  ExtractTemporaryFile('entree-service-check.ps1');
  Result := RunServiceCheck(ExpandConstant('{tmp}\entree-service-check.ps1'), 'Check');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Error: String;
begin
  if CurUninstallStep <> usUninstall then Exit;
  Error := RunServiceCheck(ExpandConstant('{app}\setup\service-lifecycle.ps1'), 'Uninstall');
  if Error <> '' then RaiseException(Error + #13#10 + 'Uninstall was stopped; application files and receipt data remain.');
end;
