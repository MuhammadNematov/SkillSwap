namespace SkillSwap.Application.Blocks;

public interface IUserBlockStore
{
    /// <summary>
    /// Добавляет блокировку. Вызывать внутри транзакции под блокировкой строк обоих пользователей (<c>IUnitOfWork</c>):
    /// иначе одновременные запросы обойдут лимит, а заявки и обмены пары не успеют закрыться вместе с блокировкой.
    /// Повтор не считается ошибкой.
    /// </summary>
    Task<BlockOutcome> AddAsync(Guid blockerId, Guid targetId, CancellationToken ct);

    /// <summary>Снимает блокировку. Если её не было — ничего не происходит.</summary>
    Task RemoveAsync(Guid blockerId, Guid targetId, CancellationToken ct);

    /// <summary>Кого я заблокировал: новые сверху. Удалённые аккаунты не показываются.</summary>
    Task<IReadOnlyList<BlockedUserDto>> ListAsync(Guid blockerId, CancellationToken ct);

    /// <summary>Заблокировал ли хотя бы один из двоих другого. Нужна везде, где пользователи видят друг друга.</summary>
    Task<bool> IsBlockedEitherWayAsync(Guid userA, Guid userB, CancellationToken ct);

    /// <summary>Кто из перечисленных заблокирован мной или заблокировал меня (одним запросом).</summary>
    /// <summary>Убирает все блокировки, в которых участвует пользователь (в обе стороны): аккаунт удалён.</summary>
    Task RemoveAllForUserAsync(Guid userId, CancellationToken ct);

    Task<IReadOnlySet<Guid>> BlockedEitherWayAmongAsync(Guid userId, IReadOnlyCollection<Guid> otherIds, CancellationToken ct);
}
