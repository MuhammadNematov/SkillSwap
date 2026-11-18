using SkillSwap.Application.Abstractions;
using SkillSwap.Application.ExchangeRequests;
using SkillSwap.Application.Exceptions;
using SkillSwap.Application.Exchanges;
using SkillSwap.Application.Notifications;
using SkillSwap.Application.Profile;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Blocks;

/// <summary>Блокировка пользователей (раздел 12 ТЗ): POST и DELETE /users/{id}/block, список заблокированных.</summary>
public sealed class UserBlockService
{
    private readonly IProfileStore _profiles;
    private readonly IUserBlockStore _blocks;
    private readonly IUnitOfWork _uow;
    private readonly IExchangeRequestStore _requests;
    private readonly IExchangeStore _exchanges;
    private readonly Sessions.LessonSessionService _lessons;
    private readonly INotificationPublisher _notify;
    private readonly TimeProvider _clock;

    public UserBlockService(
        IProfileStore profiles, IUserBlockStore blocks, IUnitOfWork uow, IExchangeRequestStore requests, IExchangeStore exchanges, INotificationPublisher notify, TimeProvider clock, Sessions.LessonSessionService lessons)
    {
        _profiles = profiles;
        _blocks = blocks;
        _uow = uow;
        _requests = requests;
        _exchanges = exchanges;
        _lessons = lessons;
        _notify = notify;
        _clock = clock;
    }

    public async Task BlockAsync(Guid blockerId, Guid targetId, CancellationToken ct)
    {
        await RequireActiveRegularAsync(blockerId, ct);

        if (blockerId == targetId)
        {
            throw new BadRequestException("cannot_block_self", "Нельзя заблокировать самого себя.");
        }

        // Служебные аккаунты и удалённые пользователи для обычного пользователя «не существуют».
        var target = await _profiles.FindUserAsync(targetId, ct);
        if (target is null || !target.IsRegular || target.Status == UserStatus.Deleted)
        {
            throw new NotFoundException("user_not_found", "Пользователь не найден.");
        }

        // Одна транзакция под блокировкой обоих: блокировка, закрытие ожидающих заявок между ними (Expired) и прерывание активного обмена
        // (Aborted с причиной «блокировка», без виновного). Либо всё, либо ничего.
        var cancelledSessions = new List<Sessions.LessonSessionDto>();
        var outcome = await _uow.ExecuteAsync(async token =>
        {
            await _uow.LockUsersAsync([blockerId, targetId], token);
            var added = await _blocks.AddAsync(blockerId, targetId, token);
            if (added is BlockOutcome.TooMany or BlockOutcome.TargetGone)
            {
                return added;
            }

            var now = _clock.GetUtcNow().UtcDateTime;
            foreach (var request in await _requests.FindPendingBetweenForUpdateAsync(blockerId, targetId, token))
            {
                request.Expire(now);
            }

            foreach (var exchange in await _exchanges.FindActiveBetweenForUpdateAsync(blockerId, targetId, token))
            {
                if (exchange.Status == ExchangeStatus.Active)
                {
                    exchange.AbortByBlock(blockerId, now);   // замороженный остаётся у модератора: разбирательство не прерывается блокировкой
                    _notify.Add(targetId, NotificationType.ExchangeAborted, new { exchangeId = exchange.Id, reason = ExchangeService.PartnerUnavailable });   // заблокированному — нейтральная причина; блокировавшего не уведомляем
                    cancelledSessions.AddRange(await _lessons.CancelOpenAsync(exchange.Id, exchange.UserAId, exchange.UserBId, token));
                }
            }

            await _requests.SaveAsync(token);
            return added;
        }, ct);

        _notify.Dispatch();
        await _lessons.PublishUpdatesAsync(cancelledSessions);

        switch (outcome)
        {
            case BlockOutcome.TooMany:
                throw new ConflictException("too_many_blocks", $"Можно заблокировать не больше {UserBlock.MaxPerUser} пользователей.");
            case BlockOutcome.TargetGone:
                throw new NotFoundException("user_not_found", "Пользователь не найден.");
        }
    }

    public async Task UnblockAsync(Guid blockerId, Guid targetId, CancellationToken ct)
    {
        await RequireActiveRegularAsync(blockerId, ct);
        await _blocks.RemoveAsync(blockerId, targetId, ct);
    }

    public async Task<IReadOnlyList<BlockedUserDto>> ListAsync(Guid blockerId, CancellationToken ct)
    {
        await RequireActiveRegularAsync(blockerId, ct);
        // Профиль заблокированного для блокировавшего закрыт, поэтому аватар не отдаём: ключ из хранилища наружу не уходит.
        return (await _blocks.ListAsync(blockerId, ct)).Select(b => b with { AvatarUrl = null }).ToList();
    }

    private async Task RequireActiveRegularAsync(Guid userId, CancellationToken ct)
    {
        var user = await _profiles.FindUserAsync(userId, ct) ?? throw new NotFoundException("user_not_found", "Пользователь не найден.");
        if (!user.IsRegular)
        {
            throw new ForbiddenException("regular_account_required", "Блокировки доступны только обычным аккаунтам.");
        }

        if (user.Status != UserStatus.Active)
        {
            throw new ForbiddenException("account_not_active", "Аккаунт недоступен.");
        }
    }
}
