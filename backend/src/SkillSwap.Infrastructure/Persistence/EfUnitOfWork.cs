using Microsoft.EntityFrameworkCore;
using SkillSwap.Application.Abstractions;

namespace SkillSwap.Infrastructure.Persistence;

/// <summary>Транзакция и блокировка строк пользователей на EF Core и PostgreSQL.</summary>
public sealed class EfUnitOfWork : IUnitOfWork
{
    private readonly AppDbContext _db;

    public EfUnitOfWork(AppDbContext db)
    {
        _db = db;
    }

    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        if (_db.Database.CurrentTransaction is not null)
        {
            return await work(ct);   // вложенный вызов: транзакцией владеет внешний
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        var result = await work(ct);
        await transaction.CommitAsync(ct);
        return result;   // при исключении транзакция откатывается при освобождении
    }

    public async Task LockUsersAsync(IEnumerable<Guid> userIds, CancellationToken ct)
    {
        if (_db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Блокировать пользователей можно только внутри IUnitOfWork.ExecuteAsync: без транзакции блокировка снимется сразу.");
        }

        // По возрастанию Id и по одному: порядок захвата одинаков у всех запросов, поэтому взаимной блокировки (deadlock) не бывает.
        foreach (var id in userIds.Distinct().Order())
        {
            await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Users\" WHERE \"Id\" = {id} FOR UPDATE", ct);
        }
    }
}
