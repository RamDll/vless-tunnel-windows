namespace VlessTunnel.Core;

/// <summary>
/// Битая или неподдерживаемая ссылка. Сообщение — понятное пользователю
/// (по-русски, как в эталоне die()), а не стек-трейс парсера.
/// </summary>
public sealed class LinkParseException(string message) : Exception(message);
