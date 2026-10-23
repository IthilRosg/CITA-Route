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
  #define CopyHook "; AfterInstall: FixtureCopyFault"
#else
  #define ProductName "CITA Route"
  #define ProductId "CITA.Route.Client"
  #define OutputName "CITA-Route-" + ProductVersion + "-win-x64-setup"
  #define CopyHook ""
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
Source: "{#PayloadDir}\*"; DestDir: "{app}\client"; Excludes: "force-direct.txt,force-vpn.txt"; Flags: ignoreversion recursesubdirs createallsubdirs{#CopyHook}
; User rules survive reinstallation; the updater preserves the same files.
Source: "{#PayloadDir}\force-direct.txt"; DestDir: "{app}\client"; Flags: onlyifdoesntexist uninsneveruninstall
Source: "{#PayloadDir}\force-vpn.txt"; DestDir: "{app}\client"; Flags: onlyifdoesntexist uninsneveruninstall
Source: "InstallationGuard.ps1"; DestDir: "{app}\maintenance"; Flags: ignoreversion
Source: "InstallationTransaction.ps1"; DestDir: "{app}\maintenance"; Flags: ignoreversion
Source: "{#PayloadDir}\manifest.json"; DestName: "cita-install-manifest.json"; Flags: dontcopy
#ifdef NeedsDesktopRuntime
Source: "..\DesktopRuntime.ps1"; Flags: dontcopy
#endif

[Icons]
Name: "{userdesktop}\{#ProductName}"; Filename: "{app}\client\RoutePilot.exe"; WorkingDir: "{app}\client"; AppUserModelID: "CITA.Route.Client"
Name: "{userprograms}\{#ProductName}"; Filename: "{app}\client\RoutePilot.exe"; WorkingDir: "{app}\client"; AppUserModelID: "CITA.Route.Client"

[Run]
Filename: "{app}\client\RoutePilot.exe"; WorkingDir: "{app}\client"; Description: "Запустить CITA Route"; Flags: nowait postinstall skipifsilent runasoriginaluser; Check: InstallationCommitted

[Code]
var
  GuardReady, GuardRelease: String;
  GuardActive: Boolean;
  GuardFailure: Boolean;
  GuardAttempt: Integer;
  PreviousDirectory: String;
#ifdef NeedsDesktopRuntime
  RuntimeDownload: TDownloadWizardPage;
#endif

function GetCurrentProcessId(): LongWord;
  external 'GetCurrentProcessId@kernel32.dll stdcall';

#ifdef TestBuild
procedure FixtureCopyFault();
var
  Name: String;
begin
  Name := ExpandConstant('{param:FAILAFTERCOPY|}');
  if (Name <> '') and (CompareText(ExtractFileName(CurrentFileName), Name) = 0) then begin
    { The guest harness terminates this exact test Setup process after observing
      this marker. Ordinary exceptions in file callbacks can be ignored by Setup. }
    SaveStringToFile(ExpandConstant('{app}\client\runtime\fixture-copy-paused.pid'), IntToStr(GetCurrentProcessId()), False);
    Sleep(60000);
  end;
end;
#endif

function InstallationCommitted(): Boolean;
begin
  Result := not GuardFailure;
end;

function GetCustomSetupExitCode(): Integer;
begin
  if GuardFailure then Result := 7 else Result := 0;
end;

procedure InitializeWizard();
begin
  WizardForm.Font.Name := 'Segoe UI';
  RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#ProductId}_is1', 'Inno Setup: App Path', PreviousDirectory);
  WizardForm.WelcomeLabel1.Font.Name := 'Consolas';
  WizardForm.WelcomeLabel1.Font.Size := 22;
  WizardForm.WelcomeLabel1.Font.Style := [fsBold];
  WizardForm.PageNameLabel.Font.Name := 'Consolas';
  WizardForm.PageNameLabel.Font.Style := [fsBold];
#ifdef NeedsDesktopRuntime
  RuntimeDownload := CreateDownloadPage('ПОДГОТОВКА .NET', 'Загрузка компонента Microsoft для запуска CITA Route', nil);
  RuntimeDownload.ShowBaseNameInsteadOfUrl := True;
#endif
end;

#ifdef NeedsDesktopRuntime
function CheckRuntime(): Integer;
var
  Code: Integer;
begin
  ExtractTemporaryFile('DesktopRuntime.ps1');
  if Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + ExpandConstant('{tmp}\DesktopRuntime.ps1') + '" -Action Check',
    '', SW_HIDE, ewWaitUntilTerminated, Code) then Result := Code else Result := -1;
end;

function PrepareRuntime(var NeedsRestart: Boolean): String;
var
  Code, Check: Integer;
  FileName, Args: String;
  VerificationDetail: AnsiString;
begin
  Result := '';
  Check := CheckRuntime();
  if Check = 0 then exit;
  if Check <> 10 then begin Result := 'Не удалось проверить .NET Desktop Runtime. Установка клиента не начата.'; exit; end;
  if WizardSilent and (ExpandConstant('{param:INSTALLRUNTIME|0}') <> '1') then begin
    Result := 'Требуется .NET Desktop Runtime 10.0.13 x64. Запусти обычную установку CITA Route: она предложит загрузить компонент Microsoft.';
    exit;
  end;
  if not WizardSilent then begin
    if MsgBox('Для запуска нужен Microsoft .NET Desktop Runtime 10.0.13 (x64). Скачать и установить его сейчас? Компонент устанавливается один раз; Windows запросит права администратора.', mbConfirmation, MB_YESNO) <> IDYES then begin
      Result := 'Подготовка .NET отменена. Файлы CITA Route не изменены.'; exit;
    end;
  end;
  RuntimeDownload.Clear;
  RuntimeDownload.Add('{#RuntimeUrl}', 'windowsdesktop-runtime.exe', '');
  RuntimeDownload.Show;
  try
    try RuntimeDownload.Download;
    except Result := 'Не удалось загрузить .NET: ' + GetExceptionMessage; end;
  finally RuntimeDownload.Hide; end;
  if Result <> '' then exit;
  FileName := ExpandConstant('{tmp}\windowsdesktop-runtime.exe');
  Args := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + ExpandConstant('{tmp}\DesktopRuntime.ps1') +
    '" -Action Verify -Installer "' + FileName + '" -Sha512 "{#RuntimeSha512}" -ResultPath "' + ExpandConstant('{tmp}\runtime-verification.txt') + '"';
  if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Args, '', SW_HIDE, ewWaitUntilTerminated, Code) then begin
    Result := 'Не удалось проверить загруженный компонент .NET.'; exit;
  end;
  if Code <> 0 then begin
    if LoadStringFromFile(ExpandConstant('{tmp}\runtime-verification.txt'), VerificationDetail) then Log('Runtime verification: ' + String(VerificationDetail));
    Result := 'Проверка контрольной суммы или подписи Microsoft не пройдена. Компонент не запущен.'; exit;
  end;
  if not ShellExec('runas', FileName, '/install /passive /norestart', '', SW_SHOWNORMAL, ewWaitUntilTerminated, Code) then begin
    Result := 'Установка .NET отменена или недоступна. Файлы CITA Route не изменены.'; exit;
  end;
  if (Code = 3010) or (Code = 1641) then NeedsRestart := True
  else if Code <> 0 then begin Result := 'Установка .NET не завершилась. Код: ' + IntToStr(Code); exit; end;
  if CheckRuntime() <> 0 then Result := 'Компонент .NET ещё недоступен. Перезагрузи Windows, если установщик Microsoft запросил перезапуск, и повтори установку.';
end;
#endif

function CanUpdateDestination(Directory: String): Boolean;
begin
  Result := (PreviousDirectory = '') or
    (CompareText(RemoveBackslashUnlessRoot(Directory), RemoveBackslashUnlessRoot(PreviousDirectory)) = 0) or
    (FileExists(AddBackslash(Directory) + 'client\manifest.json') and FileExists(AddBackslash(Directory) + 'client\runtime\installation-transaction.json'));
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID <> wpSelectDir then exit;
  if not CanUpdateDestination(WizardDirValue) then begin
    SuppressibleMsgBox('Обновление устанавливается в прежнюю папку, чтобы сохранить подключение и настройки. Для переноса сначала удали приложение; личные данные сохраняются.', mbInformation, MB_OK, IDOK);
    Result := False;
  end;
end;

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
  if not Removing then Args := Args + ' -IncomingManifest "' + ExpandConstant('{tmp}\cita-install-manifest.json') + '"';
  if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Args, '', SW_HIDE, ewNoWait, Code) then begin
    Result := 'Не удалось проверить установку. ' + SysErrorMessage(Code);
    exit;
  end;
  for I := 1 to 600 do begin
    if LoadStringsFromFile(GuardReady, State) and (GetArrayLength(State) > 0) then begin
      if State[0] = 'READY' then GuardActive := True
      else Result := State[0];
      exit;
    end;
    Sleep(100);
  end;
  SaveStringToFile(GuardRelease, 'ROLLBACK', False);
  Result := 'Проверка установки не завершилась. Изменения отменены. Повтори попытку.';
end;

function ReleaseGuard(Commit: Boolean): String;
var
  I: Integer;
  State: TArrayOfString;
begin
  Result := '';
  if not GuardActive then exit;
  if Commit then SaveStringToFile(GuardRelease, 'COMMIT', False)
  else SaveStringToFile(GuardRelease, 'ROLLBACK', False);
  for I := 1 to 600 do begin
    if LoadStringsFromFile(GuardReady, State) and (GetArrayLength(State) > 0) then begin
      if State[0] = 'DONE' then begin GuardActive := False; exit; end;
      if State[0] <> 'READY' then begin Result := State[0]; GuardActive := False; exit; end;
    end;
    Sleep(100);
  end;
  SaveStringToFile(GuardRelease, 'ROLLBACK', False);
  Result := 'Восстановление установки ещё не подтверждено. Дождись его завершения и повтори установщик.';
  GuardActive := False;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  if not CanUpdateDestination(ExpandConstant('{app}')) then begin
    Result := 'Обновление должно использовать прежнюю папку установки. Для переноса сначала удали приложение.';
    exit;
  end;
  { A previously committed installation at the explicitly selected destination
    may have a stale registration pointing elsewhere. The guard still validates
    its root/SID and complete transaction before it can accept replacement. }
#ifdef NeedsDesktopRuntime
  Result := PrepareRuntime(NeedsRestart);
  if Result <> '' then exit;
#endif
  if GuardActive then begin Result := ''; exit; end;
  ExtractTemporaryFile('InstallationGuard.ps1');
  ExtractTemporaryFile('InstallationTransaction.ps1');
  ExtractTemporaryFile('cita-install-manifest.json');
  Result := AcquireGuard(ExpandConstant('{tmp}\InstallationGuard.ps1'), False);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Problem: String;
begin
  { The installed application can open only after file replacement is complete. }
  if CurStep = ssPostInstall then begin
    Problem := ReleaseGuard(True);
    if Problem <> '' then begin
      GuardFailure := True;
      WizardForm.FinishedHeadingLabel.Caption := 'Установка не подтверждена';
      WizardForm.FinishedLabel.Caption := Problem;
      RaiseException(Problem);
    end;
  end;
end;

procedure DeinitializeSetup();
begin
  ReleaseGuard(False);
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
  ReleaseGuard(False);
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
