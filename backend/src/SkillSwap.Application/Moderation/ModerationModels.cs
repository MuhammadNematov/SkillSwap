using SkillSwap.Application.ExchangeRequests;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Moderation;

/// <summary>Какие обращения перечислять модератору.</summary>
public enum ModeratorScope
{
    /// <summary>Очередь: ещё не взятые (<c>Open</c>), без обращений, где у модератора конфликт интересов.</summary>
    Queue,

    /// <summary>Мои: взятые мной и ещё не закрытые окончательно.</summary>
    Mine,
}

/// <summary>Оспариваемый отзыв в обращении для модератора.</summary>
public sealed record ModerationReviewDto(
    Guid Id, Guid AuthorId, Guid TargetId, int ScoreKnowledge, int ScoreExplaining, int ScorePunctuality, int ScorePoliteness, decimal Average, string Text, ReviewStatus Status);

/// <summary>Обращение глазами модератора.</summary>
/// <param name="DueAt">Срок реакции (24 или 72 часа с подачи).</param>
/// <param name="IsOverdue">Срок реакции прошёл, а обращение не взято.</param>
/// <param name="IsMine">Обращение взял этот модератор.</param>
/// <param name="HasChatAccess">У меня сейчас есть доступ к чату этого обмена по этому обращению (читать: <c>GET /moderation/cases/{id}/chat</c>).</param>
/// <param name="Review">Оспариваемый отзыв (только спор по отзыву).</param>
/// <param name="PendingQuestions">Сколько заданных мной вопросов ещё без ответа.</param>
public sealed record ModerationCaseDto(
    Guid Id,
    CaseType Type,
    CaseCategory Category,
    CaseStatus Status,
    CasePriority Priority,
    Guid ExchangeId,
    Guid? ReviewId,
    RequestUserDto OpenedBy,
    RequestUserDto Against,
    string Description,
    bool FromCall,
    DateTime CreatedAt,
    DateTime DueAt,
    bool IsOverdue,
    Guid? ModeratorId,
    bool IsMine,
    bool HasChatAccess,
    IReadOnlyList<Guid> EvidenceMessageIds,
    ModerationReviewDto? Review,
    CaseDecision? Decision,
    bool AlsoWarned,
    string? DecisionReason,
    DateTime? ResolvedAt,
    DateTime? ResponseDeadlineAt,
    int PendingQuestions);

public sealed record ModerationCaseListDto(IReadOnlyList<ModerationCaseDto> Items, int Page, int PageSize, int TotalCount);

/// <summary>Прежняя версия сообщения (до правки или удаления).</summary>
public sealed record MessageRevisionDto(string Text, DateTime ChangedAt);

/// <summary>
/// Сообщение глазами модератора: удалённое показывается с текстом (<c>DeletedAt</c> заполнен), у исправленного есть прежние версии.
/// </summary>
/// <param name="IsEvidence">Сообщение приложено к обращению как доказательство.</param>
public sealed record ModerationMessageDto(
    Guid Id,
    Guid? SenderId,
    MessageType Type,
    string? Text,
    bool PaymentWarning,
    Guid? AttachmentId,
    Files.AttachmentDto? Attachment,
    DateTime CreatedAt,
    DateTime? EditedAt,
    DateTime? DeletedAt,
    DateTime? ReadAt,
    bool IsEvidence,
    IReadOnlyList<MessageRevisionDto> Revisions);

public sealed record ModerationChatPageDto(
    Guid CaseId, Guid ExchangeId, RequestUserDto UserA, RequestUserDto UserB, IReadOnlyList<ModerationMessageDto> Items, string? NextBefore);
