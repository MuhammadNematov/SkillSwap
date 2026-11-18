namespace SkillSwap.Application.Abstractions;

/// <summary>
/// Транзакция с блокировкой пользователей (раздел 10.2 и 11.1 ТЗ). Нужна там, где два запроса не должны одновременно
/// пройти проверку и обойти правило: лимит обменов, пересечение уроков.
/// </summary>
public interface IUnitOfWork
{
    /// <summary>
    /// Выполняет работу в одной транзакции: успех — фиксируем, исключение — откат. Вложенный вызов использует ту же транзакцию.
    /// </summary>
    Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct);

    /// <summary>
    /// Блокирует строки пользователей до конца транзакции (<c>SELECT … FOR UPDATE</c>). Всегда по возрастанию Id,
    /// какой бы порядок ни указали: два запроса, берущие одну и ту же пару в разном порядке, не заблокируют друг друга намертво.
    /// Вызывать только внутри <see cref="ExecuteAsync{T}"/>. Несуществующие Id пропускаются.
    /// </summary>
    Task LockUsersAsync(IEnumerable<Guid> userIds, CancellationToken ct);
}
