using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Common;
using SkillSwap.Application.Exceptions;
using SkillSwap.Application.ExchangeRequests;
using SkillSwap.Application.Auth;
using SkillSwap.Application.Exchanges;
using SkillSwap.Application.Notifications;
using SkillSwap.Application.Profile;
using SkillSwap.Application.Reviews;
using SkillSwap.Application.Sessions;
using SkillSwap.Application.Staff;
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
    private readonly AccountBlocker _blocker;
    private readonly IExchangeRequestStore _requests;
    private readonly IMailSender _mail;
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
        ICaseStore cases, IExchangeStore exchanges, IExchangeRequestStore requests, IMailSender mail, AccountBlocker blocker, LessonSessionService lessons, ReviewScheduler reviewScheduler, IProfileStore profiles, IRefreshTokenRepository refreshTokens,
        IRealtimeDisconnector disconnector, AdminCaseService admin, CaseConsequences consequences, IUnitOfWork uow, IAuditLog audit, INotificationPublisher notify, TimeProvider clock)
    {
        _cases = cases;
        _exchanges = exchanges;
        _blocker = blocker;
        _requests = requests;
        _mail = mail;
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

    /// <summary>
    /// Одобрить запрос: аккаунт блокируется сразу (общий путь <see cref="AccountBlocker"/>, тот же, что у ручной блокировки): сеансы отзываются, соединения отключаются, активные и
    /// замороженные обмены прерываются (партнёру причина «партнёр недоступен»), ожидающие заявки истекают, заблокированному уходит письмо.
    /// </summary>
    public async Task<BanRequestDto> ApproveAsync(Guid adminId, Guid banRequestId, DecideBanRequest? request, CancellationToken ct)
    {
        await RequireAdminAsync(adminId, ct);
        var peek = await _cases.FindBanRequestAsync(banRequestId, ct) ?? throw new NotFoundException("ban_request_not_found", "Запрос на блокировку не найден.");

        BlockOutcome outcome = null!;
        await _uow.ExecuteAsync(async token =>
        {
            // Блокируем админа, пользователя, запросившего модератора и всех партнёров по открытым обменам пользователя.
            var lockIds = (await _blocker.UsersToLockAsync(peek.UserId, token)).Append(adminId).ToList();
            if (peek.RequestedById is { } requestedBy)
            {
                lockIds.Add(requestedBy);
            }

            await _uow.LockUsersAsync(lockIds, token);

            var ban = await _cases.FindBanRequestAsync(banRequestId, token) ?? throw new NotFoundException("ban_request_not_found", "Запрос на блокировку не найден.");
            var now = Now();
            ban.Approve(adminId, now);   // уже решён: 409 invalid_state_transition
            outcome = await _blocker.ApplyAsync(adminId, ban.UserId, now, "ban_request", request?.Comment, banRequestId, token);   // не действующий аккаунт: 400 user_not_active
            _audit.Add(new AuditEntry(adminId, AuditActions.BanDecided, "User", ban.UserId, new { banRequestId, approved = true, comment = request?.Comment, exchangesAborted = outcome.ExchangesAborted }));
            NotifyRequester(ban, approved: true);
            await _cases.SaveAsync(token);
            return 0;
        }, ct);

        await _blocker.FinishAsync(outcome, ct);
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
