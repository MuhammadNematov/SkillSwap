namespace SkillSwap.Application.Abstractions;

/// <summary>
/// Отметка активности пользователя (<c>Users.LastActiveAt</c>, нужна для сортировки в подборе, раздел 5.3 ТЗ).
/// Пишется точечным UPDATE, а не через <c>UserManager.UpdateAsync</c>: тот перезаписывает всю строку и
/// при параллельных запросах конфликтует по ConcurrencyStamp, из-за чего падает весь вход.
/// </summary>
public interface IUserActivityStore
{
    Task TouchAsync(Guid userId, DateTime nowUtc, CancellationToken ct = default);
}
