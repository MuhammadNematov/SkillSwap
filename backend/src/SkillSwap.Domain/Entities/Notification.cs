using SkillSwap.Domain.Common;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Domain.Entities;

/// <summary>
/// Уведомление в интерфейсе (таблица Notifications, раздел 13 ТЗ). Тип и данные (<see cref="PayloadJson"/>) нейтральны к языку:
/// текст на языке пользователя строит фронтенд.
/// </summary>
public class Notification : BaseEntity
{
    public const int PayloadMaxLength = 4000;

    private Notification()
    {
    }

    public Guid UserId { get; private set; }
    public NotificationType Type { get; private set; }
    public string PayloadJson { get; private set; } = "{}";
    public bool IsRead { get; private set; }
    public DateTime? ReadAt { get; private set; }

    /// <summary>Обмен, к которому относится уведомление (непрочитанные сообщения); у остальных null.</summary>
    public Guid? ExchangeId { get; private set; }

    /// <summary>Когда письмо-дубль поставлено в очередь (null — не отправлялось); защита от повторной отправки при повторе задачи.</summary>
    public DateTime? EmailQueuedAt { get; private set; }

    /// <summary>Когда уведомление передано в отправку в Telegram; защита от повторной отправки при повторе задачи.</summary>
    public DateTime? TelegramQueuedAt { get; private set; }

    public void MarkTelegramQueued(DateTime now) => TelegramQueuedAt ??= now;

    public static Notification Create(Guid userId, NotificationType type, string payloadJson, Guid? exchangeId = null)
    {
        EnsurePayload(payloadJson);
        return new Notification { UserId = userId, Type = type, PayloadJson = payloadJson, ExchangeId = exchangeId };
    }

    /// <summary>
    /// Обновляет непрочитанное уведомление новыми данными (например, счётчиком) и поднимает его наверх списка: время создания
    /// становится временем последнего обновления. Прочитанное уведомление не обновляется, вместо него создаётся новое.
    /// </summary>
    public void Refresh(string payloadJson, DateTime now)
    {
        if (IsRead)
        {
            throw new Exceptions.InvalidStateTransitionException(nameof(Notification), "Read", "Refreshed");
        }

        EnsurePayload(payloadJson);
        PayloadJson = payloadJson;
        CreatedAt = now;
    }

    /// <summary>Прочитано. Повторный вызов ничего не меняет.</summary>
    public void MarkRead(DateTime now)
    {
        if (IsRead)
        {
            return;
        }

        IsRead = true;
        ReadAt = now;
    }

    public void MarkEmailQueued(DateTime now) => EmailQueuedAt ??= now;

    /// <summary>Фиксирует отправку письма о непрочитанных сообщениях; время нужно для лимита «не чаще раза в сутки».</summary>
    public void RecordEmailSent(DateTime now) => EmailQueuedAt = now;

    private static void EnsurePayload(string payloadJson)
    {
        if (payloadJson.Length > PayloadMaxLength)
        {
            throw new Exceptions.DomainValidationException("payload_too_long", "Данные уведомления слишком длинные.");
        }
    }
}
