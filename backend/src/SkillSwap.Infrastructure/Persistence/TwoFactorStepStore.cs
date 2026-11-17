using Microsoft.EntityFrameworkCore;
using SkillSwap.Application.Abstractions;
using SkillSwap.Domain.Entities;

namespace SkillSwap.Infrastructure.Persistence;

public sealed class TwoFactorStepStore : ITwoFactorStepStore
{
    private readonly AppDbContext _db;

    public TwoFactorStepStore(AppDbContext db)
    {
        _db = db;
    }

    public async Task<bool> TryUseStepAsync(Guid userId, long step, CancellationToken ct = default)
    {
        // Шаг записывается только вперёд и одним UPDATE: два параллельных запроса с одним кодом
        // не пройдут оба, а старый код после нового не пройдёт никогда.
        var updated = await _db.Set<User>()
            .Where(u => u.Id == userId && (u.TwoFactorLastUsedStep == null || u.TwoFactorLastUsedStep < step))
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.TwoFactorLastUsedStep, (long?)step), ct);

        return updated == 1;
    }

    public Task ResetAsync(Guid userId, CancellationToken ct = default) =>
        _db.Set<User>()
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.TwoFactorLastUsedStep, (long?)null), ct);
}
