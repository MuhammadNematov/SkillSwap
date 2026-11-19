using SkillSwap.Domain.Common;
using SkillSwap.Domain.Enums;
using SkillSwap.Domain.Exceptions;

namespace SkillSwap.Domain.Entities;

/// <summary>Правила обращений, не привязанные к одному обращению (разделы 8.2 и 8.3 ТЗ).</summary>
public static class CaseRules
{
    /// <summary>Спор по отзыву можно подать в течение 14 дней после публикации.</summary>
    public static readonly TimeSpan ReviewDisputeWindow = TimeSpan.FromDays(14);

    /// <summary>Жалобу на неявки и срыв — в течение 30 дней после обмена.</summary>
    public static readonly TimeSpan NoShowAbuseWindow = TimeSpan.FromDays(30);

    /// <summary>Жалобу на поведение — пока есть чат: до 90 дней после обмена (раздел 10.3 ТЗ).</summary>
    public static readonly TimeSpan ConductWindow = TimeSpan.FromDays(90);

    /// <summary>Срок реакции на обращение высокого приоритета.</summary>
    public static readonly TimeSpan HighPrioritySla = TimeSpan.FromHours(24);

    /// <summary>Срок реакции на обращение обычного приоритета.</summary>
    public static readonly TimeSpan NormalPrioritySla = TimeSpan.FromHours(72);

    /// <summary>На ответ по запросу модератора даётся 48 часов.</summary>
    public static readonly TimeSpan ExplanationWindow = TimeSpan.FromHours(48);

    /// <summary>Апелляция — один раз, в течение 7 дней после решения; без неё обращение закрывается через те же 7 дней.</summary>
    public static readonly TimeSpan AppealWindow = TimeSpan.FromDays(7);

    public const int DescriptionMinLength = 20;
    public const int DescriptionMaxLength = 2000;
    public const int ReasonMinLength = 20;
    public const int ReasonMaxLength = 2000;
    public const int MaxEvidenceMessages = 20;

    /// <summary>Категории, допустимые для каждого типа обращения.</summary>
    public static IReadOnlyList<CaseCategory> CategoriesFor(CaseType type) => type switch
    {
        CaseType.ReviewDispute => [CaseCategory.ReviewUnfair, CaseCategory.ReviewAbusive, CaseCategory.ReviewFalse],
        CaseType.Conduct => [CaseCategory.Insults, CaseCategory.Threats, CaseCategory.Harassment, CaseCategory.Spam, CaseCategory.Extortion],
        CaseType.NoShowAbuse => [CaseCategory.SystematicEvasion, CaseCategory.BadFaithLessons],
        _ => [],
    };

    /// <summary>Решения, которые можно вынести по обращению этого типа.</summary>
    public static IReadOnlyList<CaseDecision> DecisionsFor(CaseType type) => type switch
    {
        CaseType.ReviewDispute => [CaseDecision.ReviewKept, CaseDecision.ReviewHidden],
        CaseType.Conduct or CaseType.NoShowAbuse =>
            [CaseDecision.Warning, CaseDecision.ExchangeEnded, CaseDecision.FrozenBanRequested, CaseDecision.NoViolation],
        _ => [],
    };

    /// <summary>С какими решениями можно сочетать предупреждение (флаг <c>AlsoWarned</c>).</summary>
    public static bool CanBeCombinedWithWarning(CaseDecision decision) =>
        decision is CaseDecision.ReviewHidden or CaseDecision.ExchangeEnded or CaseDecision.FrozenBanRequested;

    public static TimeSpan SlaFor(CasePriority priority) => priority == CasePriority.High ? HighPrioritySla : NormalPrioritySla;

    /// <summary>
    /// Не истёк ли срок подачи. <paramref name="anchor"/>: для спора — когда опубликован отзыв, для остальных — когда закрыт обмен;
    /// null — обмен ещё идёт (подать можно всегда).
    /// </summary>
    public static bool IsWithinFilingWindow(CaseType type, DateTime? anchor, DateTime now)
    {
        if (anchor is null)
        {
            return true;
        }

        var window = type switch
        {
            CaseType.ReviewDispute => ReviewDisputeWindow,
            CaseType.NoShowAbuse => NoShowAbuseWindow,
            _ => ConductWindow,
        };
        return now <= anchor.Value + window;
    }
}

/// <summary>
/// Обращение к модератору (таблица Cases, разделы 8.2 и 9.4 ТЗ). Все переходы статуса — только методами этого класса.
/// Участников обращения двое: заявитель (<see cref="OpenedById"/>) и тот, на кого подана жалоба (<see cref="AgainstUserId"/>).
/// </summary>
public class Case : BaseEntity
{
    private Case()
    {
    }

    public CaseType Type { get; private set; }
    public CaseCategory Category { get; private set; }
    public Guid ExchangeId { get; private set; }

    /// <summary>Оспариваемый отзыв (только у спора по отзыву).</summary>
    public Guid? ReviewId { get; private set; }

    public Guid OpenedById { get; private set; }

    /// <summary>Против кого обращение: для спора — автор отзыва, для жалоб — партнёр заявителя.</summary>
    public Guid AgainstUserId { get; private set; }

    /// <summary>Модератор, взявший обращение (решение по нему выносит он же).</summary>
    public Guid? ModeratorId { get; private set; }

    public CasePriority Priority { get; private set; }
    public CaseStatus Status { get; private set; } = CaseStatus.Open;
    public string Description { get; private set; } = string.Empty;

    /// <summary>Жалоба подана из видеозвонка: обмен замораживается сразу (раздел 10.4 ТЗ).</summary>
    public bool FromCall { get; private set; }

    /// <summary>Срок реакции: подачa + 24 или 72 часа (по приоритету). Просроченные подсвечиваются в очереди.</summary>
    public DateTime DueAt { get; private set; }

    /// <summary>До какого времени стороны должны ответить на вопрос модератора (WaitingResponse, 48 часов).</summary>
    public DateTime? ResponseDeadlineAt { get; private set; }

    public CaseDecision? Decision { get; private set; }

    /// <summary>К решению добавлено предупреждение нарушителю.</summary>
    public bool AlsoWarned { get; private set; }

    public string? DecisionReason { get; private set; }
    public DateTime? ResolvedAt { get; private set; }
    public DateTime? ClosedAt { get; private set; }

    /// <summary>Есть ли у обращения апелляция (она бывает одна).</summary>
    public bool WasAppealed { get; private set; }

    /// <summary>Участник обращения: заявитель или тот, на кого оно подано.</summary>
    public bool IsParty(Guid userId) => userId == OpenedById || userId == AgainstUserId;

    public Guid PartnerOf(Guid userId) => userId == OpenedById ? AgainstUserId : OpenedById;

    /// <summary>Просрочен ли срок реакции (обращение ещё не взято в работу и срок прошёл).</summary>
    public bool IsOverdue(DateTime now) => Status == CaseStatus.Open && now > DueAt;

    /// <summary>Открыто ли обращение: ещё не решено окончательно (для отложенного удаления аккаунта и очистки чата).</summary>
    public bool IsActive => Status is not (CaseStatus.Withdrawn or CaseStatus.Closed);

    public static Case Open(
        CaseType type, CaseCategory category, Guid exchangeId, Guid? reviewId, Guid openedById, Guid againstUserId,
        string? description, bool minorInvolved, bool fromCall, DateTime now)
    {
        if (openedById == againstUserId)
        {
            throw new DomainValidationException("case_against_self", "Обращение на самого себя невозможно.");
        }

        if (type == CaseType.ReviewDispute && reviewId is null)
        {
            throw new DomainValidationException("case_review_required", "Спор по отзыву подаётся на конкретный отзыв.");
        }

        if (type != CaseType.ReviewDispute && reviewId is not null)
        {
            throw new DomainValidationException("case_review_not_allowed", "Отзыв указывается только в споре по отзыву.");
        }

        if (!CaseRules.CategoriesFor(type).Contains(category))
        {
            throw new DomainValidationException("case_category_mismatch", "Категория не подходит к типу обращения.");
        }

        if (fromCall && type != CaseType.Conduct)
        {
            throw new DomainValidationException("case_from_call_conduct_only", "Из звонка можно подать только жалобу на поведение.");
        }

        var text = CleanText(description, CaseRules.DescriptionMinLength, CaseRules.DescriptionMaxLength, "case_description");
        var priority = type == CaseType.Conduct || minorInvolved || fromCall ? CasePriority.High : CasePriority.Normal;
        return new Case
        {
            Type = type,
            Category = category,
            ExchangeId = exchangeId,
            ReviewId = reviewId,
            OpenedById = openedById,
            AgainstUserId = againstUserId,
            Priority = priority,
            Description = text,
            FromCall = fromCall,
            DueAt = now + CaseRules.SlaFor(priority),
            Status = CaseStatus.Open,
        };
    }

    /// <summary>Заявитель отозвал обращение до взятия в работу (Open → Withdrawn).</summary>
    public void Withdraw(Guid userId)
    {
        RequireStatus(CaseStatus.Withdrawn, CaseStatus.Open);
        if (userId != OpenedById)
        {
            throw new DomainValidationException("not_case_opener", "Отозвать обращение может только заявитель.");
        }

        Status = CaseStatus.Withdrawn;
    }

    /// <summary>Модератор взял в работу (Open → InProgress). Конфликт интересов проверяется до вызова: модератор не может быть участником.</summary>
    public void Take(Guid moderatorId)
    {
        RequireStatus(CaseStatus.InProgress, CaseStatus.Open);
        if (IsParty(moderatorId))
        {
            throw new DomainValidationException("conflict_of_interest", "Модератор не может рассматривать обращение, где он участник.");
        }

        ModeratorId = moderatorId;
        Status = CaseStatus.InProgress;
    }

    /// <summary>Модератор запросил пояснения (InProgress → WaitingResponse): на ответ 48 часов.</summary>
    public void RequestExplanation(Guid moderatorId, DateTime now)
    {
        RequireStatus(CaseStatus.WaitingResponse, CaseStatus.InProgress);
        RequireModerator(moderatorId);
        Status = CaseStatus.WaitingResponse;
        ResponseDeadlineAt = now + CaseRules.ExplanationWindow;
    }

    /// <summary>Стороны ответили или истекли 48 часов (WaitingResponse → InProgress). Повторный вызов безвреден.</summary>
    /// <returns>true, если обращение вернулось в работу этим вызовом.</returns>
    public bool ResumeAfterExplanation()
    {
        if (Status != CaseStatus.WaitingResponse)
        {
            return false;
        }

        Status = CaseStatus.InProgress;
        ResponseDeadlineAt = null;
        return true;
    }

    /// <summary>Модератор вынес решение (InProgress → Resolved) с обязательным обоснованием.</summary>
    public void Resolve(Guid moderatorId, CaseDecision decision, bool alsoWarned, string? reason, DateTime now)
    {
        RequireStatus(CaseStatus.Resolved, CaseStatus.InProgress);
        RequireModerator(moderatorId);
        SetDecision(decision, alsoWarned, reason);
        ResolvedAt = now;
        Status = CaseStatus.Resolved;
    }

    /// <summary>Сторона подала апелляцию (Resolved → Appealed): один раз, в течение 7 дней после решения.</summary>
    public void Appeal(Guid userId, DateTime now)
    {
        RequireStatus(CaseStatus.Appealed, CaseStatus.Resolved);
        if (!IsParty(userId))
        {
            throw new DomainValidationException("not_case_party", "Апелляцию подаёт участник обращения.");
        }

        if (now > ResolvedAt!.Value + CaseRules.AppealWindow)
        {
            throw new DomainValidationException("appeal_window_closed", "Срок апелляции (7 дней) истёк.");
        }

        WasAppealed = true;
        Status = CaseStatus.Appealed;
    }

    /// <summary>Админ назначил другого модератора на повторную проверку (Appealed → AppealReview). Это не тот, кто вынес решение.</summary>
    public void BeginAppealReview(Guid reviewerModeratorId)
    {
        RequireStatus(CaseStatus.AppealReview, CaseStatus.Appealed);
        if (reviewerModeratorId == ModeratorId)
        {
            throw new DomainValidationException("appeal_reviewer_must_differ", "Повторную проверку проводит другой модератор.");
        }

        if (IsParty(reviewerModeratorId))
        {
            throw new DomainValidationException("conflict_of_interest", "Модератор не может рассматривать обращение, где он участник.");
        }

        Status = CaseStatus.AppealReview;
    }

    /// <summary>Второй модератор написал заключение (AppealReview → Appealed).</summary>
    public void EndAppealReview()
    {
        RequireStatus(CaseStatus.Appealed, CaseStatus.AppealReview);
        Status = CaseStatus.Appealed;
    }

    /// <summary>
    /// Админ вынес окончательное решение (Appealed → Closed). <paramref name="newDecision"/> — новое решение, если админ меняет прежнее; null — решение остаётся
    /// (<paramref name="reason"/> тогда обязательно для отмены или замены, для «оставить» необязательно).
    /// </summary>
    public void CloseByAdmin(CaseDecision? newDecision, bool alsoWarned, string? reason, DateTime now)
    {
        RequireStatus(CaseStatus.Closed, CaseStatus.Appealed);
        if (newDecision is { } decision)
        {
            SetDecision(decision, alsoWarned, reason);
        }

        ClosedAt = now;
        Status = CaseStatus.Closed;
    }

    /// <summary>Прошло 7 дней без апелляции (Resolved → Closed). До срока и повторно ничего не делает.</summary>
    /// <returns>true, если обращение закрыто этим вызовом.</returns>
    public bool CloseIfDue(DateTime now)
    {
        if (Status != CaseStatus.Resolved || now < ResolvedAt!.Value + CaseRules.AppealWindow)
        {
            return false;
        }

        ClosedAt = now;
        Status = CaseStatus.Closed;
        return true;
    }

    private void SetDecision(CaseDecision decision, bool alsoWarned, string? reason)
    {
        if (!CaseRules.DecisionsFor(Type).Contains(decision))
        {
            throw new DomainValidationException("case_decision_not_allowed", "Такое решение не подходит к этому типу обращения.");
        }

        if (alsoWarned && !CaseRules.CanBeCombinedWithWarning(decision))
        {
            throw new DomainValidationException("case_warning_not_combinable", "К этому решению предупреждение не добавляется.");
        }

        DecisionReason = CleanText(reason, CaseRules.ReasonMinLength, CaseRules.ReasonMaxLength, "case_reason");
        Decision = decision;
        AlsoWarned = alsoWarned;
    }

    private void RequireModerator(Guid moderatorId)
    {
        if (moderatorId != ModeratorId)
        {
            throw new DomainValidationException("not_case_moderator", "Обращение ведёт другой модератор.");
        }
    }

    private void RequireStatus(CaseStatus to, params CaseStatus[] allowedFrom)
    {
        if (!allowedFrom.Contains(Status))
        {
            throw new InvalidStateTransitionException(nameof(Case), Status.ToString(), to.ToString());
        }
    }

    internal static string CleanText(string? text, int min, int max, string codePrefix)
    {
        var clean = text?.Trim() ?? string.Empty;
        if (clean.Length < min)
        {
            throw new DomainValidationException($"{codePrefix}_too_short", $"Нужно не меньше {min} символов.");
        }

        if (clean.Length > max)
        {
            throw new DomainValidationException($"{codePrefix}_too_long", $"Не больше {max} символов.");
        }

        return clean;
    }
}
