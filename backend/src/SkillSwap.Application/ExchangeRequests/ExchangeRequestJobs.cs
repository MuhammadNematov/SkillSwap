using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Notifications;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.ExchangeRequests;

/// <summary>Фоновые задачи заявок. Метод идемпотентен: повторный запуск и запуск по уже закрытой заявке ничего не меняют.</summary>
public sealed class ExchangeRequestJobs
{
    private readonly IExchangeRequestStore _requests;
    private readonly IUnitOfWork _uow;
    private readonly INotificationPublisher _notify;
    private readonly TimeProvider _clock;

    public ExchangeRequestJobs(IExchangeRequestStore requests, IUnitOfWork uow, INotificationPublisher notify, TimeProvider clock)
    {
        _requests = requests;
        _uow = uow;
        _notify = notify;
        _clock = clock;
    }

    /// <summary>Через 7 дней без ответа заявка становится Expired (раздел 9.1 ТЗ).</summary>
    public async Task ExpireAsync(Guid requestId)
    {
        var peek = await _requests.GetViewAsync(requestId, CancellationToken.None);
        if (peek is null || peek.Status != ExchangeRequestStatus.Pending)
        {
            return;
        }

        await _uow.ExecuteAsync(async ct =>
        {
            await _uow.LockUsersAsync([peek.FromUser.Id, peek.ToUser.Id], ct);
            var request = await _requests.FindAsync(requestId, ct);
            var now = _clock.GetUtcNow().UtcDateTime;
            if (request is { Status: ExchangeRequestStatus.Pending } && now >= request.ExpiresAt)
            {
                request.Expire(now);
                _notify.Add(request.FromUserId, NotificationType.RequestExpired, new { requestId, userId = peek.ToUser.Id, userName = peek.ToUser.Name });
                await _requests.SaveAsync(ct);
            }

            return 0;
        }, CancellationToken.None);

        _notify.Dispatch();   // после фиксации; если заявку уже закрыли, уведомлений нет
    }
}
