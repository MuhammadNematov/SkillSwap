using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Common;
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
/// Запросы на блокировку пользователей (раздел 8.3 ТЗ): их создаёт модератор решением «заморозить и запросить бан» либо система после трёх предупреждений, решает админ.
/// Одобрение блокирует аккаунт сразу (минимальная блокировка этапа 6: статус <c>Blocked</c>, отзыв всех сеансов, отключение открытых соединений SignalR, прерывание
/// активных обменов с нейтральной причиной для партнёра), отклонение размораживает обмен, если по нему нет других жалоб.
/// </summary>
public sealed class BanRequestService
{
    private readonly ICaseStore _cases;
    private readonly IExchangeStore _exchanges;
    private readonly LessonSessionService _lessons;
    private readonly ReviewScheduler _reviewScheduler;
    private readonly IProfileStore _profiles;
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly IRealtimeDisconnector _disconnector;
    private readonly AdminCaseService _admin;
    private readonly CaseConsequences _consequences;
    private readonly IUnitOfWork _uow;
    private readonly IAuditLog _audit;
    private readonly INotificationPublisher _notify;
    private readonly TimeProvider _clock;

    public BanRequestService(
        ICaseStore cases, IExchangeStore exchanges, LessonSessionService lessons, ReviewScheduler reviewScheduler, IProfileStore profiles, IRefreshTokenRepository refreshTokens,
        IRealtimeDisconnector disconnector, AdminCaseService admin, CaseConsequences consequences, IUnitOfWork uow, IAuditLog audit, INotificationPublisher notify, TimeProvider clock)
    {
        _cases = cases;
        _exchanges = exchanges;
        _lessons = lessons;
        _reviewScheduler = reviewScheduler;
        _profiles = profiles;
        _refreshTokens = refreshTokens;
        _disconnector = disconnector;
        _admin = admin;
        _consequences = consequences;
        _uow = uow;
        _audit = audit;
        _notify = notify;
        _clock = clock;
    }

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;

    public async Task<BanRequestListDto> ListAsync(Guid adminId, BanRequestStatus? status, int page, int pageSize, CancellationToken ct)
    {
        Paging.Validate(page, pageSize);
        await RequireAdminAsync(adminId, ct);
        var (items, total) = await _cases.ListBanRequestsAsync(status, page, pageSize, ct);
        return new BanRequestListDto(await _admin.BanDtosAsync(items, ct), page, pageSize, total);
    }

    /// <summary>Одобрить запрос: аккаунт блокируется сразу, сеансы отзываются, соединения отключаются, активные и замороженные обмены прерываются (партнёру причина «партнёр недоступен»).</summary>
    public async Task<BanRequestDto> ApproveAsync(Guid adminId, Guid banRequestId, DecideBanRequest? request, CancellationToken ct)
    {
        await RequireAdminAsync(adminId, ct);
        var peek = await _cases.FindBanRequestAsync(banRequestId, ct) ?? throw new NotFoundException("ban_request_not_found", "Запрос на блокировку не найден.");

        var cancelled = new List<LessonSessionDto>();
        var closed = new List<(Guid Id, DateTime ClosedAt)>();
        await _uow.ExecuteAsync(async token =>
        {
            // Блокируем админа, пользователя и всех партнёров по его открытым обменам; перечитываем обмены уже под блокировкой.
            var open = await _exchanges.FindOpenForUserForUpdateAsync(peek.UserId, token);
            var lockIds = open.SelectMany(e => new[] { e.UserAId, e.UserBId }).Append(peek.UserId).Append(adminId).ToList();
            if (peek.RequestedById is { } requestedBy)
            {
                lockIds.Add(requestedBy);
            }

            await _uow.LockUsersAsync(lockIds, token);
            open = await _exchanges.FindOpenForUserForUpdateAsync(peek.UserId, token);

            var ban = await _cases.FindBanRequestAsync(banRequestId, token) ?? throw new NotFoundException("ban_request_not_found", "Запрос на блокировку не найден.");
            var now = Now();
            ban.Approve(adminId, now);   // уже решён: 409 invalid_state_transition
            var user = await _profiles.FindUserForUpdateAsync(ban.UserId, token) ?? throw new NotFoundException("user_not_found", "Пользователь не найден.");
            user.Block();   // не действующий аккаунт: 400 user_not_active
            await _refreshTokens.RevokeAllForUserAsync(user.Id, now, token);

            foreach (var exchange in open)
            {
                exchange.AbortByBan(now);
                closed.Add((exchange.Id, now));
                _notify.Add(exchange.PartnerOf(user.Id), NotificationType.ExchangeAborted, new { exchangeId = exchange.Id, reason = ExchangeService.PartnerUnavailable });
                cancelled.AddRange(await _lessons.CancelOpenAsync(exchange.Id, exchange.UserAId, exchange.UserBId, token));
            }

            _audit.Add(new AuditEntry(adminId, AuditActions.BanDecided, "User", user.Id, new { banRequestId, approved = true, comment = request?.Comment, exchangesAborted = open.Count }));
            NotifyRequester(ban, approved: true);
            await _cases.SaveAsync(token);
            return 0;
        }, ct);

        _notify.Dispatch();
        await _disconnector.DisconnectUserAsync(peek.UserId, ct);
        await _lessons.PublishUpdatesAsync(cancelled);
        foreach (var (id, at) in closed)
        {
            _reviewScheduler.OnExchangeClosed(id, at);
        }

        return (await _admin.BanDtosAsync([(await _cases.FindBanRequestAsync(banRequestId, ct))!], ct))[0];
    }

    /// <summary>Отклонить запрос. Обмен, замороженный по этому обращению, размораживается, если по нему нет других незакрытых жалоб.</summary>
    public async Task<BanRequestDto> RejectAsync(Guid adminId, Guid banRequestId, DecideBanRequest? request, CancellationToken ct)
    {
        await RequireAdminAsync(adminId, ct);
        var peek = await _cases.FindBanRequestAsync(banRequestId, ct) ?? throw new NotFoundException("ban_request_not_found", "Запрос на блокировку не найден.");
        var relatedCase = peek.CaseId is { } caseId ? await _cases.FindAsync(caseId, tracked: false, ct) : null;

        await _uow.ExecuteAsync(async token =>
        {
            var people = new List<Guid> { adminId, peek.UserId };
            if (peek.RequestedById is { } requester)
            {
                people.Add(requester);
            }

            if (relatedCase is not null)
            {
                people.Add(relatedCase.OpenedById);
                people.Add(relatedCase.AgainstUserId);
            }

            await _uow.LockUsersAsync(people, token);
            var ban = await _cases.FindBanRequestAsync(banRequestId, token) ?? throw new NotFoundException("ban_request_not_found", "Запрос на блокировку не найден.");
            var now = Now();
            ban.Reject(adminId, now);   // уже решён: 409 invalid_state_transition

            if (relatedCase is not null)
            {
                var exchange = await _exchanges.FindAsync(relatedCase.ExchangeId, token);
                if (exchange is { Status: ExchangeStatus.Frozen } && !await _cases.HasOtherActiveComplaintAsync(relatedCase.ExchangeId, relatedCase.Id, token))
                {
                    _consequences.UnfreezeCore(exchange, relatedCase.Id, adminId, now);
                }
            }

            _audit.Add(new AuditEntry(adminId, AuditActions.BanDecided, "User", ban.UserId, new { banRequestId, approved = false, comment = request?.Comment }));
            NotifyRequester(ban, approved: false);
            await _cases.SaveAsync(token);
            return 0;
        }, ct);

        _notify.Dispatch();
        return (await _admin.BanDtosAsync([(await _cases.FindBanRequestAsync(banRequestId, ct))!], ct))[0];
    }

    private void NotifyRequester(BanRequest ban, bool approved)
    {
        if (ban.RequestedById is { } requester)
        {
            _notify.Add(requester, NotificationType.BanRequestDecided, new { banRequestId = ban.Id, userId = ban.UserId, approved });
        }
    }

    private async Task RequireAdminAsync(Guid adminId, CancellationToken ct)
    {
        var user = await _profiles.FindUserAsync(adminId, ct) ?? throw new NotFoundException("user_not_found", "Пользователь не найден.");
        if (user.Role is not (UserRole.Admin or UserRole.SuperAdmin) || user.IsRegular || user.Status != UserStatus.Active)
        {
            throw new ForbiddenException("admin_required", "Доступно только администратору.");
        }
    }
}
