using Microsoft.EntityFrameworkCore;
using SkillSwap.Application.Staff;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using SkillSwap.Infrastructure.Persistence;

namespace SkillSwap.Infrastructure.Staff;

public sealed class StaffInvitationStore : IStaffInvitationStore
{
    private readonly AppDbContext _db;

    public StaffInvitationStore(AppDbContext db)
    {
        _db = db;
    }

    public void Add(StaffInvitation invitation) => _db.StaffInvitations.Add(invitation);

    public Task<StaffInvitation?> FindAsync(Guid id, bool tracked, CancellationToken ct) =>
        (tracked ? _db.StaffInvitations : _db.StaffInvitations.AsNoTracking()).FirstOrDefaultAsync(i => i.Id == id, ct);

    public Task<StaffInvitation?> FindByTokenHashAsync(string tokenHash, bool forUpdate, CancellationToken ct) =>
        forUpdate
            ? _db.StaffInvitations.FromSqlInterpolated($"SELECT *, xmin FROM \"StaffInvitations\" WHERE \"TokenHash\" = {tokenHash} FOR UPDATE").FirstOrDefaultAsync(ct)
            : _db.StaffInvitations.AsNoTracking().FirstOrDefaultAsync(i => i.TokenHash == tokenHash, ct);

    public Task<StaffInvitation?> FindOpenByEmailAsync(string normalizedEmail, CancellationToken ct) =>
        _db.StaffInvitations.AsNoTracking().FirstOrDefaultAsync(i => i.Email == normalizedEmail && i.AcceptedAt == null && i.RevokedAt == null, ct);

    public async Task<IReadOnlyList<StaffInvitation>> ListPendingByInviterAsync(Guid inviterId, DateTime now, CancellationToken ct) =>
        await _db.StaffInvitations.Where(i => i.InvitedById == inviterId && i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt >= now).ToListAsync(ct);

    public async Task<(IReadOnlyList<StaffInvitation> Items, int Total)> ListAsync(
        IReadOnlyCollection<UserRole> roles, StaffInvitationStatus? status, DateTime now, int page, int pageSize, CancellationToken ct)
    {
        var query = _db.StaffInvitations.AsNoTracking().Where(i => roles.Contains(i.TargetRole));
        query = status switch
        {
            StaffInvitationStatus.Accepted => query.Where(i => i.AcceptedAt != null),
            StaffInvitationStatus.Revoked => query.Where(i => i.AcceptedAt == null && i.RevokedAt != null),
            StaffInvitationStatus.Expired => query.Where(i => i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt < now),
            StaffInvitationStatus.Pending => query.Where(i => i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt >= now),
            _ => query,
        };

        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(i => i.CreatedAt).ThenBy(i => i.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return (items, total);
    }

    public Task SaveAsync(CancellationToken ct) => _db.SaveChangesAsync(ct);
}
