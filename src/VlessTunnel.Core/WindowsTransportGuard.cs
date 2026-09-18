using VlessTunnel.Core.Models;

namespace VlessTunnel.Core;

/// <summary>
/// Windows-специфичная проверка транспорта ПОСЛЕ разбора ссылки
/// (ревью п.11; план, раздел 1, "Не делаем": "legacy-транспорты
/// type=quic / type=http... в нём нет TUN-инбаунда. Такая ссылка →
/// понятная ошибка"). Намеренно отдельно от <see cref="LinkParser"/> —
/// тот сохраняет паритет с Linux-эталоном (golden-тесты, план 3.1/5.7)
/// и продолжает ПРИНИМАТЬ эти транспорты при разборе, как и Linux-
/// версия; здесь — Windows-специфичный отказ ПОСЛЕ парсинга, не часть
/// общего парсера. Без этой проверки конфиг собирался, xray.exe падал
/// на разборе конфига, а пользователь через 40 секунд получал
/// TimeoutException про TUN-адаптер — ни слова о настоящей причине.
/// </summary>
public static class WindowsTransportGuard
{
    private static readonly string[] UnsupportedTunNetworks = ["quic", "http"];

    public static void RequireTunSupported(ParsedLink link)
    {
        if (UnsupportedTunNetworks.Contains(link.Network))
        {
            throw new LinkParseException(
                $"транспорт type={link.Network} не поддерживается на Windows — в ядре Xray нет TUN-инбаунда для него (план, раздел 1). Нужна ссылка с другим транспортом (tcp/ws/grpc/xhttp и т.д.)");
        }
    }
}
