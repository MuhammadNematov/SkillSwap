using SkillSwap.Domain.Common;
using SkillSwap.Domain.Enums;
using SkillSwap.Domain.Exceptions;

namespace SkillSwap.Domain.Entities;

/// <summary>
/// Сообщение чата, приложенное к обращению (таблица CaseEvidence, составной ключ CaseId + MessageId). Такие сообщения не удаляются очисткой
/// чата и хранятся вместе с обращением 1 год (раздел 10.3 ТЗ): внешний ключ на сообщение запрещает его удаление.
/// </summary>
public class CaseEvidence
{
    private CaseEvidence()
    {
    }

    public Guid CaseId { get; private set; }
    public Guid MessageId { get; private set; }
    public DateTime AddedAt { get; private set; }

    public static CaseEvidence Create(Guid caseId, Guid messageId, DateTime now) =>
        new() { CaseId = caseId, MessageId = messageId, AddedAt = now };
}

/// <summary>Вопрос модератора стороне или её ответ (таблица CaseMessages, раздел 8.2 ТЗ, пункт «Пояснения»).</summary>
public class CaseMessage : BaseEntity
{
    public const int TextMaxLength = 2000;

    private CaseMessage()
    {
    }

    public Guid CaseId { get; private set; }
    public Guid SenderId { get; private set; }
    public Guid RecipientId { get; private set; }
    public string Text { get; private set; } = string.Empty;

    /// <summary>Ответ на этот вопрос; у вопроса модератора null.</summary>
    public Guid? InReplyToId { get; private set; }

    public static CaseMessage Create(Guid caseId, Guid senderId, Guid recipientId, string? text, Guid? inReplyToId = null)
    {
        if (senderId == recipientId)
        {
            throw new DomainValidationException("case_message_self", "Сообщение самому себе невозможно.");
        }

        var clean = text?.Trim() ?? string.Empty;
        if (clean.Length == 0)
        {
            throw new DomainValidationException("case_message_empty", "Текст не может быть пустым.");
        }

        if (clean.Length > TextMaxLength)
        {
            throw new DomainValidationException("case_message_too_long", $"Не больше {TextMaxLength} символов.");
        }

        return new CaseMessage { CaseId = caseId, SenderId = senderId, RecipientId = recipientId, Text = clean, InReplyToId = inReplyToId };
    }
}

/// <summary>
/// Апелляция по обращению (таблица CaseAppeals, раздел 8.2 ТЗ, пункт 5): одна на обращение. Админ видит материалы кейса, но не весь чат;
/// если нужна повторная проверка чата, он назначает другого модератора, тот пишет заключение, а окончательное решение принимает админ.
/// </summary>
public class CaseAppeal : BaseEntity
{
    public const int ReasonMaxLength = 2000;
    public const int ConclusionMaxLength = 4000;

    private CaseAppeal()
    {
    }

    public Guid CaseId { get; private set; }
    public Guid AppellantId { get; private set; }
    public string Reason { get; private set; } = string.Empty;

    /// <summary>Админ, который ведёт апелляцию.</summary>
    public Guid? AdminId { get; private set; }

    public Guid? ReviewerModeratorId { get; private set; }
    public string? ReviewerConclusion { get; private set; }
    public AppealOutcome? Outcome { get; private set; }

    /// <summary>Решение, на которое админ заменил прежнее (при <see cref="AppealOutcome.Changed"/>).</summary>
    public CaseDecision? FinalDecision { get; private set; }

    public DateTime? DecidedAt { get; private set; }

    public static CaseAppeal Create(Guid caseId, Guid appellantId, string? reason) => new()
    {
        CaseId = caseId,
        AppellantId = appellantId,
        Reason = Case.CleanText(reason, CaseRules.DescriptionMinLength, ReasonMaxLength, "appeal_reason"),
    };

    /// <summary>Админ взял апелляцию на себя (без повторной проверки чата можно сразу вынести решение).</summary>
    public void TakeByAdmin(Guid adminId)
    {
        AdminId ??= adminId;
    }

    public void AssignReviewer(Guid adminId, Guid reviewerModeratorId)
    {
        if (Outcome is not null)
        {
            throw new InvalidStateTransitionException(nameof(CaseAppeal), "Decided", "ReviewerAssigned");
        }

        if (ReviewerModeratorId is not null)
        {
            throw new DomainValidationException("appeal_reviewer_already_assigned", "Повторную проверку уже назначили.");
        }

        AdminId = adminId;
        ReviewerModeratorId = reviewerModeratorId;
    }

    public void WriteConclusion(Guid moderatorId, string? conclusion)
    {
        if (ReviewerModeratorId != moderatorId)
        {
            throw new DomainValidationException("not_appeal_reviewer", "Заключение пишет назначенный модератор.");
        }

        if (ReviewerConclusion is not null)
        {
            throw new DomainValidationException("appeal_conclusion_already_written", "Заключение уже написано.");
        }

        ReviewerConclusion = Case.CleanText(conclusion, CaseRules.ReasonMinLength, ConclusionMaxLength, "appeal_conclusion");
    }

    public void Decide(Guid adminId, AppealOutcome outcome, CaseDecision? finalDecision, DateTime now)
    {
        if (Outcome is not null)
        {
            throw new InvalidStateTransitionException(nameof(CaseAppeal), "Decided", "Decided");
        }

        if (ReviewerModeratorId is not null && ReviewerConclusion is null)
        {
            throw new DomainValidationException("appeal_conclusion_required", "Дождитесь заключения назначенного модератора.");
        }

        if ((outcome == AppealOutcome.Changed) != (finalDecision is not null))
        {
            throw new DomainValidationException("appeal_final_decision_mismatch", "Новое решение указывается только при замене решения.");
        }

        AdminId = adminId;
        Outcome = outcome;
        FinalDecision = finalDecision;
        DecidedAt = now;
    }
}

/// <summary>
/// Временный доступ модератора к чату обмена (таблица ChatAccessGrants, раздел 10.2 ТЗ). Создаётся при взятии обращения в работу (или при повторной
/// проверке по апелляции) и отзывается при решении. Читать чат можно только при активном доступе, каждый запрос пишется в журнал аудита.
/// </summary>
public class ChatAccessGrant : BaseEntity
{
    private ChatAccessGrant()
    {
    }

    public Guid CaseId { get; private set; }
    public Guid ModeratorId { get; private set; }
    public Guid ExchangeId { get; private set; }
    public GrantReason Reason { get; private set; }
    public DateTime GrantedAt { get; private set; }
    public DateTime? RevokedAt { get; private set; }

    public bool IsActive => RevokedAt is null;

    public static ChatAccessGrant Create(Guid caseId, Guid moderatorId, Guid exchangeId, GrantReason reason, DateTime now) =>
        new() { CaseId = caseId, ModeratorId = moderatorId, ExchangeId = exchangeId, Reason = reason, GrantedAt = now };

    /// <summary>Отзывает доступ. Повторный вызов безвреден (момент первого отзыва не меняется).</summary>
    /// <returns>true, если доступ отозван этим вызовом.</returns>
    public bool Revoke(DateTime now)
    {
        if (RevokedAt is not null)
        {
            return false;
        }

        RevokedAt = now;
        return true;
    }
}

/// <summary>
/// Предупреждение в профиль нарушителя (таблица Warnings, раздел 8.3 ТЗ). Действует 12 месяцев; три действующих предупреждения автоматически
/// создают запрос на бан для админа. Предупреждение, вынесенное по отменённому решению, отзывается (<see cref="RevokedAt"/>).
/// </summary>
public class Warning : BaseEntity
{
    public const int ValidMonths = 12;

    /// <summary>Сколько действующих предупреждений создают запрос на бан.</summary>
    public const int BanThreshold = 3;

    private Warning()
    {
    }

    public Guid UserId { get; private set; }
    public Guid CaseId { get; private set; }
    public Guid IssuedById { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public DateTime ExpiresAt { get; private set; }
    public DateTime? RevokedAt { get; private set; }

    public bool IsActiveAt(DateTime now) => RevokedAt is null && ExpiresAt > now;

    public static Warning Issue(Guid userId, Guid caseId, Guid issuedById, string? reason, DateTime now) => new()
    {
        UserId = userId,
        CaseId = caseId,
        IssuedById = issuedById,
        Reason = Case.CleanText(reason, CaseRules.ReasonMinLength, CaseRules.ReasonMaxLength, "warning_reason"),
        ExpiresAt = now.AddMonths(ValidMonths),
    };

    public bool Revoke(DateTime now)
    {
        if (RevokedAt is not null)
        {
            return false;
        }

        RevokedAt = now;
        return true;
    }
}

/// <summary>Запрос на блокировку пользователя админу (таблица BanRequests): от модератора с обоснованием или автоматически после трёх предупреждений.</summary>
public class BanRequest : BaseEntity
{
    private BanRequest()
    {
    }

    public Guid UserId { get; private set; }
    public Guid? CaseId { get; private set; }

    /// <summary>Кто запросил; null — запрос создала система (три действующих предупреждения).</summary>
    public Guid? RequestedById { get; private set; }

    public string Reason { get; private set; } = string.Empty;
    public BanRequestStatus Status { get; private set; } = BanRequestStatus.Pending;
    public Guid? DecidedById { get; private set; }
    public DateTime? DecidedAt { get; private set; }

    public static BanRequest ByModerator(Guid userId, Guid caseId, Guid moderatorId, string? reason) => new()
    {
        UserId = userId,
        CaseId = caseId,
        RequestedById = moderatorId,
        Reason = Case.CleanText(reason, CaseRules.ReasonMinLength, CaseRules.ReasonMaxLength, "ban_reason"),
    };

    public static BanRequest Automatic(Guid userId, Guid? caseId, string reason) => new() { UserId = userId, CaseId = caseId, Reason = reason };

    public void Approve(Guid adminId, DateTime now) => Decide(BanRequestStatus.Approved, adminId, now);

    public void Reject(Guid adminId, DateTime now) => Decide(BanRequestStatus.Rejected, adminId, now);

    private void Decide(BanRequestStatus to, Guid adminId, DateTime now)
    {
        if (Status != BanRequestStatus.Pending)
        {
            throw new InvalidStateTransitionException(nameof(BanRequest), Status.ToString(), to.ToString());
        }

        Status = to;
        DecidedById = adminId;
        DecidedAt = now;
    }
}

/// <summary>
/// Запись журнала аудита (таблица AuditLogs, раздел 10.2 ТЗ): только добавление. База запрещает изменение и удаление строк (триггер), в частности
/// каждый запрос модератора к истории чата остаётся в журнале навсегда.
/// </summary>
public class AuditLog
{
    public const int ActionMaxLength = 64;
    public const int TargetTypeMaxLength = 64;
    public const int IpMaxLength = 64;

    private AuditLog()
    {
    }

    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid? ActorId { get; private set; }
    public string Action { get; private set; } = string.Empty;
    public string TargetType { get; private set; } = string.Empty;
    public Guid? TargetId { get; private set; }
    public string DetailsJson { get; private set; } = "{}";
    public string? IpAddress { get; private set; }
    public DateTime CreatedAt { get; private set; }

    public static AuditLog Create(Guid? actorId, string action, string targetType, Guid? targetId, string? detailsJson, string? ipAddress, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(action) || action.Length > ActionMaxLength)
        {
            throw new DomainValidationException("audit_action_invalid", "Действие в журнале обязательно и не длиннее 64 символов.");
        }

        if (string.IsNullOrWhiteSpace(targetType) || targetType.Length > TargetTypeMaxLength)
        {
            throw new DomainValidationException("audit_target_invalid", "Тип объекта в журнале обязателен и не длиннее 64 символов.");
        }

        return new AuditLog
        {
            ActorId = actorId,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            DetailsJson = string.IsNullOrWhiteSpace(detailsJson) ? "{}" : detailsJson,
            IpAddress = ipAddress is { Length: > IpMaxLength } ? ipAddress[..IpMaxLength] : ipAddress,
            CreatedAt = now,
        };
    }
}
