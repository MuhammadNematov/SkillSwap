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

    /// <summary>Несколько обменов сразу (для календаря и истории чата).</summary>
    Task<IReadOnlyDictionary<Guid, ExchangeRow>> GetViewsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);

    /// <summary>Обмены, чат которых виден пользователю: активные, замороженные и закрытые не раньше <paramref name="closedAfter"/>.</summary>
    Task<IReadOnlyList<Guid>> ListChatExchangeIdsAsync(Guid userId, DateTime closedAfter, CancellationToken ct);

    /// <summary>Обмены, в которых участвует пользователь: новые сверху.</summary>
    Task<(IReadOnlyList<ExchangeRow> Items, int Total)> ListAsync(Guid userId, ExchangeStatus? status, int page, int pageSize, CancellationToken ct);
}
