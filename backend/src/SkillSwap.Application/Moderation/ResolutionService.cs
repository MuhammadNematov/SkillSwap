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
/// а также ручная заморозка и разморозка обмена и досрочное завершение ожидания пояснений. Всё в одной транзакции под блокировкой модератора и обоих участников:
/// решение, его последствия (<see cref="CaseConsequences"/>), отзыв доступа к чату, аудит и уведомления либо есть целиком, либо нет. Участникам уходит уведомление
/// с письмом (tg, ru, en), а через 7 дней без апелляции обращение закрывается.
/// </summary>
public sealed class ResolutionService
{
    private readonly ICaseStore _cases;
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
    private readonly CaseConsequences _consequences;

    public ResolutionService(
        ICaseStore cases, IExchangeStore exchanges, LessonSessionService lessons, ReviewScheduler reviewScheduler, ModerationService moderation, IProfileStore profiles,
        IUnitOfWork uow, IAuditLog audit, INotificationPublisher notify, IBackgroundJobs jobs, TimeProvider clock, CaseConsequences consequences)
    {
        _cases = cases;
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
        _consequences = consequences;
    }

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;

    // ---------- решение ----------

    /// <summary>
    /// Вынести решение (InProgress → Resolved). Только модератор, взявший обращение; пока ждём пояснений, решение выносить нельзя (409 <c>waiting_for_explanations</c>:
    /// дождаться ответов или завершить ожидание досрочно). Последствия по решению, отзыв доступа к чату, уведомления и письма участникам, запись в аудит — в одной транзакции.
    /// </summary>
    public async Task<ModerationCaseDto> ResolveAsync(Guid moderatorId, Guid caseId, ResolveCaseRequest request, CancellationToken ct)
    {
        await RequireModeratorAsync(moderatorId, ct);
        var peek = await RequireOwnedAsync(moderatorId, caseId, ct);

        ConsequenceResult result = ConsequenceResult.None;
        DateTime resolvedAt = default;
        await _uow.ExecuteAsync(async token =>
        {
            await _uow.LockUsersAsync([moderatorId, peek.OpenedById, peek.AgainstUserId], token);
            var found = await _cases.FindAsync(caseId, tracked: true, token) ?? throw new NotFoundException("case_not_found", "Обращение не найдено.");
            if (found.Status == CaseStatus.WaitingResponse)
            {
                throw new ConflictException("waiting_for_explanations", "Обращение ждёт пояснений: дождитесь ответов или завершите ожидание досрочно.");
            }

            var now = Now();
            resolvedAt = now;
            found.Resolve(moderatorId, request.Decision, request.AlsoWarned, request.Reason, now);   // решение не подходит типу, предупреждение не сочетается, короткое обоснование: 400
            result = await _consequences.ApplyAsync(found, moderatorId, now, token);

            await _moderation.RevokeChatAccessAsync(caseId, moderatorId, token);
            _audit.Add(new AuditEntry(moderatorId, AuditActions.CaseResolved, "Case", caseId, new { decision = request.Decision, alsoWarned = result.Warned }));
            var appealUntil = now + CaseRules.AppealWindow;
            foreach (var (user, role) in new[] { (found.OpenedById, CaseRole.Opener), (found.AgainstUserId, CaseRole.Against) })
            {
                _notify.Add(user, NotificationType.CaseResolved, new
                {
                    caseId,
                    exchangeId = found.ExchangeId,
                    role = role.ToString(),
                    decision = request.Decision.ToString(),
                    alsoWarned = result.Warned,
                    youWereWarned = result.Warned && role == CaseRole.Against,
                    appealUntil,
                });
            }

            await _cases.SaveAsync(token);
            return 0;
        }, ct);

        _notify.Dispatch();
        _jobs.Schedule<CaseJobs>(j => j.CloseIfDueAsync(caseId), new DateTimeOffset(resolvedAt + CaseRules.AppealWindow, TimeSpan.Zero));
        await _lessons.PublishUpdatesAsync(result.CancelledLessons);
        if (result.ExchangeClosedAt is { } closed)
        {
            _reviewScheduler.OnExchangeClosed(peek.ExchangeId, closed);
        }

        return await _moderation.GetAsync(moderatorId, caseId, ct);
    }

    // ---------- досрочное завершение ожидания ----------

    /// <summary>
    /// Завершить ожидание пояснений досрочно (WaitingResponse → InProgress) с обязательной причиной: после этого можно выносить решение. Причина пишется в аудит.
    /// Уже вернувшееся в работу — 409 <c>invalid_state_transition</c>. Ответы, которые успели прийти, сохраняются; поздние ответы не принимаются.
    /// </summary>
    public async Task<ModerationCaseDto> StopWaitingAsync(Guid moderatorId, Guid caseId, StopWaitingRequest request, CancellationToken ct)
    {
        await RequireModeratorAsync(moderatorId, ct);
        var peek = await RequireOwnedAsync(moderatorId, caseId, ct);

        await _uow.ExecuteAsync(async token =>
        {
            await _uow.LockUsersAsync([moderatorId, peek.OpenedById, peek.AgainstUserId], token);
            var found = await _cases.FindAsync(caseId, tracked: true, token) ?? throw new NotFoundException("case_not_found", "Обращение не найдено.");
            var reason = Case.CleanText(request.Reason, CaseRules.ReasonMinLength, CaseRules.ReasonMaxLength, "stop_waiting_reason");
            if (!found.ResumeAfterExplanation())
            {
                throw new ConflictException("invalid_state_transition", "Обращение не ждёт пояснений.");
            }

            _audit.Add(new AuditEntry(moderatorId, AuditActions.ExplanationStopped, "Case", caseId, new { reason }));
            await _cases.SaveAsync(token);
            return 0;
        }, ct);

        return await _moderation.GetAsync(moderatorId, caseId, ct);
    }

    // ---------- ручная заморозка и разморозка ----------

    /// <summary>Заморозить обмен по жалобе, пока она рассматривается (в том числе пока ждём пояснений): чат только для чтения, открытые уроки отменяются.</summary>
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

            cancelled = await _consequences.FreezeCoreAsync(exchange, caseId, moderatorId, Now(), token);
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

            _consequences.UnfreezeCore(exchange, caseId, moderatorId, Now());
            await _cases.SaveAsync(token);
            return 0;
        }, ct);

        _notify.Dispatch();
        return new ExchangeFreezeDto(peek.ExchangeId, ExchangeStatus.Active);
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
