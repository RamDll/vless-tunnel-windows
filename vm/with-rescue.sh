#!/usr/bin/env bash
# Обёртка ЛЮБОГО опасного теста (план, 5.5): всё, что меняет маршруты, DNS,
# брандмауэр/WFP или запускает туннель внутри виртуалки, идёт только через
# этот скрипт. Цепочка спасения (шаги пронумерованы как в плане):
#   1. снимок pre-<тест> (старые pre-* удаляются, оставляем последние 3)
#   2. задача vt-rescue на TEST_TIMEOUT_S+300с (страховка, если сам скрипт
#      зависнет) — намеренно БОЛЬШЕ внешнего timeout ниже (ревью стенда п.24:
#      раньше было фиксированных +10 минут при внешнем таймауте 20 минут —
#      у тяжёлых сценариев (полная пересборка + install/uninstall) реальное
#      время прогона легко залезает за 10 минут, и страховка тогда стреляла
#      ПОСЕРЕДИНЕ ещё идущего теста, сама останавливая службу и дёргая DHCP)
#   2а. фоновый сборщик диагностики связи на хосте (ревью стенда п.25) —
#      каждые 5с: ip neigh + ping с хоста, состояние адаптера vt-mgmt со
#      стороны гостя через guest agent (независимый от SSH канал). Пишет на
#      ХОСТ (не в гостя) — переживает откат VM на снимок.
#   3. синхронизация исходников в C:\dev\src (стенд, п.3 — сборка внутри гостя)
#   4. run-test.ps1 через SSH под timeout, лог копируется в logs/vm/
#   4'. timeout сработал (сценарий завис) -> сразу считаем стенд сломанным,
#       минуя проверку связи (стенд, п.4: "откат после падения ИЛИ по
#       таймауту" — зависший процесс на госте не гарантирует, что его
#       finally/rescue.ps1 вообще выполнился, даже если сеть на вид цела)
#   5. проверка: SSH отвечает и есть интернет без туннеля (с бэкоффом и
#      сбросом ARP-записи хоста, ревью стенда п.26)
#   6. нет связи -> СНАЧАЛА спросить guest agent, жив ли гость вообще
#      (ревью стенда п.27: SSH — транспорт, не индикатор здоровья; гость
#      может быть полностью жив, а сломан именно канал/сеть)
#   6а. guest agent молчит -> гость действительно не отвечает -> откат на
#       pre-снимок, тест = BROKE_VM
#   6б. guest agent отвечает -> гость жив -> rescue.ps1 через guest agent,
#       расширенная проверка связи; если и это не помогло — НЕ откатываем
#       (ломать снимком живую машину ради недоступного SSH бессмысленно),
#       результат NET_UNREACHABLE_BUT_ALIVE, машина остаётся для разбора
#   7. успех -> снять задачу vt-rescue (теперь всегда, на любом выходе —
#      ревью стенда п.24, через trap, не только на успешном пути)
set -uo pipefail  # без -e: после сбоя должны выполниться шаги восстановления, не упасть

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
VMCTL="$SCRIPT_DIR/vmctl.sh"
VM_NAME="vt-win10"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
LOGDIR="$REPO_ROOT/logs/vm"
mkdir -p "$LOGDIR"

[ $# -eq 2 ] || { echo "Использование: $0 <имя-теста> <локальный .ps1>" >&2; exit 2; }
TEST_NAME="$1"
LOCAL_SCRIPT="$2"
[ -f "$LOCAL_SCRIPT" ] || { echo "Нет файла: $LOCAL_SCRIPT" >&2; exit 2; }

log() { echo "[with-rescue:$TEST_NAME] $*" >&2; }

# Объявлен здесь (не в шаге 3, где заполняется содержимым) — под set -u
# ссылка на него из cleanup() ниже не должна падать "unbound variable",
# если скрипт выйдет РАНЬШЕ шага 3 (например, не снялся снимок).
HOSTLOG="$LOGDIR/${TEST_NAME}-$(date +%Y%m%d-%H%M%S).host.log"
: > "$HOSTLOG"

MGMT_IP="$("$VMCTL" ip)"
MGMT_BRIDGE="virbr-vtmgmt"  # см. vm/vt-mgmt-network.xml
TEST_TIMEOUT_S=1200
SAFETY_TASK_DELAY_S=$((TEST_TIMEOUT_S + 300))

# Ревью стенда п.26: "No route to host" (EHOSTUNREACH) — это ХОСТ не
# получил ARP-ответ, не "sshd не отвечает" (было бы Connection refused) и
# не "фильтр дропает" (был бы таймаут). Одна из причин — гость реально на
# секунды подвисает под нагрузкой, хост успевает пометить ARP-запись
# FAILED и потом отдаёт EHOSTUNREACH мгновенно ещё некоторое время ПОСЛЕ
# того, как гость ожил. Сбрасываем запись перед каждой попыткой, чтобы не
# мерить этот призрак, и растягиваем окно ожидания с бэкоффом вместо
# фиксированных 3х3с.
check_net() {
  local delays=(2 3 5 8 13 21 21) i out rc
  for i in "${!delays[@]}"; do
    ip neigh flush to "$MGMT_IP" dev "$MGMT_BRIDGE" >/dev/null 2>&1 || true
    out="$("$VMCTL" ssh "curl.exe -s --max-time 10 -o NUL -w '%{http_code}' http://example.com" 2>&1)"
    rc=$?
    if [ $rc -eq 0 ] && echo "$out" | grep -q '^2'; then
      return 0
    fi
    log "check_net: попытка $((i + 1))/${#delays[@]} не удалась (ssh_rc=$rc, out=[$out]), пауза ${delays[$i]}с"
    sleep "${delays[$i]}"
  done
  return 1
}

# --- фоновый сборщик диагностики связи (ревью стенда п.25) ---
NETWATCH_LOG="$LOGDIR/${TEST_NAME}-$(date +%Y%m%d-%H%M%S).netwatch.log"
: > "$NETWATCH_LOG"
netwatch_loop() {
  while true; do
    local ts neigh pingres guest
    ts="$(date '+%Y-%m-%dT%H:%M:%S%z')"
    neigh="$(ip neigh show "$MGMT_IP" dev "$MGMT_BRIDGE" 2>/dev/null | tr -d '\n')"
    [ -z "$neigh" ] && neigh="(нет записи)"
    if ping -c1 -W1 "$MGMT_IP" >/dev/null 2>&1; then pingres=ok; else pingres=fail; fi
    guest="$("$VMCTL" exec 'powershell.exe' -NoProfile -Command '$a = Get-NetIPAddress -InterfaceAlias vt-mgmt -AddressFamily IPv4 -ErrorAction SilentlyContinue; $n = Get-NetAdapter -Name vt-mgmt -ErrorAction SilentlyContinue; Write-Output "ip=$($a.IPAddress) status=$($n.Status)"' 2>&1 | tr -d '\r\n')"
    echo "$ts host_neigh=[$neigh] host_ping=$pingres guest_via_agent=[$guest]" >>"$NETWATCH_LOG"
    sleep 5
  done
}
netwatch_loop &
NETWATCH_PID=$!

# Ревью стенда п.24: снятие задачи vt-rescue раньше было отдельным шагом
# ТОЛЬКО на успешном пути в самом конце — на любом другом выходе (ранний
# exit, BROKE_VM, необработанная ошибка) задача оставалась висеть и могла
# сама сработать при следующем прогоне. Через trap — гарантированно на
# ЛЮБОМ выходе. Заодно логируем, была ли задача ещё жива к этому моменту:
# если её уже нет, а мы её явно не снимали — она либо сработала сама
# (rescue.ps1 снимает себя своим же шагом), либо не была создана вовсе.
cleanup() {
  kill "$NETWATCH_PID" >/dev/null 2>&1 || true
  wait "$NETWATCH_PID" 2>/dev/null || true

  local q
  q="$("$VMCTL" exec 'schtasks.exe' /Query /TN vt-rescue 2>&1)"
  if echo "$q" | grep -qi 'ERROR'; then
    log "vt-rescue: задачи уже нет к завершению скрипта (сама снялась — сработала, или не создавалась) — см. $HOSTLOG"
    echo "vt-rescue: not present at cleanup" >>"$HOSTLOG" 2>/dev/null || true
  else
    log "vt-rescue: задача ещё существовала, снимаю явно"
    echo "vt-rescue: was present at cleanup, removed" >>"$HOSTLOG" 2>/dev/null || true
  fi
  "$VMCTL" exec 'schtasks.exe' /Delete /TN vt-rescue /F >/dev/null 2>&1 || true
}
trap cleanup EXIT

# --- 1. снимок pre-<тест>, старые pre-* храним не больше 3 ---
SNAP="pre-${TEST_NAME}-$(date +%Y%m%d-%H%M%S)"
log "Снимок $SNAP"
"$VMCTL" snapshot "$SNAP" "before $TEST_NAME" || { log "Не удалось снять снимок — тест не запускаю"; exit 1; }

# Стенд, п.4 (найдено этим же живым тестом): сортировка по ИМЕНИ, не по
# времени, — снимки разных тестов вперемешку (pre-acl-..., pre-break-...,
# pre-hang-...) алфавитно сортируются не в хронологическом порядке, и
# "оставить последние 3" по имени однажды снесло только что созданный
# снимок ДО того, как он вообще понадобился (revert тут же упал "снимок
# не найден", а скрипт этого не заметил и всё равно доложил об успехе).
# Сортируем по колонке "Время создания" самого virsh, не по имени.
OLD_PRE="$("$VMCTL" snapshot-list 2>/dev/null | awk '$1 ~ /^pre-/{print $2$3, $1}' | sort | head -n -3 | awk '{print $2}')"
if [ -n "$OLD_PRE" ]; then
  log "Удаляю старые pre-* снимки (оставляю последние 3)"
  while IFS= read -r old; do
    [ -n "$old" ] && sudo virsh snapshot-delete "$VM_NAME" "$old" >/dev/null 2>&1
  done <<< "$OLD_PRE"
fi

# --- 2. задача vt-rescue со сроком БОЛЬШЕ внешнего таймаута теста ---
log "Ставлю задачу vt-rescue на +${SAFETY_TASK_DELAY_S}с (страховка на случай, если этот скрипт сам зависнет)"
if "$VMCTL" exec 'schtasks.exe' /Create /TN vt-rescue /SC ONCE \
  /ST "$(date -d "+${SAFETY_TASK_DELAY_S} seconds" +%H:%M)" /SD "$(date -d "+${SAFETY_TASK_DELAY_S} seconds" +%d/%m/%Y)" \
  /TR 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\dev\bin\rescue.ps1' \
  /RL HIGHEST /F >/dev/null 2>&1; then
  log "vt-rescue создана"
else
  log "vt-rescue НЕ удалось создать (см. лог хоста) — страховки на этот прогон не будет"
fi

# --- 3. синхронизируем исходники в C:\dev\src (стенд, п.3: сборка живёт
# внутри гостя — guest-build.ps1 берёт код отсюда, хосту незачем гонять
# готовые бинарники через scp). git archive -> tar по SSH, без .git и
# рабочих артефактов (bin/obj), быстро (доли секунды на этот репозиторий).
{ echo "test: $TEST_NAME"; echo "local_script: $LOCAL_SCRIPT"; echo "snapshot: $SNAP"; echo "netwatch_log: $NETWATCH_LOG"; } >> "$HOSTLOG"

log "Синхронизирую исходники в C:\\dev\\src"
"$VMCTL" ssh "Remove-Item 'C:\\dev\\src' -Recurse -Force -ErrorAction SilentlyContinue; New-Item -ItemType Directory -Force -Path 'C:\\dev\\src' | Out-Null" >>"$HOSTLOG" 2>&1
if ! git -C "$REPO_ROOT" archive --format=tar HEAD | "$VMCTL" ssh "tar -xf - -C C:\\dev\\src" >>"$HOSTLOG" 2>&1; then
  log "Синхронизация исходников не удалась — тест не запускаю"
  echo "RESULT: SYNC_FAILED" >> "$HOSTLOG"
  exit 1
fi

# --- 4. копируем тест, запускаем run-test.ps1 через SSH под timeout ---
REMOTE_SCRIPT="C:\\dev\\bin\\test-${TEST_NAME}.ps1"

SSH_OK=0
TIMED_OUT=0
if "$VMCTL" scp "$LOCAL_SCRIPT" "vt-win10:$REMOTE_SCRIPT" >>"$HOSTLOG" 2>&1; then
  log "Запускаю run-test.ps1 через SSH (timeout ${TEST_TIMEOUT_S}с)"
  timeout "$TEST_TIMEOUT_S" "$VMCTL" ssh "powershell -NoProfile -ExecutionPolicy Bypass -File C:\\dev\\bin\\run-test.ps1 -Name $TEST_NAME -Script $REMOTE_SCRIPT" >>"$HOSTLOG" 2>&1
  RUN_EXIT=$?
  echo "run-test.ps1 exit: $RUN_EXIT" >> "$HOSTLOG"
  if [ "$RUN_EXIT" = "124" ]; then
    TIMED_OUT=1
    log "Сценарий не уложился в ${TEST_TIMEOUT_S}с (timeout убил SSH-команду) — стенд считаем сломанным независимо от связи"
  else
    SSH_OK=1
  fi
else
  log "SCP не прошёл — сразу переходим к проверке связи"
fi

# забираем гостевой лог и JSON-отчёт run-test.ps1 на хост (best effort).
# Стенд, п.2: вердикт считаем ТОЛЬКО по JSON (числа/структура, из
# run-test.ps1 -> Add-Step), не по тексту транскрипта — тот проехал через
# гостевую консоль и SSH несколько перекодировок и ловил ложные срабатывания
# на локализованных строках системных утилит.
#
# scp (современный OpenSSH — только по SFTP) на обратных слешах в
# Windows-пути молча не находит файл ("No such file or directory"), хотя
# Get-ChildItem его только что вернул, — путь нужен с прямыми слешами
# (SFTP-протокол, не cmd.exe; Windows одинаково понимает оба варианта).
# Найдено живым тестом при проверке самого этого фикса (п.2): раньше
# REMOTE_LOG_PATH тем же путём никогда фактически не долетал.
REMOTE_LOG_PATH="$("$VMCTL" ssh "Get-ChildItem C:\\dev\\logs\\${TEST_NAME}-*.log -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1 -ExpandProperty FullName" 2>/dev/null | tr -d '\r')"
REMOTE_LOG_PATH="${REMOTE_LOG_PATH//\\//}"
[ -n "$REMOTE_LOG_PATH" ] && "$VMCTL" scp "vt-win10:$REMOTE_LOG_PATH" "$LOGDIR/" >/dev/null 2>&1

REMOTE_JSON_PATH="$("$VMCTL" ssh "Get-ChildItem C:\\dev\\logs\\${TEST_NAME}-*.json -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1 -ExpandProperty FullName" 2>/dev/null | tr -d '\r')"
REMOTE_JSON_PATH="${REMOTE_JSON_PATH//\\//}"
TEST_VERDICT="NO_REPORT"
if [ -n "$REMOTE_JSON_PATH" ] && "$VMCTL" scp "vt-win10:$REMOTE_JSON_PATH" "$LOGDIR/" >/dev/null 2>&1; then
  LOCAL_JSON="$LOGDIR/$(basename "$REMOTE_JSON_PATH")"
  TEST_VERDICT="$(jq -r '.verdict // "NO_REPORT"' "$LOCAL_JSON" 2>/dev/null || echo "NO_REPORT")"
  echo "test_report: $LOCAL_JSON" >> "$HOSTLOG"
  echo "test_verdict: $TEST_VERDICT" >> "$HOSTLOG"
  jq -r '.steps[]? | select(.verdict != "PASS") | "  FAIL: " + .step + " (ожидали " + (.expected|tostring) + ", получили " + (.actual|tostring) + ")"' "$LOCAL_JSON" 2>/dev/null >> "$HOSTLOG"
fi
log "Вердикт теста (из JSON-отчёта): $TEST_VERDICT"

# --- 5/6. проверка связи ---
if [ "$TIMED_OUT" = "1" ]; then
  # Стенд, п.4: таймаут -> сразу откат, без попытки "подлатать" через
  # guest agent. Зависший на госте процесс не гарантирует, что его
  # finally (rescue.ps1) вообще выполнился, даже если сеть на вид цела —
  # доверять частичному восстановлению здесь неверно, откатываем сразу.
  log "Таймаут сценария — откатываюсь на $SNAP без попытки rescue.ps1"
  sudo virsh snapshot-revert "$VM_NAME" "$SNAP" --running 2>>"$HOSTLOG" \
    || sudo virsh snapshot-revert "$VM_NAME" "$SNAP" 2>>"$HOSTLOG"
  echo "RESULT: BROKE_VM (timeout)" >> "$HOSTLOG"
  log "RESULT: BROKE_VM (timeout) — см. $HOSTLOG"
  exit 1
elif [ "$SSH_OK" = "1" ] && check_net; then
  log "Связь в порядке."
else
  # Ревью стенда п.27: SSH — транспорт, не индикатор здоровья гостя. Он
  # может не отвечать и тогда, когда гость полностью жив (сетевая
  # проблема именно на управляющем канале/хосте) — прежде чем решать
  # "стенд сломан" и откатывать снимком, спрашиваем guest agent (канал,
  # полностью независимый от сети) — жив ли гость вообще.
  log "SSH/связь не отвечает — спрашиваю guest agent, жив ли гость (SSH не индикатор)"
  if "$VMCTL" guest-alive; then
    log "guest agent отвечает — гость жив. Пробую rescue.ps1 через guest agent и расширенную проверку связи."
    "$VMCTL" rescue >>"$HOSTLOG" 2>&1

    # check_net уже сама делает до ~73с бэкоффа за один вызов (ревью п.26)
    # — 12 внешних попыток по 5с паузы (старая цифра, рассчитанная под
    # СТАРУЮ быструю check_net из 3х3с=9с) умножали это на 12 и растягивали
    # реальное ожидание до ~15 минут. Три попытки с более длинной паузой
    # между ними — тот же порядок ожидания (~4 минуты), без перемножения.
    RECOVERED=0
    for i in 1 2 3; do
      if check_net; then RECOVERED=1; break; fi
      log "После rescue.ps1: попытка $i/3 связи не удалась, пауза 15с"
      sleep 15
    done

    if [ "$RECOVERED" = "1" ]; then
      log "Связь восстановлена."
    else
      # Гость подтверждённо жив (guest agent отвечает) — откат снимком
      # тут ничего не чинит, только теряет доказательства. Оставляем
      # машину как есть для разбора (см. $NETWATCH_LOG), не откатываем.
      log "Гость жив, но SSH/сеть не восстановились за отведённое время — это НЕ поломка VM, а сетевая проблема. Не откатываю."
      echo "RESULT: NET_UNREACHABLE_BUT_ALIVE (см. $HOSTLOG и $NETWATCH_LOG)" >> "$HOSTLOG"
      log "RESULT: NET_UNREACHABLE_BUT_ALIVE — см. $HOSTLOG и $NETWATCH_LOG"
      exit 1
    fi
  else
    log "guest agent тоже не отвечает — гость действительно не отвечает, откатываюсь на $SNAP"
    sudo virsh snapshot-revert "$VM_NAME" "$SNAP" --running 2>>"$HOSTLOG" \
      || sudo virsh snapshot-revert "$VM_NAME" "$SNAP" 2>>"$HOSTLOG"
    echo "RESULT: BROKE_VM (guest agent unresponsive)" >> "$HOSTLOG"
    log "RESULT: BROKE_VM (guest agent unresponsive) — см. $HOSTLOG"
    exit 1
  fi
fi

echo "RESULT: DONE, test_verdict=$TEST_VERDICT" >> "$HOSTLOG"
log "Готово (машина цела). Вердикт теста: $TEST_VERDICT. Лог: $HOSTLOG (диагностика связи: $NETWATCH_LOG)"
[ "$TEST_VERDICT" = "PASS" ]
