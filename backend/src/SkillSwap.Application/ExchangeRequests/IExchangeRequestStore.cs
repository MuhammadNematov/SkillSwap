using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.ExchangeRequests;

public interface IExchangeRequestStore
{
    void Add(ExchangeRequest request);

    /// <summary>Заявка для изменения (с отслеживанием). Менять только под блокировкой обоих участников.</summary>
    Task<ExchangeRequest?> FindAsync(Guid id, CancellationToken ct);

    Task SaveAsync(CancellationToken ct);

    /// <summary>Ожидающие и не истекшие по сроку исходящие заявки пользователя.</summary>
    Task<int> CountOpenOutgoingAsync(Guid userId, DateTime now, CancellationToken ct);

    /// <summary>Сколько заявок пользователь создал начиная с <paramref name="since"/> (любого статуса).</summary>
    Task<int> CountCreatedSinceAsync(Guid userId, DateTime since, CancellationToken ct);

    /// <summary>Открытая заявка между двумя пользователями в любую сторону.</summary>
    Task<ExchangeRequest?> FindOpenBetweenAsync(Guid userA, Guid userB, DateTime now, CancellationToken ct);

    /// <summary>Когда <paramref name="fromUserId"/> в последний раз получил отказ от <paramref name="toUserId"/>.</summary>
    Task<DateTime?> LastDeclinedAtAsync(Guid fromUserId, Guid toUserId, CancellationToken ct);

    Task<ExchangeRequestRow?> GetViewAsync(Guid id, CancellationToken ct);

    /// <summary>Заявки пользователя: входящие (<c>incoming</c>) или исходящие, новые сверху.</summary>
    Task<(IReadOnlyList<ExchangeRequestRow> Items, int Total)> ListAsync(
        Guid userId, bool incoming, ExchangeRequestStatus? status, int page, int pageSize, CancellationToken ct);
}
