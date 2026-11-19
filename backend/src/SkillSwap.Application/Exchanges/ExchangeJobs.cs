using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Notifications;

namespace SkillSwap.Application.Exchanges;

/// <summary>Фоновые задачи обменов. Метод идемпотентен: повторный запуск и запуск до срока ничего не меняют.</summary>
public sealed class ExchangeJobs
{
    private readonly IExchangeStore _exchanges;
    private readonly IUnitOfWork _uow;
    private readonly INotificationPublisher _notify;
    private readonly Sessions.LessonSessionService _lessons;
    private readonly TimeProvider _clock;

    public ExchangeJobs(IExchangeStore exchanges, IUnitOfWork uow, INotificationPublisher notify, TimeProvider clock, Sessions.LessonSessionService lessons)
    {
        _exchanges = exchanges;
        _uow = uow;
        _notify = notify;
        _lessons = lessons;
        _clock = clock;
    }

    /// <summary>По истечении <c>EndDate</c> активный обмен завершается. Замороженный и закрытый не трогаются; продлённый ждёт нового срока.</summary>
    public async Task CompleteDueAsync(Guid exchangeId)
    {
        var peek = await _exchanges.GetViewAsync(exchangeId, CancellationToken.None);
        if (peek is null || peek.Status != Domain.Enums.ExchangeStatus.Active)
        {
            return;
        }

        var cancelled = new List<Sessions.LessonSessionDto>();
        await _uow.ExecuteAsync(async ct =>
        {
            await _uow.LockUsersAsync([peek.UserA.Id, peek.UserB.Id], ct);
            var exchange = await _exchanges.FindAsync(exchangeId, ct);
            if (exchange is not null && exchange.CompleteIfDue(_clock.GetUtcNow().UtcDateTime))
            {
                _notify.Add(exchange.UserAId, Domain.Enums.NotificationType.ExchangeCompleted, new { exchangeId });
                _notify.Add(exchange.UserBId, Domain.Enums.NotificationType.ExchangeCompleted, new { exchangeId });
                cancelled = [.. await _lessons.CancelOpenAsync(exchangeId, exchange.UserAId, exchange.UserBId, ct)];   // открытые уроки отменяются без виновного
                await _exchanges.SaveAsync(ct);
            }

            return 0;
        }, CancellationToken.None);

        _notify.Dispatch();
        await _lessons.PublishUpdatesAsync(cancelled);
    }
}
