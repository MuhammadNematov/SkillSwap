using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Moderation;

/// <summary>Кому модератор задаёт вопрос.</summary>
public enum ExplanationRecipient
{
    /// <summary>Заявителю.</summary>
    Opener,

    /// <summary>Тому, на кого подано обращение.</summary>
    Against,

    /// <summary>Обоим (каждому уходит свой вопрос с тем же текстом).</summary>
    Both,
}

/// <summary>Вопрос или ответ в переписке по обращению.</summary>
public enum ExplanationKind
{
    Question,
    Answer,
}

/// <summary>Вопрос модератора: <c>text</c> 1–2000 символов.</summary>
public sealed record AskQuestionRequest(ExplanationRecipient Recipient, string? Text);

/// <summary>Ответ участника на конкретный вопрос модератора.</summary>
public sealed record AnswerQuestionRequest(Guid QuestionId, string? Text);

/// <param name="InReplyToId">У ответа: id вопроса.</param>
/// <param name="Answered">У вопроса: на него уже ответили.</param>
public sealed record ExplanationDto(
    Guid Id, ExplanationKind Kind, Guid SenderId, Guid RecipientId, string Text, Guid? InReplyToId, bool Answered, DateTime CreatedAt);

/// <summary>Переписка по обращению. <c>ResponseDeadlineAt</c> заполнен, пока ждём ответов.</summary>
public sealed record ExplanationThreadDto(Guid CaseId, CaseStatus Status, DateTime? ResponseDeadlineAt, IReadOnlyList<ExplanationDto> Items);

/// <summary>Хранилище вопросов модератора и ответов участников (таблица CaseMessages).</summary>
public interface ICaseMessageStore
{
    void Add(CaseMessage message);

    Task<CaseMessage?> FindAsync(Guid id, CancellationToken ct);

    /// <summary>Вся переписка по обращению по времени; <paramref name="forUser"/> оставляет только сообщения, где он отправитель или получатель.</summary>
    Task<IReadOnlyList<CaseMessage>> ListAsync(Guid caseId, Guid? forUser, CancellationToken ct);

    /// <summary>Сколько вопросов уже задано по обращению.</summary>
    Task<int> CountQuestionsAsync(Guid caseId, CancellationToken ct);

    /// <summary>Есть ли по обращению вопросы без ответа.</summary>
    Task<bool> HasUnansweredAsync(Guid caseId, CancellationToken ct);

    /// <summary>Сколько вопросов без ответа по каждому обращению (для получателя <paramref name="recipientId"/> или, если null, всего).</summary>
    Task<IReadOnlyDictionary<Guid, int>> CountUnansweredAsync(IReadOnlyCollection<Guid> caseIds, Guid? recipientId, CancellationToken ct);

    /// <summary>Id вопросов, на которые уже ответили.</summary>
    Task<IReadOnlySet<Guid>> AnsweredQuestionIdsAsync(IReadOnlyCollection<Guid> questionIds, CancellationToken ct);

    Task SaveAsync(CancellationToken ct);
}
