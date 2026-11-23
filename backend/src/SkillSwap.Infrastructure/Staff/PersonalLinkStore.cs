using Microsoft.EntityFrameworkCore;
using SkillSwap.Application.Staff;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
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

    public async Task<IReadOnlyList<PersonalLinkRequest>> ListPendingInvolvingAsync(Guid userId, DateTime now, CancellationToken ct) =>
        await _db.PersonalLinkRequests.Where(r => (r.StaffId == userId || r.PersonalUserId == userId) && r.ConfirmedAt == null && r.DeclinedAt == null && r.CancelledAt == null && r.ExpiresAt >= now).ToListAsync(ct);

    public Task<int> CountRequestedSinceAsync(Guid staffId, DateTime since, CancellationToken ct) =>
        // Время создания в базе ставится по настоящим часам, а срок запроса по часам приложения: считаем по сроку (создан не раньше since).
        _db.PersonalLinkRequests.CountAsync(r => r.StaffId == staffId && r.ExpiresAt >= since + PersonalLinkRequest.Lifetime, ct);

    public Task<Guid?> FindStaffLinkedToAsync(Guid personalUserId, CancellationToken ct) =>
        _db.Users.AsNoTracking().Where(u => u.LinkedUserId == personalUserId).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);   // действующая привязка; снятые не мешают привязать к другому

    public void AddLink(StaffPersonalLink link) => _db.StaffPersonalLinks.Add(link);

    public Task<StaffPersonalLink?> FindActiveLinkAsync(Guid staffId, CancellationToken ct) =>
        _db.StaffPersonalLinks.FirstOrDefaultAsync(l => l.StaffId == staffId && l.UnlinkedAt == null, ct);

    public async Task<IReadOnlyList<StaffPersonalLink>> ListLinksAsync(Guid staffId, CancellationToken ct) =>
        await _db.StaffPersonalLinks.AsNoTracking().Where(l => l.StaffId == staffId).OrderByDescending(l => l.LinkedAt).ToListAsync(ct);

    public void AddUnlinkRequest(PersonalUnlinkRequest request) => _db.PersonalUnlinkRequests.Add(request);

    public Task<PersonalUnlinkRequest?> FindUnlinkRequestAsync(Guid id, bool tracked, CancellationToken ct) =>
        (tracked ? _db.PersonalUnlinkRequests : _db.PersonalUnlinkRequests.AsNoTracking()).FirstOrDefaultAsync(r => r.Id == id, ct);

    public Task<PersonalUnlinkRequest?> FindPendingUnlinkRequestAsync(Guid staffId, bool tracked, CancellationToken ct) =>
        (tracked ? _db.PersonalUnlinkRequests : _db.PersonalUnlinkRequests.AsNoTracking()).FirstOrDefaultAsync(r => r.StaffId == staffId && r.Status == UnlinkRequestStatus.Pending, ct);

    public async Task<(IReadOnlyList<PersonalUnlinkRequest> Items, int Total)> ListUnlinkRequestsAsync(UnlinkRequestStatus? status, int page, int pageSize, CancellationToken ct)
    {
        var query = _db.PersonalUnlinkRequests.AsNoTracking().AsQueryable();
        if (status is { } s)
        {
            query = query.Where(r => r.Status == s);
        }

        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(r => r.CreatedAt).ThenBy(r => r.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return (items, total);
    }

    public Task SaveAsync(CancellationToken ct) => _db.SaveChangesAsync(ct);
}
