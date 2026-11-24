using Microsoft.EntityFrameworkCore;
using SkillSwap.Application.Staff;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using SkillSwap.Infrastructure.Persistence;

namespace SkillSwap.Infrastructure.Staff;

public sealed class ModeratorStore : IModeratorStore
{
    private readonly AppDbContext _db;

    public ModeratorStore(AppDbContext db)
    {
        _db = db;
    }

    public async Task<(IReadOnlyList<User> Items, int Total)> SearchAsync(UserRole role, bool removed, int page, int pageSize, CancellationToken ct)
    {
        var query = _db.Users.AsNoTracking().Where(u => u.AccountType == AccountType.Staff && u.Role == role
            && (removed ? u.Status != UserStatus.Active : u.Status == UserStatus.Active));
        var total = await query.CountAsync(ct);
        var items = await query.OrderBy(u => u.Name).ThenBy(u => u.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return (items, total);
    }

    public async Task<IReadOnlyDictionary<Guid, ModeratorLoadDto>> GetLoadAsync(IReadOnlyCollection<Guid> ids, DateTime since, CancellationToken ct)
    {
        var inWork = await _db.Cases.AsNoTracking()
            .Where(c => c.ModeratorId != null && ids.Contains(c.ModeratorId.Value) && (c.Status == CaseStatus.InProgress || c.Status == CaseStatus.WaitingResponse))
            .GroupBy(c => new { Moderator = c.ModeratorId!.Value, c.Status }).Select(g => new { g.Key.Moderator, g.Key.Status, Count = g.Count() }).ToListAsync(ct);
        var reviews = await _db.CaseAppeals.AsNoTracking()
            .Where(a => a.ReviewerModeratorId != null && ids.Contains(a.ReviewerModeratorId.Value)
                && _db.Cases.Any(c => c.Id == a.CaseId && c.Status == CaseStatus.AppealReview))
            .GroupBy(a => a.ReviewerModeratorId!.Value).Select(g => new { Moderator = g.Key, Count = g.Count() }).ToListAsync(ct);
        var resolved = await _db.Cases.AsNoTracking()
            .Where(c => c.ModeratorId != null && ids.Contains(c.ModeratorId.Value) && c.ResolvedAt != null && c.ResolvedAt >= since)
            .GroupBy(c => c.ModeratorId!.Value).Select(g => new { Moderator = g.Key, Count = g.Count() }).ToListAsync(ct);

        return ids.ToDictionary(id => id, id => new ModeratorLoadDto(
            inWork.Where(x => x.Moderator == id && x.Status == CaseStatus.InProgress).Sum(x => x.Count),
            inWork.Where(x => x.Moderator == id && x.Status == CaseStatus.WaitingResponse).Sum(x => x.Count),
            reviews.Where(x => x.Moderator == id).Sum(x => x.Count),
            resolved.Where(x => x.Moderator == id).Sum(x => x.Count)));
    }
}
