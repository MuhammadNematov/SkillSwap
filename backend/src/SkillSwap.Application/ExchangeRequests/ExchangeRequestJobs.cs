using SkillSwap.Application.Abstractions;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.ExchangeRequests;

/// <summary>Фоновые задачи заявок. Метод идемпотентен: повторный запуск и запуск по уже закрытой заявке ничего не меняют.</summary>
public sealed class ExchangeRequestJobs
{
    private readonly IExchangeRequestStore _requests;
    private readonly IUnitOfWork _uow;
    private readonly TimeProvider _clock;

    public ExchangeRequestJobs(IExchangeRequestStore requests, IUnitOfWork uow, TimeProvider clock)
    {
        _requests = requests;
        _uow = uow;
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
                await _requests.SaveAsync(ct);
            }

            return 0;
        }, CancellationToken.None);
    }
}
