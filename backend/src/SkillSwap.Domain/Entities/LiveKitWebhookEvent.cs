using SkillSwap.Domain.Common;

namespace SkillSwap.Domain.Entities;

/// <summary>
/// Событие, присланное медиасервером LiveKit (webhook), раздел 11.1 ТЗ.
/// Каждое событие записывается один раз: <see cref="EventId"/> уникален, повторная доставка игнорируется.
/// Из этих событий собираются интервалы присутствия и итог урока (этап 5).
/// </summary>
public class LiveKitWebhookEvent : BaseEntity
{
    private LiveKitWebhookEvent()
    {
    }

    /// <summary>Идентификатор события от LiveKit (поле id). Уникален.</summary>
    public string EventId { get; private set; } = string.Empty;

    /// <summary>Тип события: room_started, participant_joined, participant_left, room_finished и т. д.</summary>
    public string Type { get; private set; } = string.Empty;

    public string? RoomName { get; private set; }

    /// <summary>Идентификатор участника в LiveKit (то, что мы кладём в токен). У событий комнаты его нет.</summary>
    public string? ParticipantIdentity { get; private set; }

    /// <summary>Когда событие произошло по часам LiveKit (UTC).</summary>
    public DateTime OccurredAt { get; private set; }

    /// <summary>Когда событие обработано (учтено в посещаемости). Пусто, пока событие не обработано.</summary>
    public DateTime? ProcessedAt { get; private set; }

    /// <summary>Событие учтено в посещаемости (или не требует учёта). Повторный вызов безвреден.</summary>
    public void MarkProcessed(DateTime now) => ProcessedAt ??= now;

    public static LiveKitWebhookEvent Create(
        string eventId, string type, string? roomName, string? participantIdentity, DateTime occurredAt)
    {
        return new LiveKitWebhookEvent
        {
            EventId = eventId,
            Type = type,
            RoomName = roomName,
            ParticipantIdentity = participantIdentity,
            OccurredAt = occurredAt,
        };
    }
}
