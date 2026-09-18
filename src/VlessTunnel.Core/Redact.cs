using System.Text.RegularExpressions;

namespace VlessTunnel.Core;

/// <summary>
/// Защита от случайной утечки секретов в логи (план, этап 7: "секреты не
/// попадают в вывод"). Наш собственный код не логирует UUID/pbk/sid
/// напрямую (см. TunnelController.SetLink — только host, не вся ссылка),
/// но вывод СТОРОННЕГО процесса (xray.exe) — не наш контракт: в норме он
/// не печатает содержимое конфига на loglevel=warning, но при ошибке
/// разбора конфига гипотетически мог бы процитировать фрагмент с UUID.
/// Редактируется защитно, самый уверенный по формату паттерн — UUID
/// (пользовательский идентификатор VLESS); pbk/sid, в отличие от него, —
/// значения без узнаваемой сигнатуры (произвольный base64/hex), которые
/// нельзя отличить от случайного не-секретного текста без ложных
/// срабатываний, поэтому не редактируются отдельно.
/// </summary>
public static partial class Redact
{
    public static string Secrets(string text) => UuidRegex().Replace(text, "«скрыто»");

    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex UuidRegex();
}
