using Microsoft.EntityFrameworkCore;
using SkillSwap.Application.Sessions;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using SkillSwap.Infrastructure.Persistence;

namespace SkillSwap.Infrastructure.Exchanges;

public sealed class LessonSessionStore : ILessonSessionStore
{
    private static readonly SessionStatus[] Busy = [SessionStatus.Scheduled, SessionStatus.InProgress];
    private static readonly SessionStatus[] Hidden = [SessionStatus.Declined, SessionStatus.Expired, SessionStatus.Cancelled, SessionStatus.LateCancelled];

    private readonly AppDbContext _db;

    public LessonSessionStore(AppDbContext db)
    {
        _db = db;
    }

    public void Add(LessonSession session) => _db.Sessions.Add(session);

    public Task<LessonSession?> FindAsync(Guid id, CancellationToken ct) => _db.Sessions.FirstOrDefaultAsync(s => s.Id == id, ct);

    public Task SaveAsync(CancellationToken ct) => _db.SaveChangesAsync(ct);

    public async Task<Guid?> FindInCallSessionAsync(Guid exchangeId, Guid userId, CancellationToken ct) =>
        await _db.Sessions.AsNoTracking()
            .Where(s => s.ExchangeId == exchangeId && s.Status == SessionStatus.InProgress
                && _db.SessionAttendances.Any(a => a.SessionId == s.Id && a.UserId == userId && a.LeftAt == null))
            .Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyDictionary<Guid, LessonSession>> GetManyAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        ids.Count == 0
            ? new Dictionary<Guid, LessonSession>()
            : await _db.Sessions.AsNoTracking().Where(s => ids.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);

    public async Task<IReadOnlyList<LessonSession>> ListByExchangeAsync(Guid exchangeId, CancellationToken ct) =>
        await _db.Sessions.AsNoTracking().Where(s => s.ExchangeId == exchangeId).OrderByDescending(s => s.StartAt).ThenBy(s => s.Id).ToListAsync(ct);

    public async Task<IReadOnlyList<SessionRow>> ListForUserAsync(Guid userId, DateTime rangeStart, DateTime rangeEnd, bool includeClosed, CancellationToken ct)
    {
        var query = from s in _db.Sessions.AsNoTracking()
                    join e in _db.Exchanges.AsNoTracking() on s.ExchangeId equals e.Id
                    where (e.UserAId == userId || e.UserBId == userId) && s.StartAt >= rangeStart && s.StartAt < rangeEnd
                    select new { s, e.UserAId, e.UserBId };
        if (!includeClosed)
        {
            query = query.Where(x => !Hidden.Contains(x.s.Status));
        }

        var rows = await query.OrderBy(x => x.s.StartAt).ThenBy(x => x.s.Id).ToListAsync(ct);
        return rows.Select(x => new SessionRow(x.s, x.UserAId, x.UserBId)).ToList();
    }

    public async Task<IReadOnlySet<Guid>> BusyUsersAsync(IReadOnlyCollection<Guid> userIds, DateTime start, DateTime end, Guid? excludeSessionId, CancellationToken ct)
    {
        var rows = await (from s in _db.Sessions.AsNoTracking()
                          join e in _db.Exchanges.AsNoTracking() on s.ExchangeId equals e.Id
                          where Busy.Contains(s.Status) && s.StartAt < end && s.EndAt > start && (excludeSessionId == null || s.Id != excludeSessionId)
                                && (userIds.Contains(e.UserAId) || userIds.Contains(e.UserBId))
                          select new { e.UserAId, e.UserBId }).ToListAsync(ct);
        return rows.SelectMany(r => new[] { r.UserAId, r.UserBId }).Where(userIds.Contains).ToHashSet();
    }

    public Task<int> CountProposedAsync(Guid exchangeId, CancellationToken ct) =>
        _db.Sessions.CountAsync(s => s.ExchangeId == exchangeId && s.Status == SessionStatus.Proposed, ct);

    public async Task<IReadOnlyList<LessonSession>> FindOpenForExchangeAsync(Guid exchangeId, CancellationToken ct) =>
        await _db.Sessions.Where(s => s.ExchangeId == exchangeId && (s.Status == SessionStatus.Proposed || s.Status == SessionStatus.Scheduled)).ToListAsync(ct);

    public async Task<IReadOnlyDictionary<(Guid ExchangeId, Guid TeacherId), int>> CountCompletedAsync(IReadOnlyCollection<Guid> exchangeIds, CancellationToken ct)
    {
        if (exchangeIds.Count == 0)
        {
            return new Dictionary<(Guid, Guid), int>();
        }

        var rows = await _db.Sessions.AsNoTracking()
            .Where(s => exchangeIds.Contains(s.ExchangeId) && s.Status == SessionStatus.Completed)
            .GroupBy(s => new { s.ExchangeId, s.TeacherId })
            .Select(g => new { g.Key.ExchangeId, g.Key.TeacherId, Count = g.Count() })
            .ToListAsync(ct);
        return rows.ToDictionary(r => (r.ExchangeId, r.TeacherId), r => r.Count);
    }

    public async Task<IReadOnlyDictionary<(Guid ExchangeId, Guid UserId), int>> CountFinalNoShowsAsync(IReadOnlyCollection<Guid> exchangeIds, CancellationToken ct)
    {
        if (exchangeIds.Count == 0)
        {
            return new Dictionary<(Guid, Guid), int>();
        }

        var rows = await _db.Sessions.AsNoTracking()
            .Where(s => exchangeIds.Contains(s.ExchangeId) && s.Status == SessionStatus.NoShow && s.NoShowUserId != null && s.RoomClosedAt != null)
            .GroupBy(s => new { s.ExchangeId, UserId = s.NoShowUserId!.Value })
            .Select(g => new { g.Key.ExchangeId, g.Key.UserId, Count = g.Count() })
            .ToListAsync(ct);
        return rows.ToDictionary(r => (r.ExchangeId, r.UserId), r => r.Count);
    }
}
