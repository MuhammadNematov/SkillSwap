using SkillSwap.Application.Exceptions;
using SkillSwap.Application.Exchanges;
using SkillSwap.Application.Notifications;
using SkillSwap.Application.Reviews;
using SkillSwap.Application.Sessions;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Moderation;

/// <summary>Что осталось сделать после коммита решения: обновить карточки отменённых уроков и поставить приглашения на отзыв, если обмен закрыт.</summary>
public sealed record ConsequenceResult(IReadOnlyList<LessonSessionDto> CancelledLessons, DateTime? ExchangeClosedAt, bool Warned)
{
    public static ConsequenceResult None { get; } = new([], null, false);
}

/// <summary>
/// Последствия решений по обращению и их откат (раздел 8.3 ТЗ). Общий код решения модератора и решения админа по апелляции. Все методы вызываются внутри
/// транзакции под блокировкой участников, сохраняет вызывающий.
/// </summary>
public sealed class CaseConsequences
{
    private readonly ICaseStore _cases;
    private readonly IReviewStore _reviews;
    private readonly ReviewService _reviewService;
    private readonly IExchangeStore _exchanges;
    private readonly LessonSessionService _lessons;
    private readonly IAuditLog _audit;
    private readonly INotificationPublisher _notify;

    public CaseConsequences(
        ICaseStore cases, IReviewStore reviews, ReviewService reviewService, IExchangeStore exchanges, LessonSessionService lessons, IAuditLog audit, INotificationPublisher notify)
    {
        _cases = cases;
        _reviews = reviews;
        _reviewService = reviewService;
        _exchanges = exchanges;
        _lessons = lessons;
        _audit = audit;
        _notify = notify;
    }

    // ---------- применить ----------

    /// <summary>Применяет решение (обращение уже переведено в новое состояние, <c>Decision</c> и <c>DecisionReason</c> заполнены). <paramref name="actorId"/> — модератор или админ.</summary>
    public async Task<ConsequenceResult> ApplyAsync(Case found, Guid actorId, DateTime now, CancellationToken ct)
    {
        var decision = found.Decision!.Value;
        var warn = decision == CaseDecision.Warning || found.AlsoWarned;
        IReadOnlyList<LessonSessionDto> cancelled = [];
        DateTime? closedAt = null;
        switch (decision)
        {
            case CaseDecision.ReviewKept:
            case CaseDecision.ReviewHidden:
                await ApplyReviewDecisionAsync(found, decision, actorId, ct);
                break;
            case CaseDecision.ExchangeEnded:
                (cancelled, closedAt) = await EndExchangeAsync(found, actorId, now, ct);
                break;
            case CaseDecision.FrozenBanRequested:
                cancelled = await FreezeAndRequestBanAsync(found, actorId, now, ct);
                break;
            default:
                await UnfreezeIfNothingHoldsAsync(found, actorId, now, ct);
                break;
        }

        if (warn)
        {
            await IssueWarningAsync(found, actorId, now, ct);
        }

        return new ConsequenceResult(cancelled, closedAt, warn);
    }

    // ---------- откатить ----------

    /// <summary>
    /// Откатывает то, что можно вернуть: скрытый отзыв, вынесенное предупреждение (и созданный им автоматический запрос на бан, если действующих стало меньше трёх),
    /// ещё не решённый запрос на бан и заморозку обмена. Принудительное завершение обмена и уже одобренную блокировку откатить нельзя.
    /// </summary>
    /// <returns>Было ли отозвано предупреждение.</returns>
    public async Task<bool> ReverseAsync(Case found, CaseDecision oldDecision, bool oldWarned, Guid actorId, DateTime now, CancellationToken ct)
    {
        if (oldDecision == CaseDecision.ReviewHidden && found.ReviewId is { } reviewId && await _reviews.FindAsync(reviewId, ct) is { Status: ReviewStatus.Hidden } review)
        {
            review.Restore();
            _audit.Add(new AuditEntry(actorId, AuditActions.ReviewRestored, "Review", review.Id, new { caseId = found.Id }));
            await _cases.SaveAsync(ct);
            await _reviewService.RecalculateRatingAsync(review.TargetId, ct);
        }

        if (oldDecision == CaseDecision.FrozenBanRequested)
        {
            foreach (var ban in (await _cases.FindBanRequestsForCaseAsync(found.Id, ct)).Where(b => b.Status == BanRequestStatus.Pending && b.RequestedById is not null))
            {
                ban.Reject(actorId, now);
                _audit.Add(new AuditEntry(actorId, AuditActions.BanDecided, "User", ban.UserId, new { banRequestId = ban.Id, approved = false, reason = "decision_overturned" }));
            }

            var exchange = await _exchanges.FindAsync(found.ExchangeId, ct);
            if (exchange is { Status: ExchangeStatus.Frozen } && !await _cases.HasOtherActiveComplaintAsync(found.ExchangeId, found.Id, ct))
            {
                UnfreezeCore(exchange, found.Id, actorId, now);
            }
        }

        var revoked = false;
        if (oldDecision == CaseDecision.Warning || oldWarned)
        {
            foreach (var warning in await _cases.FindWarningsForCaseAsync(found.Id, ct))
            {
                if (warning.Revoke(now))
                {
                    revoked = true;
                    _audit.Add(new AuditEntry(actorId, AuditActions.WarningRevoked, "User", warning.UserId, new { caseId = found.Id }));
                    _notify.Add(warning.UserId, NotificationType.WarningRevoked, new { caseId = found.Id });
                }
            }

            await _cases.SaveAsync(ct);
            await RejectAutomaticBanIfNoLongerDueAsync(found, actorId, now, ct);
        }

        return revoked;
    }

    // ---------- части ----------

    private async Task ApplyReviewDecisionAsync(Case found, CaseDecision decision, Guid actorId, CancellationToken ct)
    {
        var review = found.ReviewId is { } id ? await _reviews.FindAsync(id, ct) : null;
        if (review is null)
        {
            return;
        }

        if (decision == CaseDecision.ReviewKept && review.Status == ReviewStatus.Frozen)
        {
            review.Unfreeze();
        }
        else if (decision == CaseDecision.ReviewHidden && review.Status is ReviewStatus.Published or ReviewStatus.Frozen)
        {
            review.Hide();
            _audit.Add(new AuditEntry(actorId, AuditActions.ReviewHidden, "Review", review.Id, new { caseId = found.Id }));
        }

        await _cases.SaveAsync(ct);
        await _reviewService.RecalculateRatingAsync(review.TargetId, ct);
    }

    private async Task<(IReadOnlyList<LessonSessionDto>, DateTime?)> EndExchangeAsync(Case found, Guid actorId, DateTime now, CancellationToken ct)
    {
        var exchange = await _exchanges.FindAsync(found.ExchangeId, ct) ?? throw new NotFoundException("exchange_not_found", "Обмен не найден.");
        if (exchange.Status is not (ExchangeStatus.Active or ExchangeStatus.Frozen))
        {
            throw new ConflictException("exchange_not_open", "Обмен уже закрыт: завершать нечего.");
        }

        exchange.AbortByModerator("moderation", now);
        var cancelled = await _lessons.CancelOpenAsync(exchange.Id, exchange.UserAId, exchange.UserBId, ct);
        _audit.Add(new AuditEntry(actorId, AuditActions.ExchangeForceEnded, "Exchange", exchange.Id, new { caseId = found.Id }));
        foreach (var user in new[] { exchange.UserAId, exchange.UserBId })
        {
            _notify.Add(user, NotificationType.ExchangeAborted, new { exchangeId = exchange.Id, reason = "moderation" });
        }

        return (cancelled, now);
    }

    private async Task<IReadOnlyList<LessonSessionDto>> FreezeAndRequestBanAsync(Case found, Guid actorId, DateTime now, CancellationToken ct)
    {
        IReadOnlyList<LessonSessionDto> cancelled = [];
        var exchange = await _exchanges.FindAsync(found.ExchangeId, ct);
        if (exchange is { Status: ExchangeStatus.Active })
        {
            cancelled = await FreezeCoreAsync(exchange, found.Id, actorId, now, ct);
        }

        _cases.AddBanRequest(BanRequest.ByModerator(found.AgainstUserId, found.Id, actorId, found.DecisionReason));
        _audit.Add(new AuditEntry(actorId, AuditActions.BanRequested, "User", found.AgainstUserId, new { caseId = found.Id }));
        return cancelled;
    }

    /// <summary>Решения без собственных последствий для обмена: если он заморожен жалобой, а других жалоб по нему нет, размораживаем. Спор по отзыву обмен не трогает.</summary>
    private async Task UnfreezeIfNothingHoldsAsync(Case found, Guid actorId, DateTime now, CancellationToken ct)
    {
        if (found.Type == CaseType.ReviewDispute)
        {
            return;
        }

        var exchange = await _exchanges.FindAsync(found.ExchangeId, ct);
        if (exchange is { Status: ExchangeStatus.Frozen } && !await _cases.HasOtherActiveComplaintAsync(found.ExchangeId, found.Id, ct))
        {
            UnfreezeCore(exchange, found.Id, actorId, now);
        }
    }

    private async Task IssueWarningAsync(Case found, Guid actorId, DateTime now, CancellationToken ct)
    {
        var culprit = found.AgainstUserId;
        _cases.AddWarning(Warning.Issue(culprit, found.Id, actorId, found.DecisionReason, now));
        _audit.Add(new AuditEntry(actorId, AuditActions.WarningIssued, "User", culprit, new { caseId = found.Id }));
        _notify.Add(culprit, NotificationType.WarningIssued, new { caseId = found.Id, exchangeId = found.ExchangeId, reason = found.DecisionReason });
        await _cases.SaveAsync(ct);

        if (await _cases.CountActiveWarningsAsync(culprit, now, ct) >= Warning.BanThreshold && !await _cases.HasPendingBanRequestAsync(culprit, ct))
        {
            _cases.AddBanRequest(BanRequest.Automatic(culprit, found.Id, $"Действующих предупреждений за {Warning.ValidMonths} месяцев: не меньше {Warning.BanThreshold}."));
            _audit.Add(new AuditEntry(null, AuditActions.BanRequested, "User", culprit, new { caseId = found.Id, automatic = true }));
        }
    }

    /// <summary>Автоматический запрос на бан по трём предупреждениям отклоняется, если после отзыва предупреждения их стало меньше трёх.</summary>
    private async Task RejectAutomaticBanIfNoLongerDueAsync(Case found, Guid actorId, DateTime now, CancellationToken ct)
    {
        var culprit = found.AgainstUserId;
        if (await _cases.CountActiveWarningsAsync(culprit, now, ct) >= Warning.BanThreshold)
        {
            return;
        }

        foreach (var ban in (await _cases.FindBanRequestsForCaseAsync(found.Id, ct)).Where(b => b.Status == BanRequestStatus.Pending && b.RequestedById is null && b.UserId == culprit))
        {
            ban.Reject(actorId, now);
            _audit.Add(new AuditEntry(actorId, AuditActions.BanDecided, "User", ban.UserId, new { banRequestId = ban.Id, approved = false, reason = "warning_revoked" }));
        }
    }

    public async Task<IReadOnlyList<LessonSessionDto>> FreezeCoreAsync(Exchange exchange, Guid caseId, Guid actorId, DateTime now, CancellationToken ct)
    {
        exchange.Freeze(now);
        var cancelled = await _lessons.CancelOpenAsync(exchange.Id, exchange.UserAId, exchange.UserBId, ct);
        _audit.Add(new AuditEntry(actorId, AuditActions.ExchangeFrozen, "Exchange", exchange.Id, new { caseId }));
        foreach (var user in new[] { exchange.UserAId, exchange.UserBId })
        {
            _notify.Add(user, NotificationType.ExchangeFrozen, new { exchangeId = exchange.Id });
        }

        return cancelled;
    }

    public void UnfreezeCore(Exchange exchange, Guid caseId, Guid actorId, DateTime now)
    {
        exchange.Unfreeze(now);
        _audit.Add(new AuditEntry(actorId, AuditActions.ExchangeUnfrozen, "Exchange", exchange.Id, new { caseId }));
        foreach (var user in new[] { exchange.UserAId, exchange.UserBId })
        {
            _notify.Add(user, NotificationType.ExchangeUnfrozen, new { exchangeId = exchange.Id });
        }
    }
}
