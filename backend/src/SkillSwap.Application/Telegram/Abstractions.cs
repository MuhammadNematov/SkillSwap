using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Telegram;

/// <summary>Кнопка-ссылка под сообщением.</summary>
public sealed record TelegramButton(string Text, string Url);

public enum TelegramSendStatus
{
    Sent,

    /// <summary>403: пользователь заблокировал бота (или бот удалён из чата) — связь надо снять.</summary>
    Blocked,

    /// <summary>429: слишком часто, повторить через <see cref="TelegramSendResult.RetryAfter"/>.</summary>
    RateLimited,

    /// <summary>Сбой сети или 5xx: повторить позже.</summary>
    Transient,

    /// <summary>Telegram отклонил запрос (400 и т. п.): повтор не поможет.</summary>
    Rejected,
}

public sealed record TelegramSendResult(TelegramSendStatus Status, TimeSpan? RetryAfter = null, string? Description = null)
{
    public static readonly TelegramSendResult Ok = new(TelegramSendStatus.Sent);
}

/// <summary>Входящее сообщение боту (из webhook или getUpdates). Не из личного чата — игнорируется.</summary>
public sealed record TelegramUpdate(long UpdateId, TelegramIncomingMessage? Message);

public sealed record TelegramIncomingMessage(long ChatId, bool IsPrivate, string? Text, string? LanguageCode);

/// <summary>Bot API. Реализация не пишет в журнал адреса запросов (в них токен).</summary>
public interface ITelegramClient
{
    Task<TelegramSendResult> SendMessageAsync(long chatId, string text, TelegramButton? button, CancellationToken ct);

    /// <summary>Long polling: ждёт до <paramref name="timeoutSeconds"/> секунд новые обновления начиная с <paramref name="offset"/>.</summary>
    Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(long offset, int timeoutSeconds, CancellationToken ct);

    Task DeleteWebhookAsync(CancellationToken ct);

    Task SetWebhookAsync(string url, string secretToken, CancellationToken ct);
}

public interface ITelegramStore
{
    Task<TelegramLink?> FindByUserAsync(Guid userId, CancellationToken ct);

    Task<TelegramLink?> FindByChatAsync(long chatId, CancellationToken ct);

    void Add(TelegramLink link);

    void Remove(TelegramLink link);

    Task<TelegramLinkCode?> FindCodeAsync(string codeHash, CancellationToken ct);

    void Add(TelegramLinkCode code);

    /// <summary>Удаляет все коды пользователя (новый код заменяет прежние).</summary>
    Task DeleteCodesAsync(Guid userId, CancellationToken ct);

    Task<IReadOnlyList<NotificationPreference>> ListPreferencesAsync(Guid userId, CancellationToken ct);

    void Add(NotificationPreference preference);

    Task<bool> IsEnabledAsync(Guid userId, NotificationCategory category, NotificationChannel channel, CancellationToken ct);

    /// <summary>
    /// Атомарно занимает «окно» диалога: true, если с прошлой отправки прошло не меньше <paramref name="interval"/> (и время обновлено). Два параллельных вызова дадут true только одному.
    /// </summary>
    Task<bool> TryAcquireDialogSlotAsync(Guid userId, Guid exchangeId, DateTime now, TimeSpan interval, CancellationToken ct);

    /// <summary>Удаление аккаунта: связь, коды, настройки и отметки диалогов.</summary>
    Task DeleteUserDataAsync(Guid userId, CancellationToken ct);

    Task SaveAsync(CancellationToken ct);
}

/// <summary>Есть ли у пользователя открытые соединения с сайтом (SignalR). Хранится в памяти процесса.</summary>
public interface IUserPresence
{
    bool IsOnline(Guid userId);
}

/// <summary>Сообщает открытым вкладкам пользователя, что связь с Telegram изменилась.</summary>
public interface ITelegramStatusPusher
{
    Task PushAsync(Guid userId, bool connected, CancellationToken ct);
}
