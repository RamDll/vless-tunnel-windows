#!/usr/bin/env bash
# Обёртка ЛЮБОГО опасного теста (план, 5.5): всё, что меняет маршруты, DNS,
# брандмауэр/WFP или запускает туннель внутри виртуалки, идёт только через
# этот скрипт. Цепочка спасения (шаги пронумерованы как в плане):
#   1. снимок pre-<тест> (старые pre-* удаляются, оставляем последние 3)
#   2. задача vt-rescue на +10 минут (страховка, если сам скрипт зависнет)
#   3. run-test.ps1 через SSH под timeout, лог копируется в logs/vm/
#   4. проверка: SSH отвечает и есть интернет без туннеля
#   5. нет связи -> rescue.ps1 через guest agent (работает без сети), ждать до 60с
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

OLD_PRE="$("$VMCTL" snapshot-list 2>/dev/null | awk '$1 ~ /^pre-/{print $1}' | sort | head -n -3)"
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
if "$VMCTL" scp "$LOCAL_SCRIPT" "vt-win10:$REMOTE_SCRIPT" >>"$HOSTLOG" 2>&1; then
  log "Запускаю run-test.ps1 через SSH (timeout 20 минут)"
  timeout 1200 "$VMCTL" ssh "powershell -NoProfile -ExecutionPolicy Bypass -File C:\\dev\\bin\\run-test.ps1 -Name $TEST_NAME -Script $REMOTE_SCRIPT" >>"$HOSTLOG" 2>&1
  echo "run-test.ps1 exit: $?" >> "$HOSTLOG"
  SSH_OK=1
else
  log "SCP не прошёл — сразу переходим к проверке связи"
fi

# забираем гостевой лог run-test.ps1 на хост (best effort)
REMOTE_LOG_PATH="$("$VMCTL" ssh "Get-ChildItem C:\\dev\\logs\\${TEST_NAME}-*.log -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1 -ExpandProperty FullName" 2>/dev/null | tr -d '\r')"
[ -n "$REMOTE_LOG_PATH" ] && "$VMCTL" scp "vt-win10:$REMOTE_LOG_PATH" "$LOGDIR/" >/dev/null 2>&1

# --- 4. проверка связи ---
if [ "$SSH_OK" = "1" ] && check_net; then
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
echo "RESULT: DONE (проверьте PASS/FAIL в госте-логе, скопированном в $LOGDIR)" >> "$HOSTLOG"
log "Готово. Лог: $HOSTLOG"
