#!/usr/bin/env bash
# Единственная точка управления виртуалкой vt-win10 (план, раздел 5.3):
# ssh | scp | exec (через guest agent) | ip | snapshot | revert | rescue | status.
# Ни один другой скрипт не должен звать virsh/ssh напрямую в обход этого файла.
set -euo pipefail

SECDIR="$HOME/.config/vless-tunnel-dev"
VM_NAME="vt-win10"
SSH_KEY="$SECDIR/vt_vm_ed25519"
KNOWN_HOSTS="$SECDIR/vm_known_hosts"
SSH_USER="vtadmin"
VIRSH="sudo virsh"

# Стенд, п.1: SSH идёт по ОТДЕЛЬНОМУ адаптеру "vt-mgmt" (host-only сеть
# libvirt vt-mgmt, 10.77.77.0/24, без forward — трафик с хоста на гостя
# и обратно, наружу не NAT'ится). Раньше управляющий канал шёл через тот
# же "default"-адаптер, что и сами сетевые тесты (kill-switch, дёрганье
# маршрутов, reboot) — то, что SSH переживал это, было случайностью
# устройства WFP (фильтры только на исходящие соединения), не гарантией.
# IP статический через DHCP-резервацию по MAC в vt-mgmt (см. vm/create-vm.sh) —
# не через domifaddr на "default", который тесты как раз и ломают.
MGMT_IP="10.77.77.5"

usage() {
  cat >&2 <<EOF
Использование: $(basename "$0") <команда> [аргументы]

  ip                          управляющий IP виртуалки (сеть vt-mgmt)
  test-ip                     IP виртуалки в тестируемой сети (default) — только для чтения/диагностики, НЕ для SSH
  status                      состояние домена + guest agent + (если есть IP) SSH
  guest-alive                 код возврата 0/1 — отвечает ли guest agent (без сети)
  ssh [команда...]            SSH внутрь виртуалки (интерактивно без аргументов)
  scp <src> <dst>             копирование через SSH (используйте vt-win10: как хост в пути)
  exec <path> [arg...]        выполнить команду через QEMU guest agent (без сети)
  rescue                      запустить C:\\dev\\bin\\rescue.ps1 через guest agent
  snapshot <name> [descr...]  внутренний снимок
  snapshot-list                список снимков
  revert <name>               откатиться на снимок
EOF
  exit 2
}

ip_of() { echo "$MGMT_IP"; }

# Тестируемая сеть (default, NAT) — то, что ломают сетевые тесты. Только
# для диагностики со стороны хоста (например, сверить с тем, что видит
# сам тест внутри гостя); SSH/SCP сюда НЕ ходят.
TEST_NIC_MAC="52:54:00:89:6c:85"

test_ip_of() {
  local ip
  ip="$($VIRSH domifaddr "$VM_NAME" 2>/dev/null | awk -v mac="$TEST_NIC_MAC" 'tolower($2)==mac{print $4}' | cut -d/ -f1 | head -1)"
  [ -n "$ip" ] || { echo "Не удалось определить тестовый IP $VM_NAME" >&2; return 1; }
  echo "$ip"
}

do_ssh() {
  local ip
  ip="$(ip_of)"
  ssh -i "$SSH_KEY" -o IdentitiesOnly=yes -o UserKnownHostsFile="$KNOWN_HOSTS" -o StrictHostKeyChecking=accept-new \
      -o ConnectTimeout=10 -o ServerAliveInterval=5 -o ServerAliveCountMax=2 "$SSH_USER@$ip" "$@"
}

do_scp() {
  local ip src="$1" dst="$2"
  ip="$(ip_of)"
  src="${src/vt-win10:/$SSH_USER@$ip:}"
  dst="${dst/vt-win10:/$SSH_USER@$ip:}"
  scp -i "$SSH_KEY" -o IdentitiesOnly=yes -o UserKnownHostsFile="$KNOWN_HOSTS" -o StrictHostKeyChecking=accept-new \
      -o ConnectTimeout=10 -o ServerAliveInterval=5 -o ServerAliveCountMax=2 "$src" "$dst"
}

# guest-exec через QEMU guest agent — работает без сети внутри виртуалки,
# это единственный канал, который остаётся при сломанных маршрутах (план, 5.3/5.5).
do_exec() {
  local path="$1"; shift
  local args_json cmd_json resp pid status_json exited exitcode out_b64 err_b64
  args_json="$(printf '%s\n' "$@" | jq -R . | jq -s .)"
  cmd_json="$(jq -n --arg path "$path" --argjson args "$args_json" \
      '{execute:"guest-exec",arguments:{path:$path,arg:$args,"capture-output":true}}')"
  resp="$($VIRSH qemu-agent-command "$VM_NAME" "$cmd_json")"
  pid="$(echo "$resp" | jq '.return.pid')"
  while true; do
    status_json="$($VIRSH qemu-agent-command "$VM_NAME" "{\"execute\":\"guest-exec-status\",\"arguments\":{\"pid\":$pid}}")"
    exited="$(echo "$status_json" | jq -r '.return.exited')"
    [ "$exited" = "true" ] && break
    sleep 1
  done
  exitcode="$(echo "$status_json" | jq -r '.return.exitcode // 0')"
  out_b64="$(echo "$status_json" | jq -r '.return."out-data" // empty')"
  err_b64="$(echo "$status_json" | jq -r '.return."err-data" // empty')"
  [ -n "$out_b64" ] && echo "$out_b64" | base64 -d
  [ -n "$err_b64" ] && echo "$err_b64" | base64 -d >&2
  return "$exitcode"
}

# Ревью стенда п.27: здоровье гостя определяется guest agent'ом (канал
# независим от сети совсем), а не доступностью SSH — SSH может не
# отвечать и тогда, когда гость полностью жив (сетевая проблема на
# управляющем адаптере/хосте), путать эти два состояния и откатывать VM
# только по недоступности SSH было ошибкой (with-rescue.sh).
guest_alive() {
  $VIRSH qemu-agent-command "$VM_NAME" '{"execute":"guest-ping"}' >/dev/null 2>&1
}

do_status() {
  echo "domstate: $($VIRSH domstate "$VM_NAME" 2>&1)"
  local ping
  ping="$($VIRSH qemu-agent-command "$VM_NAME" '{"execute":"guest-ping"}' 2>&1)"
  echo "guest agent: $ping"
  local ip
  if ip="$(ip_of 2>/dev/null)"; then
    echo "ip: $ip"
    if ssh -i "$SSH_KEY" -o IdentitiesOnly=yes -o UserKnownHostsFile="$KNOWN_HOSTS" -o StrictHostKeyChecking=accept-new \
        -o ConnectTimeout=5 -o BatchMode=yes "$SSH_USER@$ip" "echo ok" >/dev/null 2>&1; then
      echo "ssh: ok"
    else
      echo "ssh: недоступен"
    fi
  else
    echo "ip: неизвестен"
  fi
}

cmd="${1:-}"
[ -n "$cmd" ] || usage
shift || true

case "$cmd" in
  ip) ip_of ;;
  test-ip) test_ip_of ;;
  status) do_status ;;
  guest-alive) guest_alive ;;
  ssh) do_ssh "$@" ;;
  scp) [ $# -eq 2 ] || usage; do_scp "$1" "$2" ;;
  exec) [ $# -ge 1 ] || usage; do_exec "$@" ;;
  rescue) do_exec 'powershell.exe' -NoProfile -ExecutionPolicy Bypass -File 'C:\dev\bin\rescue.ps1' ;;
  snapshot) [ $# -ge 1 ] || usage; name="$1"; shift; $VIRSH snapshot-create-as "$VM_NAME" "$name" "$*" ;;
  snapshot-list) $VIRSH snapshot-list "$VM_NAME" ;;
  revert) [ $# -eq 1 ] || usage; $VIRSH snapshot-revert "$VM_NAME" "$1" --running || $VIRSH snapshot-revert "$VM_NAME" "$1" ;;
  *) usage ;;
esac
