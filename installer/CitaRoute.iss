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
DisableDirPage=no
DisableProgramGroupPage=yes
DisableWelcomePage=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
MinVersion=10.0
WizardStyle=modern dark includetitlebar hidebevels
WizardBackColor=#0C0C0C
WizardImageBackColor=#0C0C0C
WizardSmallImageBackColor=#0C0C0C
WizardImageFile={#ArtworkDir}\welcome.bmp
WizardSmallImageFile={#ArtworkDir}\mark.bmp
WizardImageStretch=no
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

[Tasks]
Name: "autostart"; Description: "Открывать CITA Route в трее при входе в Windows"; Flags: unchecked

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#ProductName}"; ValueData: """{app}\client\RoutePilot.exe"" --startup"; Tasks: autostart; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "{#ProductName}"; Tasks: not autostart; Flags: deletevalue

[Messages]
russian.WelcomeLabel1=УСТАНОВКА CITA ROUTE
russian.WelcomeLabel2=Прямой доступ. Обход DPI. VPN.%n%nВыбери папку установки и настрой запуск приложения. Для подключения до входа в Windows включи отдельную опцию в настройках после выбора профиля.%n%nВерсия {#ProductVersion}
russian.SelectDirLabel3=Выбери папку для CITA Route. Профили и настройки будут храниться внутри неё.

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
  PreviousDirectory: String;

procedure InitializeWizard();
begin
  WizardForm.Font.Name := 'Segoe UI';
  RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#ProductId}_is1', 'Inno Setup: App Path', PreviousDirectory);
  WizardForm.WelcomeLabel1.Font.Name := 'Consolas';
  WizardForm.WelcomeLabel1.Font.Size := 22;
  WizardForm.WelcomeLabel1.Font.Style := [fsBold];
  WizardForm.PageNameLabel.Font.Name := 'Consolas';
  WizardForm.PageNameLabel.Font.Style := [fsBold];
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID <> wpSelectDir then exit;
  if (PreviousDirectory <> '') and (CompareText(RemoveBackslashUnlessRoot(WizardDirValue), RemoveBackslashUnlessRoot(PreviousDirectory)) <> 0) then begin
    MsgBox('Обновление устанавливается в прежнюю папку, чтобы сохранить подключение и настройки. Для переноса сначала удали приложение; личные данные сохраняются.', mbInformation, MB_OK);
    Result := False;
  end;
end;

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
  if (PreviousDirectory <> '') and (CompareText(RemoveBackslashUnlessRoot(ExpandConstant('{app}')), RemoveBackslashUnlessRoot(PreviousDirectory)) <> 0) then begin
    Result := 'Обновление должно использовать прежнюю папку установки. Для переноса сначала удали приложение.';
    exit;
  end;
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

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  StartupCommand: String;
begin
  if CurUninstallStep = usPostUninstall then begin
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', '{#ProductName}', StartupCommand) then begin
      if CompareText(StartupCommand, '"' + ExpandConstant('{app}\client\RoutePilot.exe') + '" --startup') = 0 then
        RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', '{#ProductName}');
    end;
  end;
end;
