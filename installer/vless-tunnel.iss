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

[Tasks]
Name: "autostartservice"; Description: "Запускать службу vless-tunnel автоматически при загрузке Windows"; Flags: checkedonce
Name: "autostarttray"; Description: "Запускать трей vless-tunnel при входе в систему"; Flags: checkedonce

[Files]
; Один путь к бинарникам, без копий (план, 3.7: урок Linux про дубль
; /usr/bin и /usr/local/bin) — все три exe и их общий рантайм лежат
; рядом в {app}, ничего не копируется ни в System32, ни ещё куда-то.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"

[Icons]
Name: "{group}\vless-tunnel"; Filename: "{app}\{#MyTrayExeName}"
Name: "{group}\Удалить vless-tunnel"; Filename: "{uninstallexe}"
Name: "{commonstartup}\vless-tunnel"; Filename: "{app}\{#MyTrayExeName}"; Tasks: autostarttray

[Run]
Filename: "{app}\{#MyTrayExeName}"; Description: "Запустить vless-tunnel сейчас"; Flags: postinstall nowait skipifsilent unchecked

[UninstallRun]
; Настройки удаляет сам деинсталлятор по ответу пользователя (см. [Code]
; ниже) — CLI их не трогает (план, 3.7: "uninstall из CLI не удаляет
; файлы... а только сбрасывает настройку"). Команда uninstall ещё не
; реализована на сервере (этап 4 её честно отклоняет) — строка ниже
; безопасный no-op до тех пор, не блокирует остальную деинсталляцию.
Filename: "{app}\{#MyCliExeName}"; Parameters: "uninstall --keep-config"; Flags: runhidden waituntilterminated; RunOnceId: "CliUninstall"
; Снять kill-switch-фильтры (план, 3.7: "удаление снимает службу,
; маршруты и WFP-фильтры") — doctor ищет по provider/sublayer, а не по
; памяти процесса, поэтому отработает и если служба уже остановлена.
Filename: "{app}\{#MyServiceExeName}"; Parameters: "doctor"; Flags: runhidden waituntilterminated; RunOnceId: "Doctor"
Filename: "{sys}\sc.exe"; Parameters: "stop {#MyServiceName}"; Flags: runhidden; RunOnceId: "StopSvc"
Filename: "{sys}\sc.exe"; Parameters: "delete {#MyServiceName}"; Flags: runhidden; RunOnceId: "DeleteSvc"
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

procedure StopExistingService;
var
  ResultCode: Integer;
begin
  { Обновление поверх (план, 3.7/3.8): остановить службу -> заменить
    файлы -> запустить. Файлы ещё заняты, пока служба жива, поэтому это
    должно случиться ДО копирования в ssInstall, не после. }
  if ServiceExists('{#MyServiceName}') then
    Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#MyServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure InstallOrUpdateService;
var
  ResultCode: Integer;
  BinPath, StartType: String;
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
    случая "себе на свой ПК". }
  BinPath := '\"' + ExpandConstant('{app}\{#MyServiceExeName}') + '\" service --allow-user \"' + ExpandConstant('{username}') + '\"';
  if WizardIsTaskSelected('autostartservice') then
    StartType := 'auto'
  else
    StartType := 'demand';

  if ServiceExists('{#MyServiceName}') then
  begin
    Exec(ExpandConstant('{sys}\sc.exe'), 'config {#MyServiceName} binPath= "' + BinPath + '" start= ' + StartType, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
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
      end;
    usPostUninstall:
      if DeleteSettings then
        DelTree(ExpandConstant('{commonappdata}\vless-tunnel'), True, True, True);
  end;
end;
