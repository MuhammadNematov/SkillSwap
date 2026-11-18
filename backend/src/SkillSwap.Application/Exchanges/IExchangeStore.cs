using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Exchanges;

public interface IExchangeStore
{
    void Add(Exchange exchange);

    /// <summary>Обмен для изменения (с отслеживанием). Менять только под блокировкой обоих участников.</summary>
    Task<Exchange?> FindAsync(Guid id, CancellationToken ct);

    Task SaveAsync(CancellationToken ct);

    /// <summary>Сколько у пользователя обменов, занимающих место в лимите: активные и замороженные.</summary>
    Task<int> CountActiveAsync(Guid userId, CancellationToken ct);

    /// <summary>Есть ли между двумя людьми активный или замороженный обмен.</summary>
    Task<bool> HasActiveBetweenAsync(Guid userA, Guid userB, CancellationToken ct);

    /// <summary>Активные и замороженные обмены пары (для блокировки).</summary>
    Task<IReadOnlyList<Exchange>> FindActiveBetweenForUpdateAsync(Guid userA, Guid userB, CancellationToken ct);

    Task<ExchangeRow?> GetViewAsync(Guid id, CancellationToken ct);

    /// <summary>Обмены, в которых участвует пользователь: новые сверху.</summary>
    Task<(IReadOnlyList<ExchangeRow> Items, int Total)> ListAsync(Guid userId, ExchangeStatus? status, int page, int pageSize, CancellationToken ct);
}
