# vless-tunnel-windows — прозрачное проксирование всего трафика Windows через VLESS

Нативный порт [vless-tunnel](https://github.com/RamDll/vless-tunnel) (Linux) под
Windows 10/11: весь трафик (TCP и UDP, включая QUIC/HTTP-3) идёт через туннель
VLESS на базе Xray-core через TUN-адаптер (Wintun), с kill-switch на WFP —
пока туннель не поднят, интернета в обход него нет.

* служба Windows (`vless-tunnel`) поднимает TUN, маршруты и kill-switch;
* трей + главное окно — статус, внешний IP, переключатель, список параметров,
  меню действий (сменить сервер / проверить туннель / журнал / диагностика / удалить);
* CLI (`VlessTunnel.Cli.exe`) — те же действия без графики, для скриптов;
* `doctor` снимает зависшие фильтры и предупреждает о сбитых часах (ломают REALITY/TLS);
* автозапуск службы при загрузке и трея при входе — задачи установщика, по желанию.

---

## Установка

1. Скачайте с [Releases](https://github.com/RamDll/vless-tunnel-windows/releases)
   три файла: `vless-tunnel-setup.exe`, `trust.ps1`, `vless-tunnel.cer` —
   положите рядом, в одну папку.
2. Запустите `trust.ps1` от имени администратора (правой кнопкой →
   «Выполнить с помощью PowerShell», либо):

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\trust.ps1
   ```

   Скрипт сверяет отпечаток `vless-tunnel.cer` с зашитым в себя, ставит
   сертификат в доверенные хранилища Windows и запускает установщик —
   без этого шага Windows покажет «Неизвестный издатель» (сертификат
   самоподписанный, не от публичного удостоверяющего центра).
3. В мастере установки при желании включите автозапуск службы/трея,
   нажмите «Установить».
4. При первом запуске трея (значок в области уведомлений) —
   «Настроить» → вставьте вашу `vless://`-ссылку.

Обновление — тот же установщик поверх (настройки сохраняются). Удаление —
через «Установка и удаление программ» или сразу деинсталлятором в
`C:\Program Files\vless-tunnel\unins000.exe`: спросит, удалять ли
сохранённую ссылку на сервер, а при тихом удалении (`/VERYSILENT`) —
оставит её.

## CLI

```
"C:\Program Files\vless-tunnel\VlessTunnel.Cli.exe" on              # включить туннель
"C:\Program Files\vless-tunnel\VlessTunnel.Cli.exe" off             # выключить
"C:\Program Files\vless-tunnel\VlessTunnel.Cli.exe" toggle          # переключить
"C:\Program Files\vless-tunnel\VlessTunnel.Cli.exe" restart         # перезапустить
"C:\Program Files\vless-tunnel\VlessTunnel.Cli.exe" status --json   # состояние
"C:\Program Files\vless-tunnel\VlessTunnel.Cli.exe" set-link "vless://..."
"C:\Program Files\vless-tunnel\VlessTunnel.Cli.exe" test            # HTTP/SOCKS5/TCP/DNS через туннель
"C:\Program Files\vless-tunnel\VlessTunnel.Cli.exe" doctor          # снять зависшие фильтры, проверить часы
```

Управлять туннелем может любой пользователь без прав администратора —
права нужны только на установку/обновление/удаление самой программы.

## Аварийное восстановление

Если по какой-то причине программа удалена, а фильтры kill-switch
остались (интернета нет вообще) — `vm/rescue.ps1` из репозитория
снимает их без установленной программы, от имени администратора:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\rescue.ps1
```

## Статус проекта

Разработка в процессе — подробный план, найденные и исправленные баги,
что уже живьём проверено на стенде, а что нет — в
[`PLAN-windows.md`](PLAN-windows.md).
