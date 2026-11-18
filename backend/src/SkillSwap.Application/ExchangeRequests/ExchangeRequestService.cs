using FluentValidation;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Blocks;
using SkillSwap.Application.Common;
using SkillSwap.Application.Exchanges;
using SkillSwap.Application.Exceptions;
using SkillSwap.Application.Matching;
using SkillSwap.Application.Notifications;
using SkillSwap.Application.Profile;
using SkillSwap.Application.Settings;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.ExchangeRequests;

/// <summary>
/// Заявки на обмен (разделы 5.3, 9.1 и 12 ТЗ): отправить, принять, отклонить, отозвать, списки «входящие» и «исходящие».
/// Любое изменение идёт в транзакции под блокировкой строк обоих участников (по возрастанию Id), проверки внутри блокировки:
/// два параллельных запроса не обходят лимиты и не принимают одну заявку дважды.
/// </summary>
public sealed class ExchangeRequestService
{
    private const int DefaultMaxPending = 5;
    private const int DefaultMaxPerDay = 10;
    private const int DefaultCooldownDays = 30;
    private const int DefaultMaxActiveExchanges = 3;

    /// <summary>409 <c>exchange_limit_reached</c>; <c>limitReachedFor</c>: you — у меня, partner — у партнёра.</summary>
    private static ConflictException LimitReached(int max, string who) => new(
        "exchange_limit_reached",
        who == "you"
            ? $"Достигнут лимит активных обменов ({max}). Завершите один из них."
            : $"У собеседника достигнут лимит активных обменов ({max}): принять заявку сейчас нельзя.",
        new Dictionary<string, object?> { ["limitReachedFor"] = who });

    private readonly IExchangeRequestStore _requests;
    private readonly IExchangeStore _exchanges;
    private readonly IProfileStore _profiles;
    private readonly IUserSkillStore _skills;
    private readonly IUserBlockStore _blocks;
    private readonly ISystemSettingsStore _settings;
    private readonly IUnitOfWork _uow;
    private readonly IBackgroundJobs _jobs;
    private readonly INotificationPublisher _notify;
    private readonly IValidator<CreateExchangeRequestRequest> _validator;
    private readonly TimeProvider _clock;

    public ExchangeRequestService(
        IExchangeRequestStore requests, IExchangeStore exchanges, IProfileStore profiles, IUserSkillStore skills, IUserBlockStore blocks,
        ISystemSettingsStore settings, IUnitOfWork uow, IBackgroundJobs jobs, IValidator<CreateExchangeRequestRequest> validator, INotificationPublisher notify,
        TimeProvider clock)
    {
        _requests = requests;
        _exchanges = exchanges;
        _profiles = profiles;
        _skills = skills;
        _blocks = blocks;
        _settings = settings;
        _uow = uow;
        _jobs = jobs;
        _notify = notify;
        _validator = validator;
        _clock = clock;
    }

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;

    public async Task<ExchangeRequestDto> CreateAsync(Guid fromId, CreateExchangeRequestRequest request, CancellationToken ct)
    {
        await _validator.ValidateAndThrowAsync(request, ct);

        var me = await RequireActiveRegularAsync(fromId, ct);
        if (request.ToUserId == fromId)
        {
            throw new BadRequestException("cannot_request_self", "Нельзя отправить заявку самому себе.");
        }

        var created = await _uow.ExecuteAsync(async token =>
        {
            await _uow.LockUsersAsync([fromId, request.ToUserId], token);
            var now = Now();

            // Получатель «не существует», если он скрыт, удалён, служебный, заблокирован в любую сторону или из другой возрастной группы:
            // ответ тот же 404, что и у профиля, поэтому заблокированный не узнаёт о блокировке.
            var target = await _profiles.FindUserAsync(request.ToUserId, token);
            if (target is null || !target.IsRegular || target.Status != UserStatus.Active || !target.IsSearchable
                || await _blocks.IsBlockedEitherWayAsync(fromId, target.Id, token))
            {
                throw new NotFoundException("user_not_found", "Пользователь не найден.");
            }

            var restrictByAge = await _settings.GetBoolAsync(SystemSettingKeys.MinorsMatchOnlyMinors, defaultValue: true, token);
            if (!AgeGroups.CanSeeEachOther(me, target, restrictByAge, DateOnly.FromDateTime(now)))
            {
                throw new NotFoundException("user_not_found", "Пользователь не найден.");
            }

            if (!await _skills.IsSkillsCompleteAsync(fromId, token))
            {
                throw new ForbiddenException("skills_incomplete", "Сначала заполните анкету: навыки «Умею» и «Хочу изучать» и язык занятий.");
            }

            var theirTeach = await TeachSkillIdsAsync(target.Id, token);
            if (!theirTeach.Contains(request.ToTeachesSkillId))
            {
                throw new BadRequestException("skill_not_taught_by_recipient", "Получатель не преподаёт этот навык.");
            }

            if (request.FromTeachesSkillId is { } mine && !(await TeachSkillIdsAsync(fromId, token)).Contains(mine))
            {
                throw new BadRequestException("skill_not_taught_by_sender", "Этого навыка нет в вашем списке «Умею».");
            }

            if (await _requests.FindOpenBetweenAsync(fromId, target.Id, now, token) is { } open)
            {
                var mineOpen = open.FromUserId == fromId;
                throw new ConflictException(
                    mineOpen ? "request_already_pending" : "reverse_request_pending",
                    mineOpen ? "Ваша заявка этому человеку уже ждёт ответа." : "Этот человек уже прислал вам заявку: ответьте на неё.",
                    new Dictionary<string, object?> { ["requestId"] = open.Id });
            }

            // Между двумя людьми одновременно один обмен, и у отправителя не больше лимита активных обменов
            // (у получателя лимит проверяется при принятии).
            if (await _exchanges.HasActiveBetweenAsync(fromId, target.Id, token))
            {
                throw new ConflictException("exchange_already_active", "У вас уже есть активный обмен с этим человеком.");
            }

            var maxActive = await _settings.GetIntAsync(SystemSettingKeys.MaxActiveExchanges, DefaultMaxActiveExchanges, token);
            if (await _exchanges.CountActiveAsync(fromId, token) >= maxActive)
            {
                throw LimitReached(maxActive, "you");
            }

            var cooldownDays = await _settings.GetIntAsync(SystemSettingKeys.DeclineCooldownDays, DefaultCooldownDays, token);
            if (await _requests.LastDeclinedAtAsync(fromId, target.Id, token) is { } declinedAt && declinedAt.AddDays(cooldownDays) > now)
            {
                throw new ConflictException(
                    "request_cooldown",
                    $"Этот человек отклонил вашу заявку. Повторить можно через {cooldownDays} дней после отказа.",
                    new Dictionary<string, object?> { ["retryAfter"] = declinedAt.AddDays(cooldownDays) });
            }

            var maxPending = await _settings.GetIntAsync(SystemSettingKeys.MaxPendingOutgoing, DefaultMaxPending, token);
            if (await _requests.CountOpenOutgoingAsync(fromId, now, token) >= maxPending)
            {
                throw new ConflictException("too_many_pending_requests", $"Одновременно можно ждать ответа не больше чем на {maxPending} заявок.");
            }

            var maxPerDay = await _settings.GetIntAsync(SystemSettingKeys.MaxRequestsPerDay, DefaultMaxPerDay, token);
            if (await _requests.CountCreatedSinceAsync(fromId, now.AddDays(-1), token) >= maxPerDay)
            {
                throw new TooManyRequestsException("daily_request_limit", $"Не больше {maxPerDay} новых заявок за сутки. Попробуйте завтра.");
            }

            var entity = ExchangeRequest.Create(
                fromId, target.Id, request.FromTeachesSkillId, request.ToTeachesSkillId, request.DurationDays, request.Message, now);
            _requests.Add(entity);
            _notify.Add(target.Id, NotificationType.RequestReceived, new { requestId = entity.Id, userId = fromId, userName = me.Name, durationDays = request.DurationDays });
            await _requests.SaveAsync(token);
            return (entity.Id, entity.ExpiresAt);
        }, ct);

        // После фиксации транзакции: срок заявки 7 дней, по его истечении задача переведёт её в Expired.
        var id = created.Item1;
        _notify.Dispatch();
        _jobs.Schedule<ExchangeRequestJobs>(j => j.ExpireAsync(id), new DateTimeOffset(created.Item2, TimeSpan.Zero));

        return await GetAsync(fromId, id, ct);
    }

    /// <summary>Принять. Если отправитель не указал свой навык, <c>FromTeachesSkillId</c> обязателен. Только получатель.</summary>
    public async Task<ExchangeRequestDto> AcceptAsync(Guid userId, Guid requestId, AcceptExchangeRequestRequest? body, CancellationToken ct)
    {
        var me = await RequireActiveRegularAsync(userId, ct);
        var expired = false;
        (Guid Id, DateTime EndDate)? createdExchange = null;

        await Mutate(userId, requestId, ct, async (request, token) =>
        {
            if (request.ToUserId != userId)
            {
                throw new ForbiddenException("not_request_recipient", "Принять заявку может только её получатель.");
            }

            var now = Now();
            if (request.Status != ExchangeRequestStatus.Pending)
            {
                request.Accept(null, now);   // бросит 409 invalid_state_transition
                return;
            }

            if (now >= request.ExpiresAt)
            {
                request.Expire(now);   // срок вышел, а задача ещё не успела: фиксируем и отвечаем «истекла»
                _notify.Add(request.FromUserId, NotificationType.RequestExpired, new { requestId = request.Id, userId, userName = me.Name });
                expired = true;
                return;
            }

            if (await _blocks.IsBlockedEitherWayAsync(request.FromUserId, request.ToUserId, token))
            {
                throw new NotFoundException("request_not_found", "Заявка не найдена.");
            }

            var chosen = body?.FromTeachesSkillId;
            if (chosen is { } skill && !(await TeachSkillIdsAsync(request.FromUserId, token)).Contains(skill))
            {
                throw new BadRequestException("skill_not_taught_by_sender", "Этого навыка нет в списке «Умею» отправителя.");
            }

            if (!(await TeachSkillIdsAsync(userId, token)).Contains(request.ToTeachesSkillId))
            {
                throw new ConflictException("skill_no_longer_taught", "Вы больше не преподаёте этот навык: заявку можно только отклонить.");
            }

            if (request.FromTeachesSkillId is { } named && !(await TeachSkillIdsAsync(request.FromUserId, token)).Contains(named))
            {
                throw new ConflictException("sender_skill_no_longer_taught", "Отправитель больше не преподаёт указанный навык: заявку можно только отклонить.");
            }

            // Для заявки без навыка отправителя выбор из «Умею» отправителя обязателен (иначе 400 from_skill_required).
            request.Accept(chosen, now);

            // Лимит активных обменов у обоих, под блокировкой обоих (она взята в Mutate). При отказе исключение откатывает транзакцию:
            // заявка остаётся Pending, обмен не создаётся.
            var max = await _settings.GetIntAsync(SystemSettingKeys.MaxActiveExchanges, DefaultMaxActiveExchanges, token);
            var mineBusy = await _exchanges.CountActiveAsync(request.ToUserId, token) >= max;
            var theirsBusy = await _exchanges.CountActiveAsync(request.FromUserId, token) >= max;
            if (mineBusy || theirsBusy)
            {
                throw LimitReached(max, mineBusy ? "you" : "partner");
            }

            // Принятие заявки и создание обмена: одна транзакция. A — отправитель, B — получатель.
            var exchange = Exchange.Create(
                request.Id, request.FromUserId, request.ToUserId, request.FromTeachesSkillId!.Value, request.ToTeachesSkillId,
                request.DurationDays, now);
            _exchanges.Add(exchange);
            createdExchange = (exchange.Id, exchange.EndDate);
            _notify.Add(request.FromUserId, NotificationType.RequestAccepted, new { requestId = request.Id, exchangeId = exchange.Id, userId, userName = me.Name });
        });

        _notify.Dispatch();
        if (expired)
        {
            throw new ConflictException("request_expired", "Срок заявки истёк.");
        }

        if (createdExchange is { } created)
        {
            // После фиксации: по истечении срока обмен завершится сам (раздел 9.2 ТЗ).
            _jobs.Schedule<ExchangeJobs>(j => j.CompleteDueAsync(created.Id), new DateTimeOffset(created.EndDate, TimeSpan.Zero));
        }

        return await GetAsync(userId, requestId, ct);
    }

    /// <summary>Отклонить. Только получатель. Отправитель сможет написать снова через настроенное число дней (по умолчанию 30).</summary>
    public async Task<ExchangeRequestDto> DeclineAsync(Guid userId, Guid requestId, CancellationToken ct)
    {
        var me = await RequireActiveRegularAsync(userId, ct);
        await Mutate(userId, requestId, ct, (request, _) =>
        {
            if (request.ToUserId != userId)
            {
                throw new ForbiddenException("not_request_recipient", "Отклонить заявку может только её получатель.");
            }

            request.Decline(Now());
            _notify.Add(request.FromUserId, NotificationType.RequestDeclined, new { requestId = request.Id, userId, userName = me.Name });
            return Task.CompletedTask;
        });
        _notify.Dispatch();
        return await GetAsync(userId, requestId, ct);
    }

    /// <summary>Отозвать. Только отправитель.</summary>
    public async Task<ExchangeRequestDto> WithdrawAsync(Guid userId, Guid requestId, CancellationToken ct)
    {
        await RequireActiveRegularAsync(userId, ct);
        await Mutate(userId, requestId, ct, (request, _) =>
        {
            if (request.FromUserId != userId)
            {
                throw new ForbiddenException("not_request_sender", "Отозвать заявку может только её отправитель.");
            }

            request.Withdraw(Now());
            return Task.CompletedTask;
        });
        return await GetAsync(userId, requestId, ct);
    }

    public async Task<ExchangeRequestDto> GetAsync(Guid userId, Guid requestId, CancellationToken ct)
    {
        var row = await _requests.GetViewAsync(requestId, ct);
        if (row is null || (row.FromUser.Id != userId && row.ToUser.Id != userId))
        {
            throw new NotFoundException("request_not_found", "Заявка не найдена.");   // чужая заявка выглядит как несуществующая
        }

        return Map(row, userId);
    }

    /// <param name="box">in — входящие, out — исходящие.</param>
    public async Task<PagedResult<ExchangeRequestDto>> ListAsync(
        Guid userId, string? box, ExchangeRequestStatus? status, int page, int pageSize, CancellationToken ct)
    {
        Paging.Validate(page, pageSize);
        if (box is not ("in" or "out"))
        {
            throw new BadRequestException("invalid_box", "box: in (входящие) или out (исходящие).");
        }

        await RequireActiveRegularAsync(userId, ct);
        var (items, total) = await _requests.ListAsync(userId, box == "in", status, page, pageSize, ct);
        return new PagedResult<ExchangeRequestDto>(items.Select(r => Map(r, userId)).ToList(), page, pageSize, total);
    }

    /// <summary>
    /// Общая часть изменения: найти заявку, заблокировать обоих участников, перечитать заявку уже под блокировкой, применить
    /// изменение, сохранить. Чужая и несуществующая заявка неотличимы (404). Исключение внутри <paramref name="change"/> откатывает транзакцию.
    /// </summary>
    private async Task Mutate(Guid userId, Guid requestId, CancellationToken ct, Func<ExchangeRequest, CancellationToken, Task> change)
    {
        var peek = await _requests.GetViewAsync(requestId, ct);
        if (peek is null || (peek.FromUser.Id != userId && peek.ToUser.Id != userId))
        {
            throw new NotFoundException("request_not_found", "Заявка не найдена.");
        }

        await _uow.ExecuteAsync(async token =>
        {
            await _uow.LockUsersAsync([peek.FromUser.Id, peek.ToUser.Id], token);
            var request = await _requests.FindAsync(requestId, token) ?? throw new NotFoundException("request_not_found", "Заявка не найдена.");
            await change(request, token);
            await _requests.SaveAsync(token);
            return 0;
        }, ct);
    }

    private async Task<HashSet<Guid>> TeachSkillIdsAsync(Guid userId, CancellationToken ct) =>
        (await _skills.ListActiveAsync(userId, ct)).Where(s => s.Direction == SkillDirection.Teach).Select(s => s.SkillId).ToHashSet();

    private async Task<User> RequireActiveRegularAsync(Guid userId, CancellationToken ct)
    {
        var user = await _profiles.FindUserAsync(userId, ct) ?? throw new NotFoundException("user_not_found", "Пользователь не найден.");
        if (!user.IsRegular)
        {
            throw new ForbiddenException("regular_account_required", "Заявки доступны только обычным аккаунтам.");
        }

        if (user.Status != UserStatus.Active)
        {
            throw new ForbiddenException("account_not_active", "Аккаунт недоступен.");
        }

        return user;
    }

    private static ExchangeRequestDto Map(ExchangeRequestRow r, Guid viewerId) => new(
        r.Id, r.ToUser.Id == viewerId ? "in" : "out", r.Status, r.FromUser, r.ToUser, r.FromTeachesSkill, r.ToTeachesSkill,
        r.DurationDays, r.Message, r.ExpiresAt, r.RespondedAt, r.CreatedAt, r.ExchangeId);
}
