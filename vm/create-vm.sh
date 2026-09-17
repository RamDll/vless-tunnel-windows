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

TMPDIR="$(mktemp -d)"
trap 'rm -rf "$TMPDIR"' EXIT

ADMIN_PASSWORD="$(cat "$SECDIR/vm-admin-password.txt")"
sed "s|{{ADMIN_PASSWORD}}|$ADMIN_PASSWORD|" "$SCRIPT_DIR/autounattend.xml.template" > "$TMPDIR/autounattend.xml"
cp "$SCRIPT_DIR/setup-guest.ps1" "$TMPDIR/vt-setup-guest.ps1"
cp "$SCRIPT_DIR/rescue.ps1" "$TMPDIR/rescue.ps1"
cp "$SCRIPT_DIR/run-test.ps1" "$TMPDIR/run-test.ps1"
cp "$SECDIR/vt_vm_ed25519.pub" "$TMPDIR/authorized_key.pub"

genisoimage -quiet -o "$RESOURCE_ISO" -V VTRES -J -r "$TMPDIR"
log "Ресурсный ISO собран: $RESOURCE_ISO"

log "virt-install: BIOS/SeaBIOS, диск SATA, сеть e1000e (встроенные драйверы, без инъекции в windowsPE — план 5.2)"
sudo virt-install \
  --name "$VM_NAME" \
  --memory 8192 --vcpus 4 \
  --cpu host-passthrough \
  --machine pc \
  --disk path="$DISK",size=64,format=qcow2,bus=sata \
  --cdrom "$WIN10_ISO" \
  --disk path="$VIRTIO_ISO",device=cdrom,bus=sata,readonly=on \
  --disk path="$RESOURCE_ISO",device=cdrom,bus=sata,readonly=on \
  --network network=default,model=e1000e \
  --graphics spice --video qxl --channel spicevmc \
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
