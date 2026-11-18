using SkillSwap.Application.Abstractions;

namespace SkillSwap.Application.Exchanges;

/// <summary>Фоновые задачи обменов. Метод идемпотентен: повторный запуск и запуск до срока ничего не меняют.</summary>
public sealed class ExchangeJobs
{
    private readonly IExchangeStore _exchanges;
    private readonly IUnitOfWork _uow;
    private readonly TimeProvider _clock;

    public ExchangeJobs(IExchangeStore exchanges, IUnitOfWork uow, TimeProvider clock)
    {
        _exchanges = exchanges;
        _uow = uow;
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

        await _uow.ExecuteAsync(async ct =>
        {
            await _uow.LockUsersAsync([peek.UserA.Id, peek.UserB.Id], ct);
            var exchange = await _exchanges.FindAsync(exchangeId, ct);
            if (exchange is not null && exchange.CompleteIfDue(_clock.GetUtcNow().UtcDateTime))
            {
                await _exchanges.SaveAsync(ct);
            }

            return 0;
        }, CancellationToken.None);
    }
}
