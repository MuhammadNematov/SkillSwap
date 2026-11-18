using FluentValidation;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Common;
using SkillSwap.Application.Exceptions;
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
    private readonly IProfileStore _profiles;
    private readonly IUnitOfWork _uow;
    private readonly IBackgroundJobs _jobs;
    private readonly IValidator<AbortExchangeRequest> _abortValidator;
    private readonly TimeProvider _clock;

    public ExchangeService(
        IExchangeStore exchanges, IProfileStore profiles, IUnitOfWork uow, IBackgroundJobs jobs,
        IValidator<AbortExchangeRequest> abortValidator, TimeProvider clock)
    {
        _exchanges = exchanges;
        _profiles = profiles;
        _uow = uow;
        _jobs = jobs;
        _abortValidator = abortValidator;
        _clock = clock;
    }

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;

    public async Task<PagedResult<ExchangeDto>> ListAsync(Guid userId, ExchangeStatus? status, int page, int pageSize, CancellationToken ct)
    {
        Paging.Validate(page, pageSize);
        await RequireActiveRegularAsync(userId, ct);
        var (items, total) = await _exchanges.ListAsync(userId, status, page, pageSize, ct);
        return new PagedResult<ExchangeDto>(items.Select(r => Map(r, userId)).ToList(), page, pageSize, total);
    }

    public async Task<ExchangeDto> GetAsync(Guid userId, Guid exchangeId, CancellationToken ct)
    {
        await RequireActiveRegularAsync(userId, ct);
        return await ViewAsync(userId, exchangeId, ct);
    }

    /// <summary>«Завершить»: обмен завершается, когда нажали оба. Повторное нажатие не ошибка.</summary>
    public async Task<ExchangeDto> CompleteAsync(Guid userId, Guid exchangeId, CancellationToken ct)
    {
        await RequireActiveRegularAsync(userId, ct);
        await Mutate(userId, exchangeId, ct, e => e.MarkCompleted(userId, Now()));
        return await ViewAsync(userId, exchangeId, ct);
    }

    /// <summary>Выйти досрочно с причиной. Запланированные уроки отменяются (когда появятся в шаге 7).</summary>
    public async Task<ExchangeDto> AbortAsync(Guid userId, Guid exchangeId, AbortExchangeRequest? request, CancellationToken ct)
    {
        await RequireActiveRegularAsync(userId, ct);
        await _abortValidator.ValidateAndThrowAsync(request ?? new AbortExchangeRequest(null), ct);
        await Mutate(userId, exchangeId, ct, e => e.Abort(userId, request!.Reason, Now()));
        return await ViewAsync(userId, exchangeId, ct);
    }

    /// <summary>Согласиться на продление на 30 дней. Применяется, когда согласны оба; продление одно за обмен.</summary>
    public async Task<ExchangeDto> ExtendAsync(Guid userId, Guid exchangeId, CancellationToken ct)
    {
        await RequireActiveRegularAsync(userId, ct);
        var applied = false;
        await Mutate(userId, exchangeId, ct, e => applied = e.AcceptExtension(userId));

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

        return Map(row, userId);
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

    private async Task RequireActiveRegularAsync(Guid userId, CancellationToken ct)
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
    }

    private static ExchangeDto Map(ExchangeRow r, Guid viewerId)
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
            r.FrozenAt, abortedBy, r.AbortReason, r.ClosedAt, ExchangeBalance.Calculate(0, 0), r.CreatedAt);
    }
}
