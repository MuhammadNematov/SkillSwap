using Microsoft.EntityFrameworkCore;
using SkillSwap.Application.Abstractions;
using SkillSwap.Domain.Entities;

namespace SkillSwap.Infrastructure.Persistence;

public sealed class UserActivityStore : IUserActivityStore
{
    private readonly AppDbContext _db;

    public UserActivityStore(AppDbContext db)
    {
        _db = db;
    }

    public Task TouchAsync(Guid userId, DateTime nowUtc, CancellationToken ct = default) =>
        _db.Set<User>()
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.LastActiveAt, (DateTime?)nowUtc), ct);
}
