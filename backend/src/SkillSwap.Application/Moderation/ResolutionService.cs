using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Exceptions;
using SkillSwap.Application.Exchanges;
using SkillSwap.Application.Notifications;
using SkillSwap.Application.Profile;
using SkillSwap.Application.Reviews;
using SkillSwap.Application.Sessions;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Moderation;

/// <summary>
/// Решения по обращениям (раздел 8.3 ТЗ): оставить или скрыть отзыв, предупреждение, принудительное завершение обмена, заморозка и запрос на бан, «нарушений нет»,
/// а также ручная заморозка и разморозка обмена. Всё в одной транзакции под блокировкой модератора и обоих участников: решение, его последствия, отзыв доступа
/// к чату, аудит и уведомления либо есть целиком, либо нет. Участникам уходит уведомление с письмом (tg, ru, en), а через 7 дней без апелляции обращение закрывается.
/// </summary>
public sealed class ResolutionService
{
    private readonly ICaseStore _cases;
    private readonly IReviewStore _reviews;
    private readonly ReviewService _reviewService;
    private readonly IExchangeStore _exchanges;
    private readonly LessonSessionService _lessons;
    private readonly ReviewScheduler _reviewScheduler;
    private readonly ModerationService _moderation;
    private readonly IProfileStore _profiles;
    private readonly IUnitOfWork _uow;
    private readonly IAuditLog _audit;
    private readonly INotificationPublisher _notify;
    private readonly IBackgroundJobs _jobs;
    private readonly TimeProvider _clock;

    public ResolutionService(
        ICaseStore cases, IReviewStore reviews, ReviewService reviewService, IExchangeStore exchanges, LessonSessionService lessons, ReviewScheduler reviewScheduler,
        ModerationService moderation, IProfileStore profiles, IUnitOfWork uow, IAuditLog audit, INotificationPublisher notify, IBackgroundJobs jobs, TimeProvider clock)
    {
        _cases = cases;
        _reviews = reviews;
        _reviewService = reviewService;
        _exchanges = exchanges;
        _lessons = lessons;
        _reviewScheduler = reviewScheduler;
        _moderation = moderation;
        _profiles = profiles;
        _uow = uow;
        _audit = audit;
        _notify = notify;
        _jobs = jobs;
        _clock = clock;
    }

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;

    // ---------- решение ----------

    /// <summary>
    /// Вынести решение (InProgress → Resolved). Только модератор, взявший обращение; пока ждём пояснений, решение выносить нельзя (409 <c>waiting_for_explanations</c>).
    /// Последствия по решению, отзыв доступа к чату, уведомления и письма участникам, запись в аудит — в одной транзакции.
    /// </summary>
    public async Task<ModerationCaseDto> ResolveAsync(Guid moderatorId, Guid caseId, ResolveCaseRequest request, CancellationToken ct)
    {
        await RequireModeratorAsync(moderatorId, ct);
        var peek = await RequireOwnedAsync(moderatorId, caseId, ct);

        IReadOnlyList<LessonSessionDto> cancelled = [];
        DateTime? exchangeClosedAt = null;
        DateTime resolvedAt = default;
        await _uow.ExecuteAsync(async token =>
        {
            await _uow.LockUsersAsync([moderatorId, peek.OpenedById, peek.AgainstUserId], token);
            var found = await _cases.FindAsync(caseId, tracked: true, token) ?? throw new NotFoundException("case_not_found", "Обращение не найдено.");
            if (found.Status == CaseStatus.WaitingResponse)
            {
                throw new ConflictException("waiting_for_explanations", "Обращение ждёт пояснений: дождитесь ответов или истечения срока.");
            }

            var now = Now();
            resolvedAt = now;
            found.Resolve(moderatorId, request.Decision, request.AlsoWarned, request.Reason, now);   // решение не подходит типу, предупреждение не сочетается, короткое обоснование: 400

            var warn = request.Decision == CaseDecision.Warning || request.AlsoWarned;
            switch (request.Decision)
            {
                case CaseDecision.ReviewKept:
                case CaseDecision.ReviewHidden:
                    await ApplyReviewDecisionAsync(found, request.Decision, token);
                    break;
                case CaseDecision.ExchangeEnded:
                    (cancelled, exchangeClosedAt) = await EndExchangeAsync(found, moderatorId, now, token);
                    break;
                case CaseDecision.FrozenBanRequested:
                    cancelled = await FreezeAndRequestBanAsync(found, moderatorId, now, token);
                    break;
                default:
                    cancelled = await UnfreezeIfNothingHoldsAsync(found, moderatorId, now, token);
                    break;
            }

            if (warn)
            {
                await IssueWarningAsync(found, moderatorId, now, token);
            }

            await _moderation.RevokeChatAccessAsync(caseId, moderatorId, token);
            _audit.Add(new AuditEntry(moderatorId, AuditActions.CaseResolved, "Case", caseId, new { decision = request.Decision, alsoWarned = warn }));
            var appealUntil = now + CaseRules.AppealWindow;
            foreach (var (user, role) in new[] { (found.OpenedById, CaseRole.Opener), (found.AgainstUserId, CaseRole.Against) })
            {
                _notify.Add(user, NotificationType.CaseResolved, new
                {
                    caseId,
                    exchangeId = found.ExchangeId,
                    role = role.ToString(),
                    decision = request.Decision.ToString(),
                    alsoWarned = warn,
                    youWereWarned = warn && role == CaseRole.Against,
                    appealUntil,
                });
            }

            await _cases.SaveAsync(token);
            return 0;
        }, ct);

        _notify.Dispatch();
        _jobs.Schedule<CaseJobs>(j => j.CloseIfDueAsync(caseId), new DateTimeOffset(resolvedAt + CaseRules.AppealWindow, TimeSpan.Zero));
        await _lessons.PublishUpdatesAsync(cancelled);
        if (exchangeClosedAt is { } closed)
        {
            _reviewScheduler.OnExchangeClosed(peek.ExchangeId, closed);
        }

        return await _moderation.GetAsync(moderatorId, caseId, ct);
    }

    // ---------- ручная заморозка и разморозка ----------

    /// <summary>Заморозить обмен по жалобе, пока она рассматривается: чат только для чтения, открытые уроки отменяются. Только жалобы (не спор по отзыву).</summary>
    public async Task<ExchangeFreezeDto> FreezeExchangeAsync(Guid moderatorId, Guid caseId, CancellationToken ct)
    {
        await RequireModeratorAsync(moderatorId, ct);
        var peek = await RequireOwnedForExchangeAsync(moderatorId, caseId, ct);

        IReadOnlyList<LessonSessionDto> cancelled = [];
        await _uow.ExecuteAsync(async token =>
        {
            await _uow.LockUsersAsync([moderatorId, peek.OpenedById, peek.AgainstUserId], token);
            var exchange = await _exchanges.FindAsync(peek.ExchangeId, token) ?? throw new NotFoundException("exchange_not_found", "Обмен не найден.");
            if (exchange.Status != ExchangeStatus.Active)
            {
                throw new ConflictException("exchange_not_active", "Заморозить можно только идущий обмен.");
            }

            cancelled = await FreezeCoreAsync(exchange, caseId, moderatorId, Now(), token);
            await _cases.SaveAsync(token);
            return 0;
        }, ct);

        _notify.Dispatch();
        await _lessons.PublishUpdatesAsync(cancelled);
        return new ExchangeFreezeDto(peek.ExchangeId, ExchangeStatus.Frozen);
    }

    /// <summary>Разморозить обмен (срок обмена сдвигается на время заморозки). Только жалобы, пока обращение в работе.</summary>
    public async Task<ExchangeFreezeDto> UnfreezeExchangeAsync(Guid moderatorId, Guid caseId, CancellationToken ct)
    {
        await RequireModeratorAsync(moderatorId, ct);
        var peek = await RequireOwnedForExchangeAsync(moderatorId, caseId, ct);

        await _uow.ExecuteAsync(async token =>
        {
            await _uow.LockUsersAsync([moderatorId, peek.OpenedById, peek.AgainstUserId], token);
            var exchange = await _exchanges.FindAsync(peek.ExchangeId, token) ?? throw new NotFoundException("exchange_not_found", "Обмен не найден.");
            if (exchange.Status != ExchangeStatus.Frozen)
            {
                throw new ConflictException("exchange_not_frozen", "Обмен не заморожен.");
            }

            UnfreezeCore(exchange, caseId, moderatorId, Now());
            await _cases.SaveAsync(token);
            return 0;
        }, ct);

        _notify.Dispatch();
        return new ExchangeFreezeDto(peek.ExchangeId, ExchangeStatus.Active);
    }

    // ---------- последствия решений ----------

    /// <summary>Спор по отзыву: оставить (Frozen → Published) или скрыть (→ Hidden); рейтинг автора отзыва получателя пересчитывается.</summary>
    private async Task ApplyReviewDecisionAsync(Case found, CaseDecision decision, CancellationToken ct)
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
            _audit.Add(new AuditEntry(found.ModeratorId, AuditActions.ReviewHidden, "Review", review.Id, new { caseId = found.Id }));
        }

        await _cases.SaveAsync(ct);
        await _reviewService.RecalculateRatingAsync(review.TargetId, ct);
    }

    /// <summary>Принудительное завершение обмена (из Active или Frozen): уроки отменяются, обоим уведомление, приглашения на отзыв ставятся после коммита.</summary>
    private async Task<(IReadOnlyList<LessonSessionDto>, DateTime?)> EndExchangeAsync(Case found, Guid moderatorId, DateTime now, CancellationToken ct)
    {
        var exchange = await _exchanges.FindAsync(found.ExchangeId, ct) ?? throw new NotFoundException("exchange_not_found", "Обмен не найден.");
        if (exchange.Status is not (ExchangeStatus.Active or ExchangeStatus.Frozen))
        {
            throw new ConflictException("exchange_not_open", "Обмен уже закрыт: завершать нечего.");
        }

        exchange.AbortByModerator("moderation", now);
        var cancelled = await _lessons.CancelOpenAsync(exchange.Id, exchange.UserAId, exchange.UserBId, ct);
        _audit.Add(new AuditEntry(moderatorId, AuditActions.ExchangeForceEnded, "Exchange", exchange.Id, new { caseId = found.Id }));
        foreach (var user in new[] { exchange.UserAId, exchange.UserBId })
        {
            _notify.Add(user, NotificationType.ExchangeAborted, new { exchangeId = exchange.Id, reason = "moderation" });
        }

        return (cancelled, now);
    }

    /// <summary>Заморозка (если обмен идёт) и запрос на блокировку виновного админу.</summary>
    private async Task<IReadOnlyList<LessonSessionDto>> FreezeAndRequestBanAsync(Case found, Guid moderatorId, DateTime now, CancellationToken ct)
    {
        IReadOnlyList<LessonSessionDto> cancelled = [];
        var exchange = await _exchanges.FindAsync(found.ExchangeId, ct);
        if (exchange is { Status: ExchangeStatus.Active })
        {
            cancelled = await FreezeCoreAsync(exchange, found.Id, moderatorId, now, ct);
        }

        _cases.AddBanRequest(BanRequest.ByModerator(found.AgainstUserId, found.Id, moderatorId, found.DecisionReason));
        _audit.Add(new AuditEntry(moderatorId, AuditActions.BanRequested, "User", found.AgainstUserId, new { caseId = found.Id }));
        return cancelled;
    }

    /// <summary>
    /// Решения без собственных последствий для обмена (предупреждение, «нарушений нет»): если обмен заморожен жалобой, а других жалоб по нему в работе нет,
    /// он размораживается. Спор по отзыву обмен не замораживал и не трогает.
    /// </summary>
    private async Task<IReadOnlyList<LessonSessionDto>> UnfreezeIfNothingHoldsAsync(Case found, Guid moderatorId, DateTime now, CancellationToken ct)
    {
        if (found.Type == CaseType.ReviewDispute)
        {
            return [];
        }

        var exchange = await _exchanges.FindAsync(found.ExchangeId, ct);
        if (exchange is { Status: ExchangeStatus.Frozen } && !await _cases.HasOtherActiveComplaintAsync(found.ExchangeId, found.Id, ct))
        {
            UnfreezeCore(exchange, found.Id, moderatorId, now);
        }

        return [];
    }

    /// <summary>Предупреждение виновному (12 месяцев). Третье действующее создаёт запрос на бан для админа (если такого запроса уже нет).</summary>
    private async Task IssueWarningAsync(Case found, Guid moderatorId, DateTime now, CancellationToken ct)
    {
        var culprit = found.AgainstUserId;
        _cases.AddWarning(Warning.Issue(culprit, found.Id, moderatorId, found.DecisionReason, now));
        _audit.Add(new AuditEntry(moderatorId, AuditActions.WarningIssued, "User", culprit, new { caseId = found.Id }));
        _notify.Add(culprit, NotificationType.WarningIssued, new { caseId = found.Id, exchangeId = found.ExchangeId, reason = found.DecisionReason });
        await _cases.SaveAsync(ct);

        if (await _cases.CountActiveWarningsAsync(culprit, now, ct) >= Warning.BanThreshold && !await _cases.HasPendingBanRequestAsync(culprit, ct))
        {
            _cases.AddBanRequest(BanRequest.Automatic(culprit, found.Id, $"Действующих предупреждений за {Warning.ValidMonths} месяцев: не меньше {Warning.BanThreshold}."));
            _audit.Add(new AuditEntry(null, AuditActions.BanRequested, "User", culprit, new { caseId = found.Id, automatic = true }));
        }
    }

    private async Task<IReadOnlyList<LessonSessionDto>> FreezeCoreAsync(Exchange exchange, Guid caseId, Guid moderatorId, DateTime now, CancellationToken ct)
    {
        exchange.Freeze(now);
        var cancelled = await _lessons.CancelOpenAsync(exchange.Id, exchange.UserAId, exchange.UserBId, ct);
        _audit.Add(new AuditEntry(moderatorId, AuditActions.ExchangeFrozen, "Exchange", exchange.Id, new { caseId }));
        foreach (var user in new[] { exchange.UserAId, exchange.UserBId })
        {
            _notify.Add(user, NotificationType.ExchangeFrozen, new { exchangeId = exchange.Id });
        }

        return cancelled;
    }

    private void UnfreezeCore(Exchange exchange, Guid caseId, Guid moderatorId, DateTime now)
    {
        exchange.Unfreeze(now);
        _audit.Add(new AuditEntry(moderatorId, AuditActions.ExchangeUnfrozen, "Exchange", exchange.Id, new { caseId }));
        foreach (var user in new[] { exchange.UserAId, exchange.UserBId })
        {
            _notify.Add(user, NotificationType.ExchangeUnfrozen, new { exchangeId = exchange.Id });
        }
    }

    // ---------- внутреннее ----------

    private async Task<Case> RequireOwnedAsync(Guid moderatorId, Guid caseId, CancellationToken ct)
    {
        var found = await _cases.FindAsync(caseId, tracked: false, ct);
        if (found is null || found.ModeratorId != moderatorId)
        {
            throw new NotFoundException("case_not_found", "Обращение не найдено.");
        }

        return found;
    }

    private async Task<Case> RequireOwnedForExchangeAsync(Guid moderatorId, Guid caseId, CancellationToken ct)
    {
        var found = await RequireOwnedAsync(moderatorId, caseId, ct);
        if (found.Type == CaseType.ReviewDispute)
        {
            throw new ConflictException("case_type_not_supported", "Спор по отзыву обмен не замораживает.");
        }

        if (found.Status is not (CaseStatus.InProgress or CaseStatus.WaitingResponse))
        {
            throw new ConflictException("case_not_in_work", "Обращение не в работе.");
        }

        return found;
    }

    private async Task RequireModeratorAsync(Guid moderatorId, CancellationToken ct)
    {
        var user = await _profiles.FindUserAsync(moderatorId, ct) ?? throw new NotFoundException("user_not_found", "Пользователь не найден.");
        if (user.Role != UserRole.Moderator || user.IsRegular || user.Status != UserStatus.Active)
        {
            throw new ForbiddenException("moderator_required", "Доступно только модератору.");
        }
    }
}
