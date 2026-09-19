#!/usr/bin/env bash
# Обёртка ЛЮБОГО опасного теста (план, 5.5): всё, что меняет маршруты, DNS,
# брандмауэр/WFP или запускает туннель внутри виртуалки, идёт только через
# этот скрипт. Цепочка спасения (шаги пронумерованы как в плане):
#   1. снимок pre-<тест> (старые pre-* удаляются, оставляем последние 3)
#   2. задача vt-rescue на +10 минут (страховка, если сам скрипт зависнет)
#   3. run-test.ps1 через SSH под timeout, лог копируется в logs/vm/
#   3'. timeout сработал (сценарий завис) -> сразу считаем стенд сломанным,
#       минуя проверку связи (стенд, п.4: "откат после падения ИЛИ по
#       таймауту" — зависший процесс на госте не гарантирует, что его
#       finally/rescue.ps1 вообще выполнился, даже если сеть на вид цела)
#   4. проверка: SSH отвечает и есть интернет без туннеля
#   5. нет связи (или был таймаут) -> rescue.ps1 через guest agent (без сети), ждать до 60с
#   6. guest agent не помог -> откат на pre-снимок, тест = BROKE_VM
#   7. успех -> снять задачу vt-rescue
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
check_net() {
  "$VMCTL" ssh "curl.exe -s --max-time 10 -o NUL -w '%{http_code}' http://example.com" 2>/dev/null | grep -q '^2'
}

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

# --- 2. задача vt-rescue на +10 минут ---
log "Ставлю задачу vt-rescue (страховка на случай, если этот скрипт сам зависнет)"
"$VMCTL" exec 'schtasks.exe' /Create /TN vt-rescue /SC ONCE \
  /ST "$(date -d '+10 minutes' +%H:%M)" /SD "$(date -d '+10 minutes' +%d/%m/%Y)" \
  /TR 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\dev\bin\rescue.ps1' \
  /RL HIGHEST /F >/dev/null 2>&1

# --- 3. копируем тест, запускаем run-test.ps1 через SSH под timeout ---
REMOTE_SCRIPT="C:\\dev\\bin\\test-${TEST_NAME}.ps1"
HOSTLOG="$LOGDIR/${TEST_NAME}-$(date +%Y%m%d-%H%M%S).host.log"
{ echo "test: $TEST_NAME"; echo "local_script: $LOCAL_SCRIPT"; echo "snapshot: $SNAP"; } > "$HOSTLOG"

SSH_OK=0
TIMED_OUT=0
if "$VMCTL" scp "$LOCAL_SCRIPT" "vt-win10:$REMOTE_SCRIPT" >>"$HOSTLOG" 2>&1; then
  log "Запускаю run-test.ps1 через SSH (timeout 20 минут)"
  timeout 1200 "$VMCTL" ssh "powershell -NoProfile -ExecutionPolicy Bypass -File C:\\dev\\bin\\run-test.ps1 -Name $TEST_NAME -Script $REMOTE_SCRIPT" >>"$HOSTLOG" 2>&1
  RUN_EXIT=$?
  echo "run-test.ps1 exit: $RUN_EXIT" >> "$HOSTLOG"
  if [ "$RUN_EXIT" = "124" ]; then
    TIMED_OUT=1
    log "Сценарий не уложился в 20 минут (timeout убил SSH-команду) — стенд считаем сломанным независимо от связи"
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

# --- 4. проверка связи ---
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
  # --- 5. нет связи -> rescue.ps1 через guest agent (без сети) ---
  log "Связи нет — запускаю rescue.ps1 через guest agent"
  "$VMCTL" rescue >>"$HOSTLOG" 2>&1

  RECOVERED=0
  for _ in $(seq 1 12); do
    sleep 5
    if check_net; then RECOVERED=1; break; fi
  done

  if [ "$RECOVERED" = "1" ]; then
    log "guest agent восстановил связь."
  else
    # --- 6. откат к pre-снимку ---
    log "guest agent не помог — откатываюсь на $SNAP"
    sudo virsh snapshot-revert "$VM_NAME" "$SNAP" --running 2>>"$HOSTLOG" \
      || sudo virsh snapshot-revert "$VM_NAME" "$SNAP" 2>>"$HOSTLOG"
    echo "RESULT: BROKE_VM" >> "$HOSTLOG"
    log "RESULT: BROKE_VM — см. $HOSTLOG"
    exit 1
  fi
fi

# --- 7. успех -> снять задачу vt-rescue ---
"$VMCTL" exec 'schtasks.exe' /Delete /TN vt-rescue /F >/dev/null 2>&1
echo "RESULT: DONE, test_verdict=$TEST_VERDICT" >> "$HOSTLOG"
log "Готово (машина цела). Вердикт теста: $TEST_VERDICT. Лог: $HOSTLOG"
[ "$TEST_VERDICT" = "PASS" ]
