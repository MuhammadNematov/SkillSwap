using FluentValidation;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Chat;
using SkillSwap.Application.Common;
using SkillSwap.Application.Exceptions;
using SkillSwap.Application.Notifications;
using SkillSwap.Application.Profile;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Exchanges;

/// <summary>
/// Обмены (разделы 6.1, 9.2 и 12 ТЗ): список, карточка, «Завершить», выход с причиной, продление. Обмен создаётся при принятии заявки
/// (<c>ExchangeRequestService</c>). Доступ только у участников; чужой обмен выглядит как несуществующий (404).
/// Любое изменение идёт в транзакции под блокировкой обоих участников.
/// </summary>
public sealed class ExchangeService
{
    private readonly IExchangeStore _exchanges;
    private readonly IMessageStore _messages;
    private readonly IProfileStore _profiles;
    private readonly IUnitOfWork _uow;
    private readonly IBackgroundJobs _jobs;
    private readonly INotificationPublisher _notify;
    private readonly IValidator<AbortExchangeRequest> _abortValidator;
    private readonly TimeProvider _clock;
    private readonly Files.AvatarUrls _avatars;

    public ExchangeService(
        IExchangeStore exchanges, IMessageStore messages, IProfileStore profiles, IUnitOfWork uow, IBackgroundJobs jobs,
        IValidator<AbortExchangeRequest> abortValidator, INotificationPublisher notify, TimeProvider clock, Files.AvatarUrls avatars)
    {
        _exchanges = exchanges;
        _messages = messages;
        _profiles = profiles;
        _uow = uow;
        _jobs = jobs;
        _notify = notify;
        _abortValidator = abortValidator;
        _clock = clock;
        _avatars = avatars;
    }

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;

    public async Task<PagedResult<ExchangeDto>> ListAsync(Guid userId, ExchangeStatus? status, int page, int pageSize, CancellationToken ct)
    {
        Paging.Validate(page, pageSize);
        await RequireActiveRegularAsync(userId, ct);
        var (items, total) = await _exchanges.ListAsync(userId, status, page, pageSize, ct);
        var unread = await _messages.CountUnreadAsync(userId, items.Select(r => r.Id).ToList(), ct);
        var dtos = items.Select(r => Map(r, userId, unread.GetValueOrDefault(r.Id))).ToList();
        var urls = await _avatars.ForViewerAsync(userId, dtos.Select(d => (d.Partner.Id, d.Partner.AvatarUrl)), ct);
        return new PagedResult<ExchangeDto>(dtos.Select(d => d with { Partner = d.Partner with { AvatarUrl = urls.GetValueOrDefault(d.Partner.Id) } }).ToList(), page, pageSize, total);
    }

    public async Task<ExchangeDto> GetAsync(Guid userId, Guid exchangeId, CancellationToken ct)
    {
        await RequireActiveRegularAsync(userId, ct);
        return await ViewAsync(userId, exchangeId, ct);
    }

    /// <summary>«Завершить»: обмен завершается, когда нажали оба. Повторное нажатие не ошибка.</summary>
    public async Task<ExchangeDto> CompleteAsync(Guid userId, Guid exchangeId, CancellationToken ct)
    {
        var me = await RequireActiveRegularAsync(userId, ct);
        await Mutate(userId, exchangeId, ct, e =>
        {
            var alreadyPressed = userId == e.UserAId ? e.CompletedByA : e.CompletedByB;
            if (e.MarkCompleted(userId, Now()))
            {
                _notify.Add(e.UserAId, NotificationType.ExchangeCompleted, new { exchangeId = e.Id });
                _notify.Add(e.UserBId, NotificationType.ExchangeCompleted, new { exchangeId = e.Id });
            }
            else if (!alreadyPressed)
            {
                _notify.Add(e.PartnerOf(userId), NotificationType.ExchangeFinishRequested, new { exchangeId = e.Id, userId, userName = me.Name });
            }
        });
        _notify.Dispatch();
        return await ViewAsync(userId, exchangeId, ct);
    }

    /// <summary>Выйти досрочно с причиной. Запланированные уроки отменяются (когда появятся в шаге 7).</summary>
    public async Task<ExchangeDto> AbortAsync(Guid userId, Guid exchangeId, AbortExchangeRequest? request, CancellationToken ct)
    {
        var me = await RequireActiveRegularAsync(userId, ct);
        await _abortValidator.ValidateAndThrowAsync(request ?? new AbortExchangeRequest(null), ct);
        await Mutate(userId, exchangeId, ct, e =>
        {
            e.Abort(userId, request!.Reason, Now());
            _notify.Add(e.PartnerOf(userId), NotificationType.ExchangeAborted, new { exchangeId = e.Id, userId, userName = me.Name, reason = e.AbortReason });
        });
        _notify.Dispatch();
        return await ViewAsync(userId, exchangeId, ct);
    }

    /// <summary>Согласиться на продление на 30 дней. Применяется, когда согласны оба; продление одно за обмен.</summary>
    public async Task<ExchangeDto> ExtendAsync(Guid userId, Guid exchangeId, CancellationToken ct)
    {
        var me = await RequireActiveRegularAsync(userId, ct);
        var applied = false;
        await Mutate(userId, exchangeId, ct, e =>
        {
            var alreadyAgreed = userId == e.UserAId ? e.ExtensionAcceptedByA : e.ExtensionAcceptedByB;
            applied = e.AcceptExtension(userId);
            if (applied)
            {
                _notify.Add(e.UserAId, NotificationType.ExchangeExtended, new { exchangeId = e.Id, endDate = e.EndDate });
                _notify.Add(e.UserBId, NotificationType.ExchangeExtended, new { exchangeId = e.Id, endDate = e.EndDate });
            }
            else if (!alreadyAgreed)
            {
                _notify.Add(e.PartnerOf(userId), NotificationType.ExtensionProposed, new { exchangeId = e.Id, userId, userName = me.Name });
            }
        });
        _notify.Dispatch();

        var view = await ViewAsync(userId, exchangeId, ct);
        if (applied)
        {
            // Новый срок: старая задача по прежнему сроку проснётся раньше и ничего не сделает.
            _jobs.Schedule<ExchangeJobs>(j => j.CompleteDueAsync(exchangeId), new DateTimeOffset(view.EndDate, TimeSpan.Zero));
        }

        return view;
    }

    private async Task<ExchangeDto> ViewAsync(Guid userId, Guid exchangeId, CancellationToken ct)
    {
        var row = await _exchanges.GetViewAsync(exchangeId, ct);
        if (row is null || (row.UserA.Id != userId && row.UserB.Id != userId))
        {
            throw new NotFoundException("exchange_not_found", "Обмен не найден.");
        }

        var unread = await _messages.CountUnreadAsync(userId, [row.Id], ct);
        var dto = Map(row, userId, unread.GetValueOrDefault(row.Id));
        return await WithAvatarAsync(userId, dto, ct);
    }

    private async Task Mutate(Guid userId, Guid exchangeId, CancellationToken ct, Action<Exchange> change)
    {
        var peek = await _exchanges.GetViewAsync(exchangeId, ct);
        if (peek is null || (peek.UserA.Id != userId && peek.UserB.Id != userId))
        {
            throw new NotFoundException("exchange_not_found", "Обмен не найден.");
        }

        await _uow.ExecuteAsync(async token =>
        {
            await _uow.LockUsersAsync([peek.UserA.Id, peek.UserB.Id], token);
            var exchange = await _exchanges.FindAsync(exchangeId, token) ?? throw new NotFoundException("exchange_not_found", "Обмен не найден.");
            change(exchange);
            await _exchanges.SaveAsync(token);
            return 0;
        }, ct);
    }

    private async Task<User> RequireActiveRegularAsync(Guid userId, CancellationToken ct)
    {
        var user = await _profiles.FindUserAsync(userId, ct) ?? throw new NotFoundException("user_not_found", "Пользователь не найден.");
        if (!user.IsRegular)
        {
            throw new ForbiddenException("regular_account_required", "Обмены доступны только обычным аккаунтам.");
        }

        if (user.Status != UserStatus.Active)
        {
            throw new ForbiddenException("account_not_active", "Аккаунт недоступен.");
        }

        return user;
    }

    /// <summary>
    /// Причина прерывания для участника. Блокировку знает только тот, кто заблокировал (<c>blocked</c>); заблокированному показывается
    /// нейтральная <c>partner_unavailable</c>, чтобы факт блокировки не раскрывался. В базе остаётся настоящая причина.
    /// </summary>
    private static string? AbortReasonFor(ExchangeRow r, Guid viewerId) =>
        r.AbortReason == Exchange.AbortReasonBlocked && r.BlockedById != viewerId ? PartnerUnavailable : r.AbortReason;

    public const string PartnerUnavailable = "partner_unavailable";

    private async Task<ExchangeDto> WithAvatarAsync(Guid viewerId, ExchangeDto dto, CancellationToken ct)
    {
        var urls = await _avatars.ForViewerAsync(viewerId, [(dto.Partner.Id, dto.Partner.AvatarUrl)], ct);
        return dto with { Partner = dto.Partner with { AvatarUrl = urls.GetValueOrDefault(dto.Partner.Id) } };
    }

    private ExchangeDto Map(ExchangeRow r, Guid viewerId, int unread)
    {

        var isA = r.UserA.Id == viewerId;
        var abortedBy = r.AbortedById is null ? null : r.AbortedById == viewerId ? "me" : "partner";

        // Баланс считается по проведённым урокам; уроки появятся в шаге 7, до него счётчики нулевые.
        return new ExchangeDto(
            r.Id, r.Status, isA ? r.UserB : r.UserA,
            isA ? r.SkillA : r.SkillB, isA ? r.SkillB : r.SkillA,
            r.RequestId, r.StartDate, r.EndDate, r.IsExtended,
            isA ? r.CompletedByA : r.CompletedByB, isA ? r.CompletedByB : r.CompletedByA,
            isA ? r.ExtensionAcceptedByA : r.ExtensionAcceptedByB, isA ? r.ExtensionAcceptedByB : r.ExtensionAcceptedByA,
            r.FrozenAt, abortedBy, AbortReasonFor(r, viewerId), r.ClosedAt, ExchangeBalance.Calculate(0, 0), r.CreatedAt,
            ChatAccess.Evaluate(r.Status, r.ClosedAt, Now()), unread);
    }
}
