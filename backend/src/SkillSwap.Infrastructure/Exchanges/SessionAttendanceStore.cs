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

    public void Add(SessionAttendance attendance) => _db.SessionAttendances.Add(attendance);

    public void Remove(SessionAttendance attendance) => _db.SessionAttendances.Remove(attendance);

    public Task SaveAsync(CancellationToken ct) => _db.SaveChangesAsync(ct);
}
