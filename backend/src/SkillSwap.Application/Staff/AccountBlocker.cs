using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Auth;
using SkillSwap.Application.ExchangeRequests;
using SkillSwap.Application.Exchanges;
using SkillSwap.Application.Moderation;
using SkillSwap.Application.Notifications;
using SkillSwap.Application.Profile;
using SkillSwap.Application.Reviews;
using SkillSwap.Application.Sessions;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Staff;

/// <summary>Что осталось сделать после коммита блокировки: письмо, отключение соединений, карточки отменённых уроков и приглашения на отзыв.</summary>
public sealed record BlockOutcome(
    Guid UserId, string? Email, string Language, string Name, IReadOnlyList<LessonSessionDto> CancelledLessons, IReadOnlyList<(Guid Id, DateTime ClosedAt)> ClosedExchanges,
    int ExchangesAborted);

/// <summary>
/// Блокировка аккаунта админом (раздел 3 и 8.3 ТЗ): **один общий путь** для одобренного запроса на бан и для ручной блокировки, чтобы последствия не расходились.
/// Аккаунт сразу получает статус <c>Blocked</c>, все его сеансы отзываются, ожидающие заявки на обмен в обе стороны истекают без уведомлений отправителям, активные и
/// замороженные обмены прерываются (партнёру нейтральная причина «партнёр недоступен», открытые уроки отменяются), после коммита блокированному уходит письмо без деталей,
/// открытые соединения SignalR обрываются, а приглашения на отзыв ставятся по обычным правилам. Разблокировка прерванные обмены не возвращает.
/// Порядок вызова: <see cref="UsersToLockAsync"/>, блокировка этих пользователей, <see cref="ApplyAsync"/> в той же транзакции, после коммита <see cref="FinishAsync"/>.
/// </summary>
public sealed class AccountBlocker
{
    private readonly IExchangeStore _exchanges;
    private readonly IExchangeRequestStore _requests;
    private readonly IProfileStore _profiles;
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly LessonSessionService _lessons;
    private readonly ReviewScheduler _reviewScheduler;
    private readonly IRealtimeDisconnector _disconnector;
    private readonly INotificationPublisher _notify;
    private readonly IMailSender _mail;
    private readonly IAuditLog _audit;
    private readonly PendingStaffRequestsRevoker _pending;

    public AccountBlocker(
        IExchangeStore exchanges, IExchangeRequestStore requests, IProfileStore profiles, IRefreshTokenRepository refreshTokens, LessonSessionService lessons,
        ReviewScheduler reviewScheduler, IRealtimeDisconnector disconnector, INotificationPublisher notify, IMailSender mail, IAuditLog audit,
        PendingStaffRequestsRevoker pending)
    {
        _exchanges = exchanges;
        _requests = requests;
        _profiles = profiles;
        _refreshTokens = refreshTokens;
        _lessons = lessons;
        _reviewScheduler = reviewScheduler;
        _disconnector = disconnector;
        _notify = notify;
        _mail = mail;
        _audit = audit;
        _pending = pending;
    }

    /// <summary>Кого заблокировать в транзакции перед блокировкой аккаунта: самого пользователя и всех партнёров по его активным и замороженным обменам.</summary>
    public async Task<IReadOnlyList<Guid>> UsersToLockAsync(Guid userId, CancellationToken ct)
    {
        var open = await _exchanges.FindOpenForUserForUpdateAsync(userId, ct);
        return open.SelectMany(e => new[] { e.UserAId, e.UserBId }).Append(userId).Distinct().ToList();
    }

    /// <summary>
    /// Применяет блокировку (внутри транзакции под блокировкой <see cref="UsersToLockAsync"/>, сохраняет вызывающий). Не действующий аккаунт: 400 <c>user_not_active</c>,
    /// служебный: 400 <c>staff_cannot_be_blocked</c>. <paramref name="source"/> — <c>manual</c> или <c>ban_request</c> (в аудит).
    /// </summary>
    public async Task<BlockOutcome> ApplyAsync(Guid actorId, Guid userId, DateTime now, string source, string? reason, Guid? banRequestId, CancellationToken ct)
    {
        var open = await _exchanges.FindOpenForUserForUpdateAsync(userId, ct);   // перечитываем уже под блокировкой
        var user = await _profiles.FindUserForUpdateAsync(userId, ct) ?? throw new Exceptions.NotFoundException("user_not_found", "Пользователь не найден.");
        user.Block();
        await _refreshTokens.RevokeAllForUserAsync(user.Id, now, ct);

        foreach (var pending in await _requests.FindPendingForUserForUpdateAsync(user.Id, ct))
        {
            pending.Expire(now);   // как при блокировке пользователем: без уведомлений отправителям
        }

        var cancelled = new List<LessonSessionDto>();
        var closed = new List<(Guid Id, DateTime ClosedAt)>();
        foreach (var exchange in open)
        {
            exchange.AbortByBan(now);
            closed.Add((exchange.Id, now));
            _notify.Add(exchange.PartnerOf(user.Id), NotificationType.ExchangeAborted, new { exchangeId = exchange.Id, reason = ExchangeService.PartnerUnavailable });
            cancelled.AddRange(await _lessons.CancelOpenAsync(exchange.Id, exchange.UserAId, exchange.UserBId, ct));
        }

        // Запросы на привязку этого аккаунта и его приглашения больше не должны работать; письма получателям не уходят.
        var revoked = await _pending.RevokeAsync(actorId, user.Id, now, PendingStaffRequestsRevoker.AccountBlockedReason, ct);
        _audit.Add(new AuditEntry(actorId, StaffAuditActions.UserBlocked, "User", user.Id, new { source, reason, banRequestId, exchangesAborted = open.Count, invitationsRevoked = revoked.Invitations, linkRequestsCancelled = revoked.LinkRequests }));
        return new BlockOutcome(user.Id, user.Email, user.UiLanguage, user.Name, cancelled, closed, open.Count);
    }

    /// <summary>После коммита: уведомления, письмо блокированному (без деталей), отключение SignalR, карточки уроков, приглашения на отзыв прерванных обменов.</summary>
    public async Task FinishAsync(BlockOutcome outcome, CancellationToken ct)
    {
        _notify.Dispatch();
        if (outcome.Email is { Length: > 0 } email)
        {
            var (subject, body) = EmailTemplates.AccountBlocked(outcome.Language, outcome.Name);
            await _mail.SendAsync(email, subject, body, CancellationToken.None);
        }

        await _disconnector.DisconnectUserAsync(outcome.UserId, ct);
        await _lessons.PublishUpdatesAsync(outcome.CancelledLessons);
        foreach (var (id, at) in outcome.ClosedExchanges)
        {
            _reviewScheduler.OnExchangeClosed(id, at);
        }
    }
}
