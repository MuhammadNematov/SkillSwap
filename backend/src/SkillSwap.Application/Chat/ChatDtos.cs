using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Chat;

/// <summary>Сообщение чата. У удалённого <c>Text</c> = null и заполнен <c>DeletedAt</c>.</summary>
/// <param name="SenderId">Автор; null у системных сообщений.</param>
/// <param name="PaymentWarning">В тексте найден номер карты или призыв к оплате: показать предупреждение о запрете денежных расчётов.</param>
/// <param name="ReadAt">Когда собеседник прочитал; null — ещё нет (одна галочка «отправлено», две — «прочитано»).</param>
public sealed record MessageDto(
    Guid Id,
    Guid ExchangeId,
    Guid? SenderId,
    MessageType Type,
    string? Text,
    bool PaymentWarning,
    Guid? AttachmentId,
    Guid? SessionId,
    DateTime CreatedAt,
    DateTime? EditedAt,
    DateTime? DeletedAt,
    DateTime? ReadAt,
    Files.AttachmentDto? Attachment = null,
    Sessions.LessonSessionDto? Session = null);

/// <summary>Страница истории. <c>Items</c> по возрастанию времени (старые сверху); <c>NextBefore</c> — курсор следующей, более старой страницы (null — истории больше нет).</summary>
public sealed record MessagePageDto(IReadOnlyList<MessageDto> Items, string? NextBefore);

public sealed record MarkReadRequest(Guid? UpToMessageId);

/// <summary>Результат «прочитано»: событие <c>messagesRead</c> уходит собеседнику в хаб.</summary>
public sealed record ReadReceiptDto(Guid ExchangeId, Guid ReaderId, Guid? UpToMessageId, int Marked, DateTime ReadAt);

public sealed record MessageDeletedDto(Guid ExchangeId, Guid MessageId, DateTime DeletedAt);

public sealed record TypingDto(Guid ExchangeId, Guid UserId);

public enum ChatMode
{
    /// <summary>Можно читать и писать (обмен активен).</summary>
    ReadWrite,

    /// <summary>Только чтение: обмен заморожен либо завершён или прерван не более 30 дней назад.</summary>
    ReadOnly,

    /// <summary>Прошло больше 30 дней после завершения: участникам чат недоступен (через 90 дней его удалит фоновая очистка).</summary>
    Archived,
}

/// <param name="ReadOnlyReason">frozen, completed или aborted (только при ReadOnly).</param>
/// <param name="ReadableUntil">До какого момента чат доступен для чтения после закрытия обмена; null у активного и замороженного.</param>
public sealed record ChatStateDto(ChatMode Mode, string? ReadOnlyReason, DateTime? ReadableUntil);

/// <summary>Правила доступа к чату по статусу обмена (раздел 7.1 ТЗ).</summary>
public static class ChatAccess
{
    /// <summary>Сколько дней после закрытия обмена чат остаётся доступным для чтения.</summary>
    public const int ReadOnlyDaysAfterClose = 30;

    public static ChatStateDto Evaluate(ExchangeStatus status, DateTime? closedAt, DateTime now)
    {
        switch (status)
        {
            case ExchangeStatus.Active:
                return new ChatStateDto(ChatMode.ReadWrite, null, null);
            case ExchangeStatus.Frozen:
                return new ChatStateDto(ChatMode.ReadOnly, "frozen", null);
            default:
                var until = (closedAt ?? now).AddDays(ReadOnlyDaysAfterClose);
                return now < until
                    ? new ChatStateDto(ChatMode.ReadOnly, status == ExchangeStatus.Completed ? "completed" : "aborted", until)
                    : new ChatStateDto(ChatMode.Archived, null, until);
        }
    }
}
