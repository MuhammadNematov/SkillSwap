using Microsoft.Extensions.Logging;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Chat;
using SkillSwap.Application.Common;
using SkillSwap.Application.Exceptions;
using SkillSwap.Application.ExchangeRequests;
using SkillSwap.Application.Exchanges;
using SkillSwap.Application.Files;
using SkillSwap.Application.Notifications;
using SkillSwap.Application.Profile;
using SkillSwap.Application.Reviews;
using SkillSwap.Application.Sessions;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Moderation;

/// <summary>
/// Подача и отзыв обращений обычным пользователем (раздел 8.2 ТЗ): спор по отзыву, жалоба на поведение, жалоба на неявки и срыв.
/// Всё меняется в одной транзакции под блокировкой обоих участников. Спор замораживает отзыв (он перестаёт влиять на рейтинг), жалоба из звонка
/// замораживает обмен. Обвиняемый узнаёт об обращении, когда его берёт модератор (следующий шаг), а не при подаче.
/// </summary>
public sealed class CaseService
{
    /// <summary>Сколько обращений заявителя может быть в работе одновременно.</summary>
    public const int MaxActivePerUser = 3;

    /// <summary>Сколько обращений можно подать за 24 часа.</summary>
    public const int MaxPerDay = 5;

    private readonly ICaseStore _cases;
    private readonly IReviewStore _reviews;
    private readonly ReviewService _reviewService;
    private readonly IExchangeStore _exchanges;
    private readonly IMessageStore _messages;
    private readonly ILessonSessionStore _sessions;
    private readonly LessonSessionService _lessons;
    private readonly ILiveKitRoomService _rooms;
    private readonly IProfileStore _profiles;
    private readonly IUnitOfWork _uow;
    private readonly IAuditLog _audit;
    private readonly INotificationPublisher _notify;
    private readonly AvatarUrls _avatars;
    private readonly TimeProvider _clock;
    private readonly ILogger<CaseService> _logger;
    private readonly ICaseMessageStore _caseMessages;

    public CaseService(
        ICaseStore cases, IReviewStore reviews, ReviewService reviewService, IExchangeStore exchanges, IMessageStore messages, ILessonSessionStore sessions,
        LessonSessionService lessons, ILiveKitRoomService rooms, IProfileStore profiles, IUnitOfWork uow, IAuditLog audit, INotificationPublisher notify,
        AvatarUrls avatars, TimeProvider clock, ILogger<CaseService> logger, ICaseMessageStore caseMessages)
    {
        _caseMessages = caseMessages;
        _cases = cases;
        _reviews = reviews;
        _reviewService = reviewService;
        _exchanges = exchanges;
        _messages = messages;
        _sessions = sessions;
        _lessons = lessons;
        _rooms = rooms;
        _profiles = profiles;
        _uow = uow;
        _audit = audit;
        _notify = notify;
        _avatars = avatars;
        _clock = clock;
        _logger = logger;
    }

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;

    // ---------- подача ----------

    public async Task<CaseDto> OpenAsync(Guid userId, OpenCaseRequest request, CancellationToken ct)
    {
        // Форма запроса проверяется до обращения к базе: эти ошибки не зависят от состояния обмена.
        if (request.Type != CaseType.ReviewDispute && request.ReviewId is not null)
        {
            throw new BadRequestException("case_review_not_allowed", "Отзыв указывается только в споре по отзыву.");
        }

        if (request.Type == CaseType.ReviewDispute && request.ReviewId is null)
        {
            throw new BadRequestException("case_review_required", "Спор по отзыву подаётся на конкретный отзыв.");
        }

        if (request.FromCall && request.Type != CaseType.Conduct)
        {
            throw new BadRequestException("case_from_call_conduct_only", "Из звонка можно подать только жалобу на поведение.");
        }

        if (!CaseRules.CategoriesFor(request.Type).Contains(request.Category))
        {
            throw new BadRequestException("case_category_mismatch", "Категория не подходит к типу обращения.");
        }

        await RequireActiveRegularAsync(userId, ct);
        var peek = await RequireParticipantAsync(userId, request.ExchangeId, ct);
        var partnerId = ReviewService.PartnerOf(peek, userId);

        Guid caseId = Guid.Empty;
        IReadOnlyList<LessonSessionDto> cancelled = [];
        Guid? roomSession = null;
        await _uow.ExecuteAsync(async token =>
        {
            await _uow.LockUsersAsync([userId, partnerId], token);
            var exchange = await _exchanges.GetViewAsync(request.ExchangeId, token) ?? throw new NotFoundException("exchange_not_found", "Обмен не найден.");
            if ((exchange.UserA.Id == partnerId ? exchange.UserA : exchange.UserB).IsDeleted)
            {
                throw new ConflictException("partner_deleted", "Партнёр удалил аккаунт: обращение по этому обмену подать нельзя.");
            }

            var now = Now();
            Review? review = null;
            DateTime? anchor = exchange.ClosedAt;
            if (request.Type == CaseType.ReviewDispute)
            {
                review = await RequireDisputableReviewAsync(userId, request, token);
                anchor = review.PublishedAt;
            }

            if (!CaseRules.IsWithinFilingWindow(request.Type, anchor, now))
            {
                throw new ConflictException("filing_window_closed", "Срок подачи обращения истёк.");
            }

            await CheckLimitsAsync(userId, request, now, token);
            var evidence = await CheckEvidenceAsync(request, token);
            if (request.FromCall)
            {
                roomSession = await RequireInCallAsync(userId, exchange, token);
            }

            var minorInvolved = await AnyMinorAsync([userId, partnerId], now, token);
            var created = Case.Open(
                request.Type, request.Category, request.ExchangeId, review?.Id, userId, partnerId, request.Description, minorInvolved, request.FromCall, now);
            _cases.Add(created);
            foreach (var messageId in evidence)
            {
                _cases.AddEvidence(CaseEvidence.Create(created.Id, messageId, now));
            }

            if (review is not null)
            {
                review.Freeze();   // спор: отзыв не влияет на рейтинг до решения
                await _cases.SaveAsync(token);
                await _reviewService.RecalculateRatingAsync(review.TargetId, token);
            }

            if (request.FromCall)
            {
                cancelled = await FreezeExchangeAsync(created, exchange, now, token);
            }

            _audit.Add(new AuditEntry(userId, AuditActions.CaseOpened, "Case", created.Id,
                new { type = created.Type, category = created.Category, exchangeId = created.ExchangeId, reviewId = created.ReviewId, fromCall = created.FromCall, evidence = evidence.Count }));
            await _cases.SaveAsync(token);
            caseId = created.Id;
            return 0;
        }, ct);

        _notify.Dispatch();
        await AfterFreezeAsync(cancelled, roomSession);
        return await GetAsync(userId, caseId, ct);
    }

    /// <summary>Заявитель отзывает обращение, пока его не взяли в работу: отзыв и обмен возвращаются в прежнее состояние.</summary>
    public async Task<CaseDto> WithdrawAsync(Guid userId, Guid caseId, CancellationToken ct)
    {
        await RequireActiveRegularAsync(userId, ct);
        var peek = await _cases.FindAsync(caseId, tracked: false, ct);
        if (peek is null || peek.OpenedById != userId)
        {
            throw new NotFoundException("case_not_found", "Обращение не найдено.");
        }

        await _uow.ExecuteAsync(async token =>
        {
            await _uow.LockUsersAsync([peek.OpenedById, peek.AgainstUserId], token);
            var found = await _cases.FindAsync(caseId, tracked: true, token) ?? throw new NotFoundException("case_not_found", "Обращение не найдено.");
            found.Withdraw(userId);   // не Open: 409 invalid_state_transition
            var now = Now();

            if (found.ReviewId is { } reviewId)
            {
                var review = await _reviews.FindAsync(reviewId, token);
                if (review is { Status: ReviewStatus.Frozen })
                {
                    review.Unfreeze();
                    await _cases.SaveAsync(token);
                    await _reviewService.RecalculateRatingAsync(review.TargetId, token);
                }
            }

            if (found.FromCall && !await _cases.HasActiveFromCallAsync(found.ExchangeId, found.Id, token))
            {
                var exchange = await _exchanges.FindAsync(found.ExchangeId, token);
                if (exchange is { Status: ExchangeStatus.Frozen })
                {
                    exchange.Unfreeze(now);
                    _audit.Add(new AuditEntry(userId, AuditActions.ExchangeUnfrozen, "Exchange", exchange.Id, new { caseId = found.Id, reason = "case_withdrawn" }));
                    foreach (var participant in new[] { found.OpenedById, found.AgainstUserId })
                    {
                        _notify.Add(participant, NotificationType.ExchangeUnfrozen, new { exchangeId = exchange.Id });
                    }
                }
            }

            _audit.Add(new AuditEntry(userId, AuditActions.CaseWithdrawn, "Case", found.Id, new { exchangeId = found.ExchangeId }));
            await _cases.SaveAsync(token);
            return 0;
        }, ct);

        _notify.Dispatch();
        return await GetAsync(userId, caseId, ct);
    }

    // ---------- чтение ----------

    public async Task<CaseListDto> ListMineAsync(Guid userId, CaseListFilter filter, int page, int pageSize, CancellationToken ct)
    {
        Paging.Validate(page, pageSize);
        await RequireActiveRegularAsync(userId, ct);
        var (items, total) = await _cases.ListForUserAsync(userId, filter, page, pageSize, ct);
        return new CaseListDto(await ToDtosAsync(userId, items, ct), page, pageSize, total);
    }

    public async Task<CaseDto> GetAsync(Guid userId, Guid caseId, CancellationToken ct)
    {
        await RequireActiveRegularAsync(userId, ct);
        var found = await _cases.FindAsync(caseId, tracked: false, ct);
        if (found is null || !IsVisibleTo(found, userId))
        {
            throw new NotFoundException("case_not_found", "Обращение не найдено.");
        }

        return (await ToDtosAsync(userId, [found], ct))[0];
    }

    /// <summary>Заявитель видит своё обращение всегда; тот, на кого оно подано, — только когда его взяли в работу (до этого подача ему не раскрывается).</summary>
    private static bool IsVisibleTo(Case value, Guid userId) =>
        value.OpenedById == userId || (value.AgainstUserId == userId && value.Status is not (CaseStatus.Open or CaseStatus.Withdrawn));

    private async Task<IReadOnlyList<CaseDto>> ToDtosAsync(Guid viewerId, IReadOnlyList<Case> items, CancellationToken ct)
    {
        if (items.Count == 0)
        {
            return [];
        }

        var evidence = await _cases.GetEvidenceIdsAsync(items.Select(c => c.Id).ToList(), ct);
        var pending = await _caseMessages.CountUnansweredAsync(items.Select(c => c.Id).ToList(), viewerId, ct);
        var appeals = await _cases.GetAppealsAsync(items.Select(c => c.Id).ToList(), ct);
        var now = Now();
        var exchanges = await _exchanges.GetViewsAsync(items.Select(c => c.ExchangeId).Distinct().ToList(), ct);
        var people = exchanges.Values.SelectMany(e => new[] { e.UserA, e.UserB }).GroupBy(u => u.Id).Select(g => g.First()).ToList();
        var urls = await _avatars.ForViewerAsync(viewerId, people.Select(p => (p.Id, p.AvatarUrl)).ToList(), ct);
        RequestUserDto Person(Guid id)
        {
            var user = people.First(p => p.Id == id);
            return user with { AvatarUrl = urls.GetValueOrDefault(id) };
        }

        return items.Select(c =>
        {
            var decided = c.Status is CaseStatus.Resolved or CaseStatus.Appealed or CaseStatus.AppealReview or CaseStatus.Closed;
            return new CaseDto(
                c.Id, c.Type, c.Category, c.Status, c.Priority, c.ExchangeId, c.ReviewId, Person(c.OpenedById), Person(c.AgainstUserId),
                c.OpenedById == viewerId ? CaseRole.Opener : CaseRole.Against, c.Description, c.FromCall, c.CreatedAt,
                evidence.GetValueOrDefault(c.Id) ?? [], decided ? c.Decision : null, decided && c.AlsoWarned, decided ? c.DecisionReason : null,
                decided ? c.ResolvedAt : null, c.Status == CaseStatus.WaitingResponse ? c.ResponseDeadlineAt : null,
                c.Status == CaseStatus.Open && c.OpenedById == viewerId, c.Status == CaseStatus.WaitingResponse ? pending.GetValueOrDefault(c.Id) : 0,
                c.Status == CaseStatus.Resolved && !c.WasAppealed && c.ResolvedAt is { } resolvedAt && now <= resolvedAt + CaseRules.AppealWindow,
                appeals.TryGetValue(c.Id, out var appeal)
                    ? new CaseAppealDto(appeal.AppellantId, appeal.Reason, appeal.CreatedAt, c.Status == CaseStatus.AppealReview, appeal.Outcome, appeal.FinalDecision, appeal.DecidedAt)
                    : null);
        }).ToList();
    }

    // ---------- проверки подачи ----------

    private async Task<Review> RequireDisputableReviewAsync(Guid userId, OpenCaseRequest request, CancellationToken ct)
    {
        if (request.ReviewId is not { } reviewId)
        {
            throw new BadRequestException("case_review_required", "Спор по отзыву подаётся на конкретный отзыв.");
        }

        var review = await _reviews.FindAsync(reviewId, ct);
        if (review is null || review.ExchangeId != request.ExchangeId || review.TargetId != userId || review.Status is ReviewStatus.Pending or ReviewStatus.Hidden)
        {
            throw new NotFoundException("review_not_found", "Отзыв не найден.");   // чужой, ещё скрытый и скрытый модератором выглядят как несуществующие
        }

        if (review.Status == ReviewStatus.Frozen || await _cases.HasDisputeForReviewAsync(review.Id, ct))
        {
            throw new ConflictException(review.Status == ReviewStatus.Frozen ? "review_already_disputed" : "case_already_exists", "Этот отзыв уже оспаривался.");
        }

        return review;
    }

    private async Task CheckLimitsAsync(Guid userId, OpenCaseRequest request, DateTime now, CancellationToken ct)
    {
        if (request.Type != CaseType.ReviewDispute && await _cases.HasActiveAsync(userId, request.ExchangeId, request.Type, ct))
        {
            throw new ConflictException("case_already_open", "По этому обмену у вас уже есть такое обращение в работе.");
        }

        if (await _cases.CountActiveByOpenerAsync(userId, ct) >= MaxActivePerUser)
        {
            throw new ConflictException("too_many_open_cases", $"Одновременно в работе может быть не больше {MaxActivePerUser} ваших обращений.");
        }

        if (await _cases.CountOpenedSinceAsync(userId, now.AddHours(-24), ct) >= MaxPerDay)
        {
            throw new TooManyRequestsException("case_rate_limited", $"Не больше {MaxPerDay} обращений за сутки.");
        }
    }

    /// <summary>Доказательства: уникальные сообщения этого обмена (удалённые тоже), не больше 20.</summary>
    private async Task<IReadOnlyList<Guid>> CheckEvidenceAsync(OpenCaseRequest request, CancellationToken ct)
    {
        var ids = (request.MessageIds ?? []).Distinct().ToList();
        if (ids.Count > CaseRules.MaxEvidenceMessages)
        {
            throw new BadRequestException("too_many_evidence", $"К обращению можно приложить не больше {CaseRules.MaxEvidenceMessages} сообщений.");
        }

        if (ids.Count > 0 && (await _messages.ListIdsInExchangeAsync(request.ExchangeId, ids, ct)).Count != ids.Count)
        {
            throw new BadRequestException("evidence_not_in_exchange", "Приложить можно только сообщения из чата этого обмена.");
        }

        return ids;
    }

    /// <summary>Жалоба из звонка: заявитель должен сейчас находиться в комнате идущего урока этого обмена, обмен должен идти.</summary>
    private async Task<Guid> RequireInCallAsync(Guid userId, ExchangeRow exchange, CancellationToken ct)
    {
        if (exchange.Status != ExchangeStatus.Active)
        {
            throw new ConflictException("not_in_call", "Пожаловаться из звонка можно только в активном обмене.");
        }

        return await _sessions.FindInCallSessionAsync(exchange.Id, userId, ct)
            ?? throw new ConflictException("not_in_call", "Пожаловаться из звонка можно, только находясь в комнате идущего урока.");
    }

    private async Task<bool> AnyMinorAsync(IReadOnlyCollection<Guid> userIds, DateTime now, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(now);
        return (await _profiles.FindUsersAsync(userIds, ct)).Values.Any(u => u.IsMinor(today));
    }

    /// <summary>Заморозка обмена по жалобе из звонка: открытые уроки отменяются, обоим приходит уведомление.</summary>
    private async Task<IReadOnlyList<LessonSessionDto>> FreezeExchangeAsync(Case created, ExchangeRow view, DateTime now, CancellationToken ct)
    {
        var exchange = await _exchanges.FindAsync(created.ExchangeId, ct) ?? throw new NotFoundException("exchange_not_found", "Обмен не найден.");
        exchange.Freeze(now);
        var cancelled = await _lessons.CancelOpenAsync(exchange.Id, view.UserA.Id, view.UserB.Id, ct);
        _audit.Add(new AuditEntry(created.OpenedById, AuditActions.ExchangeFrozen, "Exchange", exchange.Id, new { caseId = created.Id, reason = "complaint_from_call" }));
        foreach (var participant in new[] { view.UserA.Id, view.UserB.Id })
        {
            _notify.Add(participant, NotificationType.ExchangeFrozen, new { exchangeId = exchange.Id });
        }

        return cancelled;
    }

    /// <summary>После коммита: обновить карточки отменённых уроков и закрыть комнату звонка (если медиасервер недоступен, комната закроется по расписанию).</summary>
    private async Task AfterFreezeAsync(IReadOnlyList<LessonSessionDto> cancelled, Guid? roomSession)
    {
        await _lessons.PublishUpdatesAsync(cancelled);
        if (roomSession is { } sessionId && _rooms.IsConfigured)
        {
            try
            {
                await _rooms.DeleteRoomAsync(VideoRoomService.RoomName(sessionId), CancellationToken.None);
            }
            catch (LiveKitUnavailableException e)
            {
                _logger.LogWarning(e, "Не удалось закрыть комнату урока {SessionId} после заморозки обмена.", sessionId);
            }
        }
    }

    private async Task<ExchangeRow> RequireParticipantAsync(Guid userId, Guid exchangeId, CancellationToken ct)
    {
        var exchange = await _exchanges.GetViewAsync(exchangeId, ct);
        if (exchange is null || (exchange.UserA.Id != userId && exchange.UserB.Id != userId))
        {
            throw new NotFoundException("exchange_not_found", "Обмен не найден.");
        }

        return exchange;
    }

    private async Task RequireActiveRegularAsync(Guid userId, CancellationToken ct)
    {
        var user = await _profiles.FindUserAsync(userId, ct) ?? throw new NotFoundException("user_not_found", "Пользователь не найден.");
        if (!user.IsRegular)
        {
            throw new ForbiddenException("regular_account_required", "Подача обращений доступна только обычным аккаунтам.");
        }

        if (user.Status != UserStatus.Active)
        {
            throw new ForbiddenException("account_not_active", "Аккаунт недоступен.");
        }
    }
}
