using Microsoft.EntityFrameworkCore;
using SkillSwap.Application.Abstractions;
using SkillSwap.Domain.Entities;

namespace SkillSwap.Infrastructure.Persistence;

public sealed class RecoveryCodeStore : IRecoveryCodeStore
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    public RecoveryCodeStore(AppDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task ReplaceAsync(Guid userId, IReadOnlyCollection<string> codeHashes, CancellationToken ct = default)
    {
        // Старые коды удаляются и новые добавляются в одной транзакции: не бывает момента без кодов
        // или с двумя наборами сразу.
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        await _db.Set<TwoFactorRecoveryCode>().Where(c => c.UserId == userId).ExecuteDeleteAsync(ct);
        _db.Set<TwoFactorRecoveryCode>().AddRange(codeHashes.Select(h => TwoFactorRecoveryCode.Create(userId, h)));
        await _db.SaveChangesAsync(ct);

        await transaction.CommitAsync(ct);
    }

    public async Task<bool> TryRedeemAsync(Guid userId, string codeHash, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;

        // Один UPDATE с условием «ещё не использован»: из двух параллельных запросов затронет строку только один.
        var updated = await _db.Set<TwoFactorRecoveryCode>()
            .Where(c => c.UserId == userId && c.CodeHash == codeHash && c.UsedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.UsedAt, now)
                .SetProperty(c => c.UpdatedAt, now), ct);

        return updated == 1;
    }

    public Task DeleteAllAsync(Guid userId, CancellationToken ct = default) =>
        _db.Set<TwoFactorRecoveryCode>().Where(c => c.UserId == userId).ExecuteDeleteAsync(ct);
}
