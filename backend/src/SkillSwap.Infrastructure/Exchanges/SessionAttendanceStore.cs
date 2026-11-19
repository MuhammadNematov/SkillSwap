using Microsoft.EntityFrameworkCore;
using SkillSwap.Application.Sessions;
using SkillSwap.Domain.Entities;
using SkillSwap.Infrastructure.Persistence;

namespace SkillSwap.Infrastructure.Exchanges;

public sealed class SessionAttendanceStore : ISessionAttendanceStore
{
    private readonly AppDbContext _db;

    public SessionAttendanceStore(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<SessionAttendance>> ListForUpdateAsync(Guid sessionId, CancellationToken ct) =>
        await _db.SessionAttendances.Where(a => a.SessionId == sessionId).OrderBy(a => a.JoinedAt).ToListAsync(ct);

    public async Task<IReadOnlyList<SessionAttendance>> ListAsync(Guid sessionId, CancellationToken ct) =>
        await _db.SessionAttendances.AsNoTracking().Where(a => a.SessionId == sessionId).OrderBy(a => a.JoinedAt).ToListAsync(ct);

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>> ListInRoomAsync(IReadOnlyCollection<Guid> sessionIds, CancellationToken ct)
    {
        if (sessionIds.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<Guid>>();
        }

        var rows = await _db.SessionAttendances.AsNoTracking()
            .Where(a => sessionIds.Contains(a.SessionId) && a.LeftAt == null)
            .Select(a => new { a.SessionId, a.UserId })
            .ToListAsync(ct);
        return rows.GroupBy(r => r.SessionId).ToDictionary(g => g.Key, g => (IReadOnlyList<Guid>)g.Select(r => r.UserId).Distinct().ToList());
    }

    public void Add(SessionAttendance attendance) => _db.SessionAttendances.Add(attendance);

    public void Remove(SessionAttendance attendance) => _db.SessionAttendances.Remove(attendance);

    public Task SaveAsync(CancellationToken ct) => _db.SaveChangesAsync(ct);
}
