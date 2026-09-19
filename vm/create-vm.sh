#!/usr/bin/env bash
# Идемпотентно создаёт vt-win10 "с нуля" (план, раздел 5.3): если домен уже
# определён — ничего не делает, если только не передан --recreate.
# Только создание + безучастная установка Windows; проверка SSH/guest agent
# и снимок clean делаются отдельно через vmctl.sh (5.9 разделяет файлы по
# ответственности).
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SECDIR="$HOME/.config/vless-tunnel-dev"
VMDIR="$HOME/.local/share/vt-vm"
ISODIR="$HOME/vm-iso"
VM_NAME="vt-win10"
DISK="$VMDIR/${VM_NAME}.qcow2"
RESOURCE_ISO="$VMDIR/${VM_NAME}-resources.iso"
WIN10_ISO="$ISODIR/win10.iso"
VIRTIO_ISO="$ISODIR/virtio-win.iso"

RECREATE=0
for arg in "$@"; do
  case "$arg" in
    --recreate) RECREATE=1 ;;
    *) echo "Неизвестный аргумент: $arg" >&2; exit 2 ;;
  esac
done

log() { echo "[create-vm] $*"; }

for f in "$WIN10_ISO" "$VIRTIO_ISO" "$SECDIR/vm-admin-password.txt" "$SECDIR/vt_vm_ed25519.pub"; do
  [ -f "$f" ] || { echo "Нужен файл: $f" >&2; exit 1; }
done

PINNED_SHA="$(awk -F' = ' '/^sha256/{print $2; exit}' "$SCRIPT_DIR/pinned-versions.txt")"
ACTUAL_SHA="$(sha256sum "$VIRTIO_ISO" | awk '{print $1}')"
if [ "$PINNED_SHA" != "$ACTUAL_SHA" ]; then
  echo "sha256 $VIRTIO_ISO не совпадает с pinned-versions.txt (ожидали $PINNED_SHA, получили $ACTUAL_SHA)" >&2
  exit 1
fi

if sudo virsh dominfo "$VM_NAME" >/dev/null 2>&1; then
  if [ "$RECREATE" != "1" ]; then
    log "$VM_NAME уже существует, ничего не делаю (--recreate для пересоздания)"
    exit 0
  fi
  log "--recreate: удаляю существующую $VM_NAME"
  sudo virsh destroy "$VM_NAME" >/dev/null 2>&1 || true
  sudo virsh undefine "$VM_NAME" --nvram --snapshots-metadata >/dev/null 2>&1 || true
  rm -f "$DISK"
fi

mkdir -p "$VMDIR"

# Стенд, п.1: управляющая сеть vt-mgmt (host-only, отдельная от "default",
# см. vm/vt-mgmt-network.xml) — SSH держится на ней отдельно от того, что
# ломают сетевые тесты. Идемпотентно: если уже определена, ничего не делаем.
if ! sudo virsh net-info vt-mgmt >/dev/null 2>&1; then
  log "Определяю сеть vt-mgmt"
  sudo virsh net-define "$SCRIPT_DIR/vt-mgmt-network.xml"
fi
# Не парсим локализованный вывод virsh (тот же урок, что с метками sc.exe,
# см. PLAN-windows.md) — просто пробуем запустить и глотаем "уже активна".
sudo virsh net-start vt-mgmt >/dev/null 2>&1 || true
sudo virsh net-autostart vt-mgmt >/dev/null 2>&1 || true

# Ревью п.22: третий сетевой адаптер для живых тестов смены сети — NAT,
# отдельная подсеть от default (см. vm/vt-test2-network.xml). vt-mgmt при
# этом не трогаем и не используем как объект теста (управляющий канал).
if ! sudo virsh net-info vt-test2 >/dev/null 2>&1; then
  log "Определяю сеть vt-test2"
  sudo virsh net-define "$SCRIPT_DIR/vt-test2-network.xml"
fi
sudo virsh net-start vt-test2 >/dev/null 2>&1 || true
sudo virsh net-autostart vt-test2 >/dev/null 2>&1 || true

TMPDIR="$(mktemp -d)"
trap 'rm -rf "$TMPDIR"' EXIT

ADMIN_PASSWORD="$(cat "$SECDIR/vm-admin-password.txt")"
sed "s|{{ADMIN_PASSWORD}}|$ADMIN_PASSWORD|" "$SCRIPT_DIR/autounattend.xml.template" > "$TMPDIR/autounattend.xml"

# $OEM$\$1\ НЕ РАБОТАЕТ для нашего сценария (проверено эмпирически дважды:
# офлайн-чтение диска показывает, что Windows Setup копирует такую папку на
# C:\ только когда она лежит на самом установочном ISO рядом с sources\, а
# не на отдельном втором CD-ROM с ответным файлом — так что для обычной
# установки без WDS/MDT это тупик). Файлы лежат прямо в корне ресурсного
# ISO; FirstLogonCommands сам перебирает буквы дисков (проверено вживую —
# файл точно находится и запускается, когда путь угадан правильно).
cp "$SCRIPT_DIR/setup-guest.ps1" "$TMPDIR/vt-setup-guest.ps1"
cp "$SCRIPT_DIR/rescue.ps1" "$TMPDIR/rescue.ps1"
cp "$SCRIPT_DIR/run-test.ps1" "$TMPDIR/run-test.ps1"
cp "$SECDIR/vt_vm_ed25519.pub" "$TMPDIR/authorized_key.pub"

genisoimage -quiet -o "$RESOURCE_ISO" -V VTRES -J -r "$TMPDIR"
log "Ресурсный ISO собран: $RESOURCE_ISO"

log "virt-install: BIOS/SeaBIOS, все диски на одной шине SATA с явным boot_order (сеть e1000e — встроенные драйверы, без инъекции в windowsPE, план 5.2)"
# Важно: все диски/CD-ROM на ОДНОЙ шине (sata), с явным boot_order на
# каждом. Смешивание bus=ide (куда --cdrom сажает медиа по умолчанию) и
# bus=sata привело к тому, что общий <os><boot dev='cdrom'/> указывал не на
# тот привод и SeaBIOS не мог прочитать загрузочный CD.
sudo virt-install \
  --name "$VM_NAME" \
  --memory 8192 --vcpus 4 \
  --cpu host-passthrough \
  --machine pc \
  --boot cdrom,hd \
  --disk path="$DISK",size=64,format=qcow2,bus=sata,boot_order=2 \
  --disk path="$WIN10_ISO",device=cdrom,bus=sata,boot_order=1 \
  --disk path="$VIRTIO_ISO",device=cdrom,bus=sata,readonly=on \
  --disk path="$RESOURCE_ISO",device=cdrom,bus=sata,readonly=on \
  --network network=default,model=e1000e \
  --network network=vt-mgmt,model=e1000e,mac=52:54:00:89:6c:86 \
  --network network=vt-test2,model=e1000e,mac=52:54:00:89:6c:87 \
  --graphics spice --video qxl --channel spicevmc \
  --channel unix,target_type=virtio,name=org.qemu.guest_agent.0 \
  --os-variant win10 \
  --noautoconsole

log "Установка идёт. Жду выключения VM — это сигнал от setup-guest.ps1, что всё готово."
TIMEOUT_S=$((90*60))
ELAPSED=0
while true; do
  STATE="$(sudo virsh domstate "$VM_NAME" 2>/dev/null || echo unknown)"
  if [ "$STATE" = "shut off" ]; then
    log "VM выключилась — установка завершена."
    break
  fi
  if [ "$ELAPSED" -ge "$TIMEOUT_S" ]; then
    echo "Таймаут ожидания установки (${TIMEOUT_S}с), последнее состояние: $STATE" >&2
    exit 1
  fi
  sleep 15
  ELAPSED=$((ELAPSED + 15))
done

log "Готово. Дальше: vmctl.sh status / vmctl.sh ssh для проверки, затем снимок clean."
