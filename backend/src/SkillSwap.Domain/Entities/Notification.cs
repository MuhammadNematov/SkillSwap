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

    /// <summary>Когда письмо-дубль поставлено в очередь (null — не отправлялось); защита от повторной отправки при повторе задачи.</summary>
    public DateTime? EmailQueuedAt { get; private set; }

    public static Notification Create(Guid userId, NotificationType type, string payloadJson)
    {
        if (payloadJson.Length > PayloadMaxLength)
        {
            throw new Exceptions.DomainValidationException("payload_too_long", "Данные уведомления слишком длинные.");
        }

        return new Notification { UserId = userId, Type = type, PayloadJson = payloadJson };
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
}
