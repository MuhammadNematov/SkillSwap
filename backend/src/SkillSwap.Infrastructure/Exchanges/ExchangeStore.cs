using Microsoft.EntityFrameworkCore;
using SkillSwap.Application.ExchangeRequests;
using SkillSwap.Application.Exchanges;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using SkillSwap.Infrastructure.Persistence;

namespace SkillSwap.Infrastructure.Exchanges;

public sealed class ExchangeStore : IExchangeStore
{
    private static readonly ExchangeStatus[] Occupying = [ExchangeStatus.Active, ExchangeStatus.Frozen];

    private readonly AppDbContext _db;

    public ExchangeStore(AppDbContext db)
    {
        _db = db;
    }

    public void Add(Exchange exchange) => _db.Exchanges.Add(exchange);

    public Task<Exchange?> FindAsync(Guid id, CancellationToken ct) => _db.Exchanges.FirstOrDefaultAsync(e => e.Id == id, ct);

    public Task SaveAsync(CancellationToken ct) => _db.SaveChangesAsync(ct);

    public Task<int> CountActiveAsync(Guid userId, CancellationToken ct) =>
        _db.Exchanges.CountAsync(e => (e.UserAId == userId || e.UserBId == userId) && Occupying.Contains(e.Status), ct);

    public Task<bool> HasActiveBetweenAsync(Guid userA, Guid userB, CancellationToken ct) =>
        _db.Exchanges.AnyAsync(e => Occupying.Contains(e.Status) &&
            ((e.UserAId == userA && e.UserBId == userB) || (e.UserAId == userB && e.UserBId == userA)), ct);

    public async Task<IReadOnlyList<Exchange>> FindActiveBetweenForUpdateAsync(Guid userA, Guid userB, CancellationToken ct) =>
        await _db.Exchanges.Where(e => Occupying.Contains(e.Status) &&
            ((e.UserAId == userA && e.UserBId == userB) || (e.UserAId == userB && e.UserBId == userA))).ToListAsync(ct);

    public async Task<IReadOnlyList<Exchange>> FindOpenForUserForUpdateAsync(Guid userId, CancellationToken ct) =>
        await _db.Exchanges.Where(e => (e.UserAId == userId || e.UserBId == userId) && Occupying.Contains(e.Status)).ToListAsync(ct);

    public async Task<ExchangeRow?> GetViewAsync(Guid id, CancellationToken ct) =>
        (await ToRowsAsync(_db.Exchanges.AsNoTracking().Where(e => e.Id == id), ct)).FirstOrDefault();

    public async Task<IReadOnlyDictionary<Guid, ExchangeRow>> GetViewsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        ids.Count == 0
            ? new Dictionary<Guid, ExchangeRow>()
            : (await ToRowsAsync(_db.Exchanges.AsNoTracking().Where(e => ids.Contains(e.Id)), ct)).ToDictionary(r => r.Id);

    public async Task<IReadOnlyList<Guid>> ListChatExchangeIdsAsync(Guid userId, DateTime closedAfter, CancellationToken ct) =>
        await _db.Exchanges.AsNoTracking()
            .Where(e => (e.UserAId == userId || e.UserBId == userId) && (Occupying.Contains(e.Status) || e.ClosedAt > closedAfter))
            .Select(e => e.Id).Take(500).ToListAsync(ct);

    public async Task<(IReadOnlyList<ExchangeRow> Items, int Total)> ListAsync(
        Guid userId, ExchangeStatus? status, int page, int pageSize, CancellationToken ct)
    {
        var query = _db.Exchanges.AsNoTracking().Where(e => e.UserAId == userId || e.UserBId == userId);
        if (status is { } s)
        {
            query = query.Where(e => e.Status == s);
        }

        var total = await query.CountAsync(ct);
        var pageOf = query.OrderByDescending(e => e.CreatedAt).ThenBy(e => e.Id).Skip((page - 1) * pageSize).Take(pageSize);
        return (await ToRowsAsync(pageOf, ct), total);
    }

    private async Task<List<ExchangeRow>> ToRowsAsync(IQueryable<Exchange> exchanges, CancellationToken ct)
    {
        var list = await exchanges.ToListAsync(ct);
        if (list.Count == 0)
        {
            return [];
        }

        var userIds = list.SelectMany(e => new[] { e.UserAId, e.UserBId }).Distinct().ToList();
        var skillIds = list.SelectMany(e => new[] { e.SkillAId, e.SkillBId }).Distinct().ToList();
        var users = await _db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id))
            .Select(u => new RequestUserDto(u.Id, u.Status == UserStatus.Deleted ? User.DeletedDisplayName : u.Name, u.Status == UserStatus.Deleted ? null : u.AvatarKey, u.Status == UserStatus.Deleted)).ToDictionaryAsync(u => u.Id, ct);
        var skills = await _db.Skills.AsNoTracking().Where(s => skillIds.Contains(s.Id))
            .Select(s => new RequestSkillDto(s.Id, s.NameTg, s.NameRu, s.NameEn)).ToDictionaryAsync(s => s.Id, ct);

        return list.Select(e => new ExchangeRow(
            e.Id, e.Status, users[e.UserAId], users[e.UserBId], skills[e.SkillAId], skills[e.SkillBId], e.RequestId,
            e.StartDate, e.EndDate, e.IsExtended, e.CompletedByA, e.CompletedByB, e.ExtensionAcceptedByA, e.ExtensionAcceptedByB,
            e.FrozenAt, e.AbortedById, e.BlockedById, e.AbortReason, e.ClosedAt, e.CreatedAt, e.AbortedForNoShow)).ToList();
    }
}
