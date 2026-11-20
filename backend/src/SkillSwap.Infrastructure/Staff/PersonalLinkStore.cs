using Microsoft.EntityFrameworkCore;
using SkillSwap.Application.Staff;
using SkillSwap.Domain.Entities;
using SkillSwap.Infrastructure.Persistence;

namespace SkillSwap.Infrastructure.Staff;

public sealed class PersonalLinkStore : IPersonalLinkStore
{
    private readonly AppDbContext _db;

    public PersonalLinkStore(AppDbContext db)
    {
        _db = db;
    }

    public void Add(PersonalLinkRequest request) => _db.PersonalLinkRequests.Add(request);

    public Task<PersonalLinkRequest?> FindByTokenHashAsync(string tokenHash, bool forUpdate, CancellationToken ct) =>
        forUpdate
            ? _db.PersonalLinkRequests.FromSqlInterpolated($"SELECT *, xmin FROM \"PersonalLinkRequests\" WHERE \"TokenHash\" = {tokenHash} FOR UPDATE").FirstOrDefaultAsync(ct)
            : _db.PersonalLinkRequests.AsNoTracking().FirstOrDefaultAsync(r => r.TokenHash == tokenHash, ct);

    public async Task<IReadOnlyList<PersonalLinkRequest>> ListOpenByStaffAsync(Guid staffId, CancellationToken ct) =>
        await _db.PersonalLinkRequests.Where(r => r.StaffId == staffId && r.ConfirmedAt == null && r.DeclinedAt == null && r.CancelledAt == null).ToListAsync(ct);

    public Task<int> CountRequestedSinceAsync(Guid staffId, DateTime since, CancellationToken ct) =>
        // Время создания в базе ставится по настоящим часам, а срок запроса по часам приложения: считаем по сроку (создан не раньше since).
        _db.PersonalLinkRequests.CountAsync(r => r.StaffId == staffId && r.ExpiresAt >= since + PersonalLinkRequest.Lifetime, ct);

    public Task<Guid?> FindStaffLinkedToAsync(Guid personalUserId, CancellationToken ct) =>
        _db.Users.AsNoTracking().Where(u => u.LinkedUserId == personalUserId).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);

    public Task SaveAsync(CancellationToken ct) => _db.SaveChangesAsync(ct);
}
