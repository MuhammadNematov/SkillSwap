using Microsoft.EntityFrameworkCore;
using SkillSwap.Application.Abstractions;
using SkillSwap.Domain.Entities;

namespace SkillSwap.Infrastructure.Persistence;

public sealed class RefreshTokenRepository : IRefreshTokenRepository
{
    private readonly AppDbContext _db;

    public RefreshTokenRepository(AppDbContext db)
    {
        _db = db;
    }

    public Task<RefreshToken?> FindByHashAsync(string tokenHash, CancellationToken ct = default) =>
        _db.Set<RefreshToken>().FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct);

    public void Add(RefreshToken token) => _db.Set<RefreshToken>().Add(token);

    public Task RevokeAllForUserAsync(Guid userId, DateTime nowUtc, CancellationToken ct = default) =>
        _db.Set<RefreshToken>()
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.RevokedAt, nowUtc)
                .SetProperty(t => t.UpdatedAt, nowUtc), ct);

    public async Task<IReadOnlyList<RefreshToken>> ListActiveByUserAsync(Guid userId, DateTime nowUtc, CancellationToken ct = default) =>
        await _db.Set<RefreshToken>()
            .AsNoTracking()
            .Where(t => t.UserId == userId && t.RevokedAt == null && t.ExpiresAt > nowUtc)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(ct);

    public Task<int> RevokeSessionAsync(Guid userId, Guid sessionId, DateTime nowUtc, CancellationToken ct = default) =>
        _db.Set<RefreshToken>()
            .Where(t => t.UserId == userId && t.SessionId == sessionId && t.RevokedAt == null && t.ExpiresAt > nowUtc)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.RevokedAt, nowUtc)
                .SetProperty(t => t.UpdatedAt, nowUtc), ct);

    public Task<int> RevokeAllExceptSessionAsync(Guid userId, Guid keepSessionId, DateTime nowUtc, CancellationToken ct = default) =>
        _db.Set<RefreshToken>()
            .Where(t => t.UserId == userId && t.SessionId != keepSessionId && t.RevokedAt == null && t.ExpiresAt > nowUtc)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.RevokedAt, nowUtc)
                .SetProperty(t => t.UpdatedAt, nowUtc), ct);

    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}
