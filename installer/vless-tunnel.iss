; Установщик vless-tunnel (план, 3.7/3.8) — Inno Setup.
;
; Живьём прогнан на стенде (план, этап 6): чистая установка, обновление
; поверх, удаление с «Да»/«Нет», переустановка, тихое удаление —
; реальным ISCC.exe 6.7.3. Сертификат подписи выпущен, secrets в GitHub
; Actions заведены, вся цепочка доверия (подпись → trust.ps1 → Valid)
; проверена живьём — см. PLAN-windows.md. Не проверено — только
; настоящий signtool.exe (на стенде нет Windows SDK, подмена через
; Set-AuthenticodeSignature; сам CI на windows-latest его использует).
;
; Ожидает, что CI (.github/workflows/build.yml) соберёт в {#SourceDir}
; ОДНУ папку со всеми тремя self-contained публикациями (Service/Cli/Tray
; для win-x64 в один каталог — они делят большую часть рантайм-DLL,
; поэтому publish всех трёх подряд в одну папку без очистки между ними
; безопасен и не дублирует файлы) плюс xray.exe/wintun.dll/geoip.dat/
; geosite.dat (версия и sha256 — vm/pinned-versions.txt, план: "версия
; Xray и wintun зашиты в сборку, sha256 проверяется в CI") плюс иконки
; из packaging/icons.

#define MyAppName "vless-tunnel"
#define MyAppPublisher "vless-tunnel"
#ifndef MyAppVersion
  #define MyAppVersion "0.0.0-dev"
#endif
#define MyServiceExeName "VlessTunnel.Service.exe"
#define MyTrayExeName "VlessTunnel.Tray.exe"
#define MyCliExeName "VlessTunnel.Cli.exe"
#define MyServiceName "vless-tunnel"
#ifndef SourceDir
  #define SourceDir "..\dist"
#endif
; Отпечаток самоподписанного сертификата — подставляется CI при сборке
; (/DCertThumbprint=...), никогда не хранится в репозитории как секрет
; (сам отпечаток не секрет, секрет — .pfx, который сюда не попадает
; вовсе; план, 3.8: ".pfx хранится только в секретах GitHub Actions и в
; офлайн-копии, никогда в репозитории"). Пустое значение по умолчанию —
; деинсталлятор просто пропускает шаги снятия доверия, если не задано.
#ifndef CertThumbprint
  #define CertThumbprint ""
#endif
; Директива SignTool= требует, чтобы соответствующий /Ssigntool=...
; ВСЕГДА был передан в командной строке ISCC — если его нет (сборка без
; секретов подписи), сам факт присутствия директивы валит компиляцию с
; "Value of [Setup] section directive SignTool is invalid" ещё на
; разборе [Setup], до какого-либо реального шага подписи (найдено живым
; прогоном CI на GitHub Actions — падало на КАЖДОМ пуше, а не только
; там, где секретов нет: директива безусловная, а /Ssigntool CI кладёт
; только при наличии секретов). Поэтому весь блок подписи — под тем же
; переключателем, что и сам /Ssigntool в build.yml (/DSignInstaller=1).
#ifndef SignInstaller
  #define SignInstaller ""
#endif

[Setup]
AppId={{B7E4B7B4-6B9E-4C7C-9B1A-9F2E7B7C9A1D}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\vless-tunnel
DefaultGroupName=vless-tunnel
DisableProgramGroupPage=yes
OutputBaseFilename=vless-tunnel-setup
Compression=lzma2
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
UninstallDisplayIcon={app}\{#MyTrayExeName}
#if SignInstaller != ""
; Реальная подпись — именованный инструмент, команду которой задаёт CI
; через ISCC /Ssigntool=... (сам .pfx там же, из секретов, никогда в
; репозитории). Присутствует только когда CI реально передаёт
; /DSignInstaller=1 вместе с /Ssigntool= — иначе директива без
; определения ломает компиляцию (см. комментарий у #define выше).
SignTool=signtool
SignedUninstaller=yes
#endif

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Files]
; Один путь к бинарникам, без копий (план, 3.7: урок Linux про дубль
; /usr/bin и /usr/local/bin) — все три exe и их общий рантайм лежат
; рядом в {app}, ничего не копируется ни в System32, ни ещё куда-то.
; Ревью п.9: план (3.8) обещает подпись "всех своих .exe", но у
; wildcard-записи ниже не было флага sign вовсе — ни один файл, включая
; сами VlessTunnel.*.exe, не подписывался, только сам Setup.exe/
; unins000.exe. Три СВОИХ exe выделены в отдельные записи с sign (только
; когда SignInstaller реально включён — тот же переключатель, что и для
; SignTool= выше, иначе sign без действующего SignTool= сломает
; компиляцию тем же способом). Не весь рантайм .NET — сотни чужих DLL
; подписывать незачем и долго, план требует подписи именно своих exe.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,{#MyServiceExeName},{#MyCliExeName},{#MyTrayExeName}"
#if SignInstaller != ""
Source: "{#SourceDir}\{#MyServiceExeName}"; DestDir: "{app}"; Flags: ignoreversion sign
Source: "{#SourceDir}\{#MyCliExeName}"; DestDir: "{app}"; Flags: ignoreversion sign
Source: "{#SourceDir}\{#MyTrayExeName}"; DestDir: "{app}"; Flags: ignoreversion sign
#else
Source: "{#SourceDir}\{#MyServiceExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\{#MyCliExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\{#MyTrayExeName}"; DestDir: "{app}"; Flags: ignoreversion
#endif

[Icons]
Name: "{group}\vless-tunnel"; Filename: "{app}\{#MyTrayExeName}"
Name: "{group}\Удалить vless-tunnel"; Filename: "{uninstallexe}"
; Без Tasks: — раньше зависело от чекбокса "autostarttray" (checkedonce),
; но /VERYSILENT/-SILENT (в том числе self-update — он всегда тихий)
; ничего не выбирают, даже checkedonce-задачи, если не передать /TASKS=
; явно (живой тест: свежая /VERYSILENT-установка v0.1.0 давала службу
; DEMAND_START несмотря на чекбокс "по умолчанию включён" в .iss —
; нашлось это именно так, экспериментом, а не из документации). Раз
; молчаливая тихая установка не обещает того же выбора, что и мастер,
; проще не завязываться на неё вовсе: автозапуск трея и службы (см.
; StartType ниже) — теперь всегда, без опции отключить в мастере.
Name: "{commonstartup}\vless-tunnel"; Filename: "{app}\{#MyTrayExeName}"

[Run]
Filename: "{app}\{#MyTrayExeName}"; Description: "Запустить vless-tunnel сейчас"; Flags: postinstall nowait skipifsilent unchecked

[UninstallRun]
; Настройки удаляет сам деинсталлятор по ответу пользователя (см. [Code]
; ниже) — CLI их не трогает (план, 3.7: "uninstall из CLI не удаляет
; файлы... а только сбрасывает настройку"). Команда uninstall ещё не
; реализована на сервере (этап 4 её честно отклоняет) — строка ниже
; безопасный no-op до тех пор, не блокирует остальную деинсталляцию.
Filename: "{app}\{#MyCliExeName}"; Parameters: "uninstall --keep-config"; Flags: runhidden waituntilterminated; RunOnceId: "CliUninstall"
; Остановка службы, doctor и sc delete — ТЕПЕРЬ в Pascal
; (StopAndCleanupServiceForUninstall, вызывается из usUninstall), не
; декларативными записями здесь: без этого doctor снимал WFP-фильтры
; ДО того, как sc stop гарантированно остановил службу (sc stop
; асинхронен — здесь раньше не ждали, ревью п.6), то есть у ЖИВОЙ
; службы с потенциально поднятым туннелем, а не у уже мёртвой.
#if CertThumbprint != ""
Filename: "{sys}\certutil.exe"; Parameters: "-delstore Root {#CertThumbprint}"; Flags: runhidden; RunOnceId: "DelRoot"
Filename: "{sys}\certutil.exe"; Parameters: "-delstore TrustedPublisher {#CertThumbprint}"; Flags: runhidden; RunOnceId: "DelTP"
#endif

[UninstallDelete]
; Логи удаляются всегда (план, 3.8, код-пример); конфиг — по ответу
; пользователя, см. usPostUninstall ниже.
Type: filesandordirs; Name: "{commonappdata}\vless-tunnel\logs"

[Code]
var
  DeleteSettings: Boolean;

function ServiceExists(const ServiceName: String): Boolean;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\sc.exe'), 'query ' + ServiceName, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := ResultCode = 0;
end;

{ Ревью п.6: sc.exe stop возвращается сразу после ОТПРАВКИ запроса на
  останов — сама служба останавливается асинхронно. ewWaitUntilTerminated
  ждёт завершения ПРОЦЕССА sc.exe (доли секунды), а не факта остановки
  СЛУЖБЫ — далее по коду идёт замена занятых файлов, отсюда гонка
  (вероятный вклад в "тихая установка молча ничего не поменяла", уже
  вскрытую RestartManager'ом отдельно). Опрашиваем реальное состояние
  через сам Win32 API (advapi32.dll), не через текст sc query — его
  вывод локализован по меткам полей (тот же урок, что и в
  GetExistingAllowUser выше: на русской Windows "STATE" не найти). }
const
  SC_MANAGER_CONNECT = $0001;
  SERVICE_QUERY_STATUS = $0004;
  SERVICE_STOPPED = 1;

type
  SERVICE_STATUS = record
    dwServiceType: LongWord;
    dwCurrentState: LongWord;
    dwControlsAccepted: LongWord;
    dwWin32ExitCode: LongWord;
    dwServiceSpecificExitCode: LongWord;
    dwCheckPoint: LongWord;
    dwWaitHint: LongWord;
  end;

function OpenSCManagerW(lpMachineName, lpDatabaseName: String; dwDesiredAccess: LongWord): LongWord;
  external 'OpenSCManagerW@advapi32.dll stdcall';
function OpenServiceW(hSCManager: LongWord; lpServiceName: String; dwDesiredAccess: LongWord): LongWord;
  external 'OpenServiceW@advapi32.dll stdcall';
function QueryServiceStatus(hService: LongWord; var lpServiceStatus: SERVICE_STATUS): Boolean;
  external 'QueryServiceStatus@advapi32.dll stdcall';
function CloseServiceHandle(hSCObject: LongWord): Boolean;
  external 'CloseServiceHandle@advapi32.dll stdcall';

// Возвращает dwCurrentState (1=STOPPED..) или 0, если службы/менеджера
// нет вовсе или запрос не прошёл — 0 трактуем как "остановлена" ниже
// (нет службы — нечему мешать замене файлов).
function GetServiceState(const ServiceName: String): LongWord;
var
  hSCM, hSvc: LongWord;
  Status: SERVICE_STATUS;
begin
  Result := 0;
  hSCM := OpenSCManagerW('', '', SC_MANAGER_CONNECT);
  if hSCM = 0 then Exit;
  try
    hSvc := OpenServiceW(hSCM, ServiceName, SERVICE_QUERY_STATUS);
    if hSvc = 0 then Exit;
    try
      if QueryServiceStatus(hSvc, Status) then
        Result := Status.dwCurrentState;
    finally
      CloseServiceHandle(hSvc);
    end;
  finally
    CloseServiceHandle(hSCM);
  end;
end;

function WaitForServiceStopped(const ServiceName: String; TimeoutMs: Integer): Boolean;
var
  Elapsed: Integer;
  State: LongWord;
begin
  Elapsed := 0;
  repeat
    State := GetServiceState(ServiceName);
    if (State = SERVICE_STOPPED) or (State = 0) then
    begin
      Result := True;
      Exit;
    end;
    Sleep(250);
    Elapsed := Elapsed + 250;
  until Elapsed >= TimeoutMs;
  Result := False;
end;

{ Ревью п.1: константа username (без фигурных скобок в этом комментарии
  нарочно — препроцессор ISPP пытается раскрыть их как ссылку на
  константу даже внутри Pascal-комментария, находка ещё с этапа
  установки install.cmd) — это
  ПОЛЬЗОВАТЕЛЬ, ОТ ИМЕНИ КОТОРОГО РЕАЛЬНО ЗАПУЩЕН Setup.exe, а не
  обязательно интерактивно вошедший в Windows. self-update (SelfUpdater.cs)
  запускает установщик из процесса САМОЙ службы (LocalSystem) — тихо, без
  показа мастера, поэтому username там резолвится в SYSTEM (подтверждено
  живым тестом: sc qc после такого запуска показывал --allow-user
  "Система"/SYSTEM вместо реального пользователя). Итог — обычный
  пользователь после КАЖДОГО self-update терял доступ к именованному
  каналу, ровно тот же симптом, что был у тестировщика при отсутствующем
  --allow-user вовсе.
  Читаем ИМЯ ПОЛЬЗОВАТЕЛЯ ИЗ УЖЕ ЗАРЕГИСТРИРОВАННОГО ImagePath службы
  (реестр, не sc.exe — вывод sc.exe локализован по меткам полей,
  "BINARY_PATH_NAME" на русской Windows превращается в нечитаемый
  русский текст, парсить по метке ненадёжно) — если служба уже
  существует, её текущий --allow-user переживает апгрейд НЕЗАВИСИМО от
  того, кто/что запустило установщик в этот раз. Только для ПЕРВОЙ
  установки (службы ещё нет) используется username — единственный
  момент, где он гарантированно совпадает с живым, интерактивным
  пользователем, который и подтверждал UAC. }
function ExtractAllowUser(const BinPath: String): String;
var
  Marker: String;
  MarkerPos, QuoteStart, QuoteEnd: Integer;
begin
  Result := '';
  Marker := '--allow-user "';
  MarkerPos := Pos(Marker, BinPath);
  if MarkerPos = 0 then Exit;
  QuoteStart := MarkerPos + Length(Marker);
  QuoteEnd := Pos('"', Copy(BinPath, QuoteStart, MaxInt));
  if QuoteEnd = 0 then Exit;
  Result := Copy(BinPath, QuoteStart, QuoteEnd - 1);
end;

function GetExistingAllowUser(const ServiceName: String): String;
var
  ImagePath: String;
begin
  Result := '';
  if RegQueryStringValue(HKLM, 'SYSTEM\CurrentControlSet\Services\' + ServiceName, 'ImagePath', ImagePath) then
    Result := ExtractAllowUser(ImagePath);
end;

procedure StopExistingService;
var
  ResultCode: Integer;
begin
  { Обновление поверх (план, 3.7/3.8): остановить службу -> заменить
    файлы -> запустить. Файлы ещё заняты, пока служба жива, поэтому это
    должно случиться ДО копирования в ssInstall, не после. }
  if ServiceExists('{#MyServiceName}') then
  begin
    Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#MyServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    // Ревью п.6 — см. комментарий у WaitForServiceStopped выше: без этого
    // копирование файлов чуть ниже (ssInstall) могло начаться раньше, чем
    // процесс службы реально освободил свой exe.
    if not WaitForServiceStopped('{#MyServiceName}', 30000) then
      RaiseException('Служба vless-tunnel не остановилась за 30 секунд — прерываю установку, файлы могли остаться занятыми.');
  end;
end;

procedure InstallOrUpdateService;
var
  ResultCode: Integer;
  BinPath, StartType, AllowUser, ExistingAllowUser: String;
begin
  { sc.exe ожидает значение binPath= одним аргументом командной строки:
    внешние кавычки — потому что весь аргумент содержит пробелы, ВНУТРИ
    него — экранированные кавычки \"...\" вокруг самого пути к exe,
    потому что путь тоже с пробелами, а после него идёт "service" уже
    без кавычек (тот же формат, что в документации Microsoft для
    "sc create ... binPath= \"\"C:\...\App.exe\" -arg\""). Раньше здесь
    были ДВОЙНЫЕ внешние кавычки (BinPath уже содержал '"'..'"' сам по
    себе, и снаружи добавлялась ещё одна пара) — sc.exe получал
    испорченную строку и молча (Exec не проверял ResultCode) ничего не
    создавал: живым тестом на стенде установщик "успешно" завершался,
    а службы не было вовсе. }
  { --allow-user с именем текущего пользователя — БЕЗ этого ACL
    именованного канала службы разрешает только SYSTEM и группу
    "Администраторы" ПРИ ПОЛНОМ ПОВЫШЕНИИ ПРАВ: обычный запуск трея
    (без "Запуск от имени администратора") у Windows идёт с урезанным
    токеном даже для админской учётки (UAC split-token) — доступа к
    каналу нет ни статус получить, ни ссылку вставить, ни включить
    туннель (план, этап 4: "пользователь без прав администратора
    управляет туннелем" — само IPC это умело с самого начала, но
    установщик никогда не передавал службе, КОГО именно пускать).
    Найдено на реальной машине тестировщика — весь этот сеанс на стенде
    трей запускался только через Планировщик заданий с повышением, что
    маскировало баг целиком. Константа username — тот, кто ставит
    программу (тот же, кто подтверждал UAC), что верно для обычного
    случая "себе на свой ПК" — НО только пока установщик запущен САМИМ
    этим человеком. self-update запускает установщик из процесса службы
    (LocalSystem) — тогда username резолвится в SYSTEM, а не в реального
    пользователя (см. ExtractAllowUser/GetExistingAllowUser выше и запись
    в PLAN-windows.md, "Ревью п.1" — подтверждено живым тестом). Поэтому
    при АПГРЕЙДЕ поверх уже существующей службы её текущий --allow-user
    переносится как есть, а username идёт в ход только при первой
    установке, когда службы ещё нет. }
  ExistingAllowUser := '';
  if ServiceExists('{#MyServiceName}') then
    ExistingAllowUser := GetExistingAllowUser('{#MyServiceName}');
  if ExistingAllowUser <> '' then
    AllowUser := ExistingAllowUser
  else
    AllowUser := ExpandConstant('{username}');
  BinPath := '\"' + ExpandConstant('{app}\{#MyServiceExeName}') + '\" service --allow-user \"' + AllowUser + '\"';
  { Раньше зависело от чекбокса "autostartservice" (checkedonce, то есть
    включён по умолчанию в мастере) — но /VERYSILENT/-SILENT (в том числе
    self-update, он всегда тихий) не выбирают вообще никаких задач, даже
    checkedonce, если не передать /TASKS= явно. Живым тестом (свежая
    /VERYSILENT-установка v0.1.0) подтверждено: служба реально вставала
    DEMAND_START, несмотря на "включённый по умолчанию" чекбокс в .iss —
    после ЛЮБОЙ перезагрузки или self-update пользователь получал
    "не удаётся подключиться к службе" без единой подсказки почему
    (нашлось по скриншоту с реальной машины параллельного тестировщика).
    Сама служба до set-link+"Включить" ничего не делает и ничего не
    подключает — держать её DEMAND_START ради опции, которую тихая
    установка всё равно игнорирует, не стоит риска сломанного приложения
    после каждого self-update. }
  StartType := 'auto';

  if ServiceExists('{#MyServiceName}') then
  begin
    { Раньше код возврата не проверялся вовсе — при сбое (например, та же
      "испорченная строка binPath" из комментария выше, которая когда-то
      ломала sc create) апгрейд молча оставлял службу со СТАРЫМ binPath,
      ничем не сигналя об этом (в отличие от ветки sc create ниже, которая
      уже кидает исключение). }
    Exec(ExpandConstant('{sys}\sc.exe'), 'config {#MyServiceName} binPath= "' + BinPath + '" start= ' + StartType, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    if ResultCode <> 0 then
      RaiseException('Не удалось обновить службу vless-tunnel (sc config, код ' + IntToStr(ResultCode) + ').');
  end
  else
  begin
    Exec(ExpandConstant('{sys}\sc.exe'), 'create {#MyServiceName} binPath= "' + BinPath + '" start= ' + StartType + ' DisplayName= "vless-tunnel"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    if ResultCode <> 0 then
      RaiseException('Не удалось зарегистрировать службу vless-tunnel (sc create, код ' + IntToStr(ResultCode) + ').');
    Exec(ExpandConstant('{sys}\sc.exe'), 'description {#MyServiceName} "VLESS-туннель (WFP kill-switch, TUN)"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;

  { Действия при сбое (план, 3.9: "перезапуск через 5 с, 30 с, 60 с") —
    без этого падение процесса (например, необработанное исключение)
    оставляет туннель выключенным до ручного вмешательства, а с
    включённым kill-switch — без интернета вовсе. reset=86400 обнуляет
    счётчик попыток через сутки без новых падений, чтобы редкие сбои не
    копились в "3-я и далее попытка" навсегда. Ставится каждый раз
    (create и config) — sc config его не трогает, а идемпотентный вызов
    sc failure ничего не портит при повторной установке. }
  Exec(ExpandConstant('{sys}\sc.exe'), 'failure {#MyServiceName} reset= 86400 actions= restart/5000/restart/30000/restart/60000', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  Exec(ExpandConstant('{sys}\net.exe'), 'start {#MyServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  case CurStep of
    ssInstall: StopExistingService;
    ssPostInstall: InstallOrUpdateService;
  end;
end;

// Ревью п.6: doctor теперь строго ПОСЛЕ гарантированной остановки службы
// (WaitForServiceStopped), не декларативным списком [UninstallRun], где
// порядок "doctor -> sc stop" снимал WFP-фильтры у ещё живой службы.
//
// Ревью п.23: раньше туннель сворачивался ПОБОЧНЫМ эффектом sc stop
// (OnStop -> OffAsync внутри службы) — работает, только пока служба
// успевает уложиться в таймаут SCM (по умолчанию у Windows он свой,
// не наш WaitForServiceStopped ниже: SCM может решить, что служба
// зависла, и убить процесс, так и не дав OnStop доработать до конца).
// Теперь туннель гасится ЯВНО, штатным путём (VlessTunnel.Cli.exe off,
// с его собственным IPC-таймаутом), пока служба заведомо жива и отвечает
// по каналу — до какого-либо sc stop. Ошибка здесь не фатальна (служба
// уже могла быть мертва) — только в лог.
procedure StopAndCleanupServiceForUninstall;
var
  ResultCode: Integer;
  StoppedInTime: Boolean;
begin
  if ServiceExists('{#MyServiceName}') then
  begin
    Exec(ExpandConstant('{app}\{#MyCliExeName}'), 'off', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Log('StopAndCleanupServiceForUninstall: "VlessTunnel.Cli.exe off" -> код ' + IntToStr(ResultCode));

    // Тем же способом, что использует self-update (SelfUpdater.
    // CloseRunningTray, теперь public специально ради этого) — без этого
    // занятый файл трея не даёт снести папку установки целиком, а иконка
    // остаётся висеть с уже мёртвой службой. Обёрнуто в отдельную команду
    // службы ("close-tray"), а не переписано заново на Pascal, чтобы не
    // завести вторую, отдельно расходящуюся реализацию того же закрытия.
    // (Комментарий нарочно избегает фигурных скобок вокруг имён констант
    // вроде app/sys — ISPP раскрывает такие ссылки даже внутри
    // { }-комментариев, см. ревью п.1 выше по файлу; на всякий случай тот
    // же принцип соблюдён и здесь, хотя это уже однострочный //.)
    Exec(ExpandConstant('{app}\{#MyServiceExeName}'), 'close-tray', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

    Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#MyServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    StoppedInTime := WaitForServiceStopped('{#MyServiceName}', 30000);
    if not StoppedInTime then
    begin
      Log('StopAndCleanupServiceForUninstall: служба vless-tunnel не остановилась за 30с после sc stop');
      if not UninstallSilent() then
        MsgBox(
          'Служба vless-tunnel не остановилась за отведённое время.' + #13#10 + #13#10 +
          'Удаление продолжится (маршруты и WFP-фильтры будут сняты принудительно), ' +
          'но при следующей возможности проверьте, что процессы VlessTunnel.Service.exe ' +
          'и xray.exe действительно завершились.',
          mbError, MB_OK);
    end;
  end;
  // doctor снимает не только WFP-фильтры, но и НАШИ маршруты по метке
  // OwnRouteProtocol (ревью п.23; план, 3.7: удаление снимает "службу,
  // маршруты и WFP-фильтры") — перебором живой таблицы маршрутов, не по
  // памяти процесса, поэтому снимет их, даже если sc stop выше не
  // уложился в таймаут (зависшая служба всё ещё может держать
  // хост-маршрут).
  Exec(ExpandConstant('{app}\{#MyServiceExeName}'), 'doctor', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\sc.exe'), 'delete {#MyServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  case CurUninstallStep of
    usUninstall:
      begin
        { Тихое удаление (/SILENT, /VERYSILENT) настройки сохраняет —
          план, 3.8, требование дословно. }
        DeleteSettings := False;
        if not UninstallSilent() then
          DeleteSettings := MsgBox(
            'Удалить настройки vless-tunnel (ссылку на сервер и параметры)?' + #13#10 + #13#10 +
            'Нет — настройки останутся и подхватятся при следующей установке.',
            mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
        StopAndCleanupServiceForUninstall;
      end;
    usPostUninstall:
      if DeleteSettings then
        DelTree(ExpandConstant('{commonappdata}\vless-tunnel'), True, True, True);
  end;
end;
