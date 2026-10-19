#ifndef PayloadDir
  #error PayloadDir is required
#endif
#ifndef ProductVersion
  #error ProductVersion is required
#endif
#ifdef TestBuild
  #define ProductName "CITA Route Installer Test"
  #define ProductId "CITA.Route.InstallerFixture"
  #define OutputName "CITA-Route-" + ProductVersion + "-win-x64-test-setup"
#else
  #define ProductName "CITA Route"
  #define ProductId "CITA.Route.Client"
  #define OutputName "CITA-Route-" + ProductVersion + "-win-x64-setup"
#endif

[Setup]
AppId={#ProductId}
AppName={#ProductName}
AppVersion={#ProductVersion}
AppPublisher=IthilRosg
AppPublisherURL=https://github.com/IthilRosg/CITA-Route
AppSupportURL=https://github.com/IthilRosg/CITA-Route/issues
DefaultDirName={localappdata}\Programs\{#ProductName}
DefaultGroupName={#ProductName}
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableWelcomePage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
MinVersion=10.0
WizardStyle=modern
SetupIconFile=..\app\Assets\RoutePilot.ico
UninstallDisplayIcon={app}\client\RoutePilot.exe
UninstallDisplayName={#ProductName}
VersionInfoProductName={#ProductName}
OutputDir={#OutputDir}
OutputBaseFilename={#OutputName}
Compression=lzma2/fast
SolidCompression=yes
CloseApplications=no
RestartApplications=no
SetupMutex=Local\CITA.Route.Setup
LicenseFile=..\LICENSE

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
; Public payload only: generated from the verified portable manifest.
Source: "{#PayloadDir}\*"; DestDir: "{app}\client"; Excludes: "force-direct.txt,force-vpn.txt"; Flags: ignoreversion recursesubdirs createallsubdirs
; User rules survive reinstallation; the updater preserves the same files.
Source: "{#PayloadDir}\force-direct.txt"; DestDir: "{app}\client"; Flags: onlyifdoesntexist uninsneveruninstall
Source: "{#PayloadDir}\force-vpn.txt"; DestDir: "{app}\client"; Flags: onlyifdoesntexist uninsneveruninstall
Source: "InstallationGuard.ps1"; DestDir: "{app}\maintenance"; Flags: ignoreversion

[Icons]
Name: "{userdesktop}\{#ProductName}"; Filename: "{app}\client\RoutePilot.exe"; WorkingDir: "{app}\client"; AppUserModelID: "CITA.Route.Client"
Name: "{userprograms}\{#ProductName}"; Filename: "{app}\client\RoutePilot.exe"; WorkingDir: "{app}\client"; AppUserModelID: "CITA.Route.Client"

[Run]
Filename: "{app}\client\RoutePilot.exe"; WorkingDir: "{app}\client"; Description: "Запустить CITA Route"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
var
  GuardReady, GuardRelease: String;
  GuardActive: Boolean;
  GuardAttempt: Integer;

function GetCurrentProcessId(): LongWord;
  external 'GetCurrentProcessId@kernel32.dll stdcall';

function AcquireGuard(ScriptPath: String; Removing: Boolean): String;
var
  Args: String;
  Code, I: Integer;
  State: TArrayOfString;
begin
  Result := '';
  GuardAttempt := GuardAttempt + 1;
  GuardReady := ExpandConstant('{tmp}\cita-maintenance-') + IntToStr(GuardAttempt) + '.ready';
  GuardRelease := GuardReady + '.release';
  DeleteFile(GuardReady);
  DeleteFile(GuardRelease);
  Args := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + ScriptPath +
    '" -Root "' + ExpandConstant('{app}\client') + '" -OwnerPid ' + IntToStr(GetCurrentProcessId()) +
    ' -Version "{#ProductVersion}" -ReadyFile "' + GuardReady + '" -ReleaseFile "' + GuardRelease + '"';
  if Removing then Args := Args + ' -Mode Uninstall';
  if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Args, '', SW_HIDE, ewNoWait, Code) then begin
    Result := 'Не удалось проверить установку. ' + SysErrorMessage(Code);
    exit;
  end;
  for I := 1 to 200 do begin
    if LoadStringsFromFile(GuardReady, State) and (GetArrayLength(State) > 0) then begin
      if State[0] = 'READY' then GuardActive := True
      else Result := State[0];
      exit;
    end;
    Sleep(100);
  end;
  SaveStringToFile(GuardRelease, 'STOP', False);
  Result := 'Проверка установки не завершилась. Изменения отменены. Повтори попытку.';
end;

procedure ReleaseGuard();
var
  I: Integer;
  State: TArrayOfString;
begin
  if not GuardActive then exit;
  SaveStringToFile(GuardRelease, 'STOP', False);
  for I := 1 to 100 do begin
    if LoadStringsFromFile(GuardReady, State) and (GetArrayLength(State) > 0) and (State[0] = 'DONE') then break;
    Sleep(50);
  end;
  GuardActive := False;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  if GuardActive then begin Result := ''; exit; end;
  ExtractTemporaryFile('InstallationGuard.ps1');
  Result := AcquireGuard(ExpandConstant('{tmp}\InstallationGuard.ps1'), False);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  { The installed application can open only after file replacement is complete. }
  if CurStep = ssPostInstall then ReleaseGuard();
end;

procedure DeinitializeSetup();
begin
  ReleaseGuard();
end;

function InitializeUninstall(): Boolean;
var
  Problem, ScriptPath: String;
begin
  ScriptPath := ExpandConstant('{tmp}\InstallationGuard.ps1');
  Result := FileCopy(ExpandConstant('{app}\maintenance\InstallationGuard.ps1'), ScriptPath, False);
  if not Result then begin
    SuppressibleMsgBox('Не удалось проверить подключение перед удалением. Повтори установку клиента для восстановления.', mbError, MB_OK, IDOK);
    exit;
  end;
  Problem := AcquireGuard(ScriptPath, True);
  Result := Problem = '';
  if not Result then SuppressibleMsgBox(Problem, mbError, MB_OK, IDOK);
end;

procedure DeinitializeUninstall();
begin
  ReleaseGuard();
end;
