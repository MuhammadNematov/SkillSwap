using Microsoft.EntityFrameworkCore;
using SkillSwap.Application.AdminUsers;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using SkillSwap.Infrastructure.Persistence;

namespace SkillSwap.Infrastructure.AdminUsers;

public sealed class AdminUserStore : IAdminUserStore
{
    private static readonly ExchangeStatus[] Occupying = [ExchangeStatus.Active, ExchangeStatus.Frozen];
    private static readonly CaseStatus[] Finished = [CaseStatus.Withdrawn, CaseStatus.Closed];
    private static readonly string[] BlockActions = ["UserBlocked", "UserUnblocked"];

    private readonly AppDbContext _db;

    public AdminUserStore(AppDbContext db)
    {
        _db = db;
    }

    public async Task<(IReadOnlyList<AdminUserRow> Items, int Total)> SearchAsync(string? search, UserStatus? status, DateTime now, int page, int pageSize, CancellationToken ct)
    {
        var query = _db.Users.AsNoTracking().Where(u => u.AccountType == AccountType.Regular);
        if (status is { } s)
        {
            query = query.Where(u => u.Status == s);
        }

        if (search is not null)
        {
            var pattern = Persistence.SearchPattern.Contains(search);
            query = query.Where(u => EF.Functions.ILike(u.Name, pattern, Persistence.SearchPattern.Escape) || (u.Email != null && EF.Functions.ILike(u.Email, pattern, Persistence.SearchPattern.Escape)));
        }

        var total = await query.CountAsync(ct);
        var users = await query.OrderBy(u => u.Name).ThenBy(u => u.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return (await WithCountersAsync(users, now, ct), total);
    }

    public async Task<AdminUserRow?> GetRowAsync(Guid id, DateTime now, CancellationToken ct)
    {
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id && u.AccountType == AccountType.Regular, ct);
        return user is null ? null : (await WithCountersAsync([user], now, ct))[0];
    }

    public async Task<IReadOnlyList<Warning>> ListWarningsAsync(Guid userId, CancellationToken ct) =>
        await _db.Warnings.AsNoTracking().Where(w => w.UserId == userId).OrderByDescending(w => w.CreatedAt).ToListAsync(ct);

    public Task<Warning?> FindWarningAsync(Guid id, CancellationToken ct) => _db.Warnings.FirstOrDefaultAsync(w => w.Id == id, ct);

    public async Task<IReadOnlyList<Case>> ListCasesAsync(Guid userId, int limit, CancellationToken ct) =>
        await _db.Cases.AsNoTracking().Where(c => c.OpenedById == userId || c.AgainstUserId == userId).OrderByDescending(c => c.CreatedAt).Take(limit).ToListAsync(ct);

    public async Task<IReadOnlyList<AuditLog>> ListBlockEventsAsync(Guid userId, int limit, CancellationToken ct) =>
        await _db.AuditLogs.AsNoTracking().Where(a => a.TargetType == "User" && a.TargetId == userId && BlockActions.Contains(a.Action))
            .OrderByDescending(a => a.CreatedAt).Take(limit).ToListAsync(ct);

    public Task<int> CountActiveExchangesAsync(Guid userId, CancellationToken ct) =>
        _db.Exchanges.CountAsync(e => (e.UserAId == userId || e.UserBId == userId) && Occupying.Contains(e.Status), ct);

    public Task SaveAsync(CancellationToken ct) => _db.SaveChangesAsync(ct);

    private async Task<IReadOnlyList<AdminUserRow>> WithCountersAsync(List<User> users, DateTime now, CancellationToken ct)
    {
        var ids = users.Select(u => u.Id).ToList();
        var warnings = await _db.Warnings.AsNoTracking().Where(w => ids.Contains(w.UserId) && w.RevokedAt == null && w.ExpiresAt > now)
            .GroupBy(w => w.UserId).Select(g => new { Id = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.Count, ct);
        var asOpener = await _db.Cases.AsNoTracking().Where(c => ids.Contains(c.OpenedById) && !Finished.Contains(c.Status))
            .GroupBy(c => c.OpenedById).Select(g => new { Id = g.Key, Count = g.Count() }).ToListAsync(ct);
        var asAgainst = await _db.Cases.AsNoTracking().Where(c => ids.Contains(c.AgainstUserId) && !Finished.Contains(c.Status))
            .GroupBy(c => c.AgainstUserId).Select(g => new { Id = g.Key, Count = g.Count() }).ToListAsync(ct);
        var cases = asOpener.Concat(asAgainst).GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.Sum(x => x.Count));
        return users.Select(u => new AdminUserRow(u, warnings.GetValueOrDefault(u.Id), cases.GetValueOrDefault(u.Id))).ToList();
    }
}
