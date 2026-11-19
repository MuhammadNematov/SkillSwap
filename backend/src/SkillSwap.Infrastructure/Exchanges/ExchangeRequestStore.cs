using Microsoft.EntityFrameworkCore;
using SkillSwap.Application.ExchangeRequests;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using SkillSwap.Infrastructure.Persistence;

namespace SkillSwap.Infrastructure.Exchanges;

public sealed class ExchangeRequestStore : IExchangeRequestStore
{
    private readonly AppDbContext _db;

    public ExchangeRequestStore(AppDbContext db)
    {
        _db = db;
    }

    public void Add(ExchangeRequest request) => _db.ExchangeRequests.Add(request);

    public Task<ExchangeRequest?> FindAsync(Guid id, CancellationToken ct) =>
        _db.ExchangeRequests.FirstOrDefaultAsync(r => r.Id == id, ct);

    public Task SaveAsync(CancellationToken ct) => _db.SaveChangesAsync(ct);

    public Task<int> CountOpenOutgoingAsync(Guid userId, DateTime now, CancellationToken ct) =>
        _db.ExchangeRequests.CountAsync(r => r.FromUserId == userId && r.Status == ExchangeRequestStatus.Pending && r.ExpiresAt > now, ct);

    public Task<int> CountCreatedSinceAsync(Guid userId, DateTime since, CancellationToken ct) =>
        _db.ExchangeRequests.CountAsync(r => r.FromUserId == userId && r.CreatedAt >= since, ct);

    public Task<ExchangeRequest?> FindOpenBetweenAsync(Guid userA, Guid userB, DateTime now, CancellationToken ct) =>
        _db.ExchangeRequests.AsNoTracking().FirstOrDefaultAsync(r =>
            r.Status == ExchangeRequestStatus.Pending && r.ExpiresAt > now &&
            ((r.FromUserId == userA && r.ToUserId == userB) || (r.FromUserId == userB && r.ToUserId == userA)), ct);

    public async Task<IReadOnlyList<ExchangeRequest>> FindPendingForUserForUpdateAsync(Guid userId, CancellationToken ct) =>
        await _db.ExchangeRequests.Where(r => r.Status == ExchangeRequestStatus.Pending && (r.FromUserId == userId || r.ToUserId == userId)).ToListAsync(ct);

    public async Task<IReadOnlyList<ExchangeRequest>> FindPendingBetweenForUpdateAsync(Guid userA, Guid userB, CancellationToken ct) =>
        await _db.ExchangeRequests.Where(r => r.Status == ExchangeRequestStatus.Pending &&
            ((r.FromUserId == userA && r.ToUserId == userB) || (r.FromUserId == userB && r.ToUserId == userA))).ToListAsync(ct);

    public async Task<DateTime?> LastDeclinedAtAsync(Guid fromUserId, Guid toUserId, CancellationToken ct) =>
        await _db.ExchangeRequests.AsNoTracking()
            .Where(r => r.FromUserId == fromUserId && r.ToUserId == toUserId && r.Status == ExchangeRequestStatus.Declined)
            .OrderByDescending(r => r.RespondedAt)
            .Select(r => r.RespondedAt)
            .FirstOrDefaultAsync(ct);

    public async Task<ExchangeRequestRow?> GetViewAsync(Guid id, CancellationToken ct)
    {
        var requests = _db.ExchangeRequests.AsNoTracking().Where(r => r.Id == id);
        return (await ToRowsAsync(requests, ct)).FirstOrDefault();
    }

    public async Task<(IReadOnlyList<ExchangeRequestRow> Items, int Total)> ListAsync(
        Guid userId, bool incoming, ExchangeRequestStatus? status, int page, int pageSize, CancellationToken ct)
    {
        var query = _db.ExchangeRequests.AsNoTracking().Where(r => incoming ? r.ToUserId == userId : r.FromUserId == userId);
        if (status is { } s)
        {
            query = query.Where(r => r.Status == s);
        }

        var total = await query.CountAsync(ct);
        var pageOfRequests = query.OrderByDescending(r => r.CreatedAt).ThenBy(r => r.Id).Skip((page - 1) * pageSize).Take(pageSize);
        return (await ToRowsAsync(pageOfRequests, ct), total);
    }

    /// <summary>
    /// Заявки вместе с именами людей и навыков. Сначала страница заявок (порядок сохраняется), затем одним запросом
    /// пользователи и одним навыки: так не нужны соединения с необязательным навыком.
    /// </summary>
    private async Task<List<ExchangeRequestRow>> ToRowsAsync(IQueryable<ExchangeRequest> requests, CancellationToken ct)
    {
        var list = await requests.ToListAsync(ct);
        if (list.Count == 0)
        {
            return [];
        }

        var userIds = list.SelectMany(r => new[] { r.FromUserId, r.ToUserId }).Distinct().ToList();
        var skillIds = list.SelectMany(r => new Guid?[] { r.FromTeachesSkillId, r.ToTeachesSkillId }).OfType<Guid>().Distinct().ToList();
        var users = await _db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id))
            .Select(u => new RequestUserDto(u.Id, u.Status == UserStatus.Deleted ? User.DeletedDisplayName : u.Name, u.Status == UserStatus.Deleted ? null : u.AvatarKey, u.Status == UserStatus.Deleted)).ToDictionaryAsync(u => u.Id, ct);
        var skills = await _db.Skills.AsNoTracking().Where(s => skillIds.Contains(s.Id))
            .Select(s => new RequestSkillDto(s.Id, s.NameTg, s.NameRu, s.NameEn)).ToDictionaryAsync(s => s.Id, ct);

        var requestIds = list.Select(r => r.Id).ToList();
        var exchangeIds = await _db.Exchanges.AsNoTracking().Where(e => requestIds.Contains(e.RequestId))
            .Select(e => new { e.RequestId, e.Id }).ToDictionaryAsync(e => e.RequestId, e => (Guid?)e.Id, ct);

        return list.Select(r => new ExchangeRequestRow(
            r.Id, r.Status, users[r.FromUserId], users[r.ToUserId],
            r.FromTeachesSkillId is { } from ? skills[from] : null,
            skills[r.ToTeachesSkillId],
            r.DurationDays, r.Message, r.ExpiresAt, r.RespondedAt, r.CreatedAt, exchangeIds.GetValueOrDefault(r.Id))).ToList();
    }
}
