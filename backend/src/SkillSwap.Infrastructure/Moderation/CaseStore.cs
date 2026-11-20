using Microsoft.EntityFrameworkCore;
using SkillSwap.Application.Moderation;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using SkillSwap.Infrastructure.Persistence;

namespace SkillSwap.Infrastructure.Moderation;

public sealed class CaseStore : ICaseStore
{
    /// <summary>Обращение не в работе: отозвано или закрыто окончательно.</summary>
    private static readonly CaseStatus[] Finished = [CaseStatus.Withdrawn, CaseStatus.Closed];

    private readonly AppDbContext _db;

    public CaseStore(AppDbContext db)
    {
        _db = db;
    }

    public void Add(Case value) => _db.Cases.Add(value);

    public void AddEvidence(CaseEvidence evidence) => _db.CaseEvidence.Add(evidence);

    public Task<Case?> FindAsync(Guid id, bool tracked, CancellationToken ct) =>
        (tracked ? _db.Cases : _db.Cases.AsNoTracking()).FirstOrDefaultAsync(c => c.Id == id, ct);

    public Task<int> CountActiveByOpenerAsync(Guid openerId, CancellationToken ct) =>
        _db.Cases.CountAsync(c => c.OpenedById == openerId && !Finished.Contains(c.Status), ct);

    public Task<int> CountOpenedSinceAsync(Guid openerId, DateTime since, CancellationToken ct) =>
        _db.Cases.CountAsync(c => c.OpenedById == openerId && c.CreatedAt >= since, ct);

    public Task<bool> HasActiveAsync(Guid openerId, Guid exchangeId, CaseType type, CancellationToken ct) =>
        _db.Cases.AnyAsync(c => c.OpenedById == openerId && c.ExchangeId == exchangeId && c.Type == type && !Finished.Contains(c.Status), ct);

    public Task<bool> HasDisputeForReviewAsync(Guid reviewId, CancellationToken ct) =>
        _db.Cases.AnyAsync(c => c.ReviewId == reviewId && c.Status != CaseStatus.Withdrawn, ct);

    public Task<bool> HasActiveFromCallAsync(Guid exchangeId, Guid? exceptCaseId, CancellationToken ct) =>
        _db.Cases.AnyAsync(c => c.ExchangeId == exchangeId && c.FromCall && c.Id != exceptCaseId && !Finished.Contains(c.Status), ct);

    public async Task<(IReadOnlyList<Case> Items, int Total)> ListForUserAsync(Guid userId, CaseListFilter filter, int page, int pageSize, CancellationToken ct)
    {
        // Поданные мной видны всегда; поданные на меня — только после взятия в работу.
        var query = filter switch
        {
            CaseListFilter.Opened => _db.Cases.AsNoTracking().Where(c => c.OpenedById == userId),
            CaseListFilter.Against => _db.Cases.AsNoTracking().Where(c => c.AgainstUserId == userId && c.Status != CaseStatus.Open && c.Status != CaseStatus.Withdrawn),
            _ => _db.Cases.AsNoTracking().Where(c =>
                c.OpenedById == userId || (c.AgainstUserId == userId && c.Status != CaseStatus.Open && c.Status != CaseStatus.Withdrawn)),
        };

        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(c => c.CreatedAt).ThenBy(c => c.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return (items, total);
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>> GetEvidenceIdsAsync(IReadOnlyCollection<Guid> caseIds, CancellationToken ct)
    {
        var rows = await _db.CaseEvidence.AsNoTracking().Where(e => caseIds.Contains(e.CaseId)).OrderBy(e => e.AddedAt).Select(e => new { e.CaseId, e.MessageId }).ToListAsync(ct);
        return rows.GroupBy(r => r.CaseId).ToDictionary(g => g.Key, g => (IReadOnlyList<Guid>)g.Select(r => r.MessageId).ToList());
    }

    public async Task<(IReadOnlyList<Case> Items, int Total)> ListForModeratorAsync(
        ModeratorScope scope, Guid moderatorId, IReadOnlyCollection<Guid> excludedUsers, CaseType? type, CasePriority? priority, int page, int pageSize, CancellationToken ct)
    {
        var query = _db.Cases.AsNoTracking().AsQueryable();
        query = scope switch
        {
            ModeratorScope.Queue => query.Where(c => c.Status == CaseStatus.Open),
            ModeratorScope.Appeals => query.Where(c => c.Status == CaseStatus.AppealReview && _db.CaseAppeals.Any(a => a.CaseId == c.Id && a.ReviewerModeratorId == moderatorId)),
            _ => query.Where(c => c.ModeratorId == moderatorId && c.Status != CaseStatus.Closed && c.Status != CaseStatus.Withdrawn),
        };
        if (excludedUsers.Count > 0)
        {
            query = query.Where(c => !excludedUsers.Contains(c.OpenedById) && !excludedUsers.Contains(c.AgainstUserId));
        }

        if (type is { } t)
        {
            query = query.Where(c => c.Type == t);
        }

        if (priority is { } p)
        {
            query = query.Where(c => c.Priority == p);
        }

        var total = await query.CountAsync(ct);
        var items = await query.OrderBy(c => c.Priority == CasePriority.High ? 0 : 1).ThenBy(c => c.DueAt).ThenBy(c => c.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return (items, total);
    }

    public async Task<IReadOnlyCollection<Guid>> GetConflictUserIdsForStaffAsync(Guid staffId, CancellationToken ct)
    {
        var personal = await _db.StaffPersonalLinks.AsNoTracking().Where(l => l.StaffId == staffId).Select(l => l.PersonalUserId).ToListAsync(ct);
        if (await _db.Users.AsNoTracking().Where(u => u.Id == staffId).Select(u => u.LinkedUserId).FirstOrDefaultAsync(ct) is { } current && !personal.Contains(current))
        {
            personal.Add(current);   // привязка, сделанная без записи в историю (старые данные, разработка)
        }

        if (personal.Count == 0)
        {
            return [];
        }

        var partners = await _db.Exchanges.AsNoTracking()
            .Where(e => personal.Contains(e.UserAId) || personal.Contains(e.UserBId))
            .Select(e => personal.Contains(e.UserAId) ? e.UserBId : e.UserAId)
            .Distinct().ToListAsync(ct);
        return personal.Concat(partners).Distinct().ToList();
    }

    public Task<int> CountActiveConflictsAsync(Guid moderatorId, IReadOnlyCollection<Guid> conflictUsers, CancellationToken ct) =>
        _db.Cases.CountAsync(c =>
            (conflictUsers.Contains(c.OpenedById) || conflictUsers.Contains(c.AgainstUserId))
            && ((c.ModeratorId == moderatorId && (c.Status == CaseStatus.InProgress || c.Status == CaseStatus.WaitingResponse))
                || (c.Status == CaseStatus.AppealReview && _db.CaseAppeals.Any(a => a.CaseId == c.Id && a.ReviewerModeratorId == moderatorId))), ct);

    public void AddAppeal(CaseAppeal appeal) => _db.CaseAppeals.Add(appeal);

    public Task<CaseAppeal?> FindAppealAsync(Guid caseId, bool tracked, CancellationToken ct) =>
        (tracked ? _db.CaseAppeals : _db.CaseAppeals.AsNoTracking()).FirstOrDefaultAsync(a => a.CaseId == caseId, ct);

    public async Task<IReadOnlyDictionary<Guid, CaseAppeal>> GetAppealsAsync(IReadOnlyCollection<Guid> caseIds, CancellationToken ct) =>
        await _db.CaseAppeals.AsNoTracking().Where(a => caseIds.Contains(a.CaseId)).ToDictionaryAsync(a => a.CaseId, ct);

    public async Task<IReadOnlyList<Guid>> ListActiveAdminIdsAsync(CancellationToken ct) =>
        await _db.Users.AsNoTracking().Where(u => (u.Role == UserRole.Admin || u.Role == UserRole.SuperAdmin) && u.Status == UserStatus.Active).Select(u => u.Id).ToListAsync(ct);

    public async Task<IReadOnlyList<User>> ListModeratorsAsync(CancellationToken ct) =>
        await _db.Users.AsNoTracking().Where(u => u.Role == UserRole.Moderator && u.Status == UserStatus.Active).OrderBy(u => u.Name).ToListAsync(ct);

    public async Task<(IReadOnlyList<Case> Items, int Total)> ListForAdminAsync(CaseStatus? status, CaseType? type, int page, int pageSize, CancellationToken ct)
    {
        var query = _db.Cases.AsNoTracking().AsQueryable();
        if (status is { } s)
        {
            query = query.Where(c => c.Status == s);
        }

        if (type is { } t)
        {
            query = query.Where(c => c.Type == t);
        }

        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(c => c.CreatedAt).ThenBy(c => c.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return (items, total);
    }

    public async Task<IReadOnlyList<Warning>> FindWarningsForCaseAsync(Guid caseId, CancellationToken ct) =>
        await _db.Warnings.Where(w => w.CaseId == caseId).ToListAsync(ct);

    public async Task<IReadOnlyList<Warning>> ListWarningsForCaseReadOnlyAsync(Guid caseId, CancellationToken ct) =>
        await _db.Warnings.AsNoTracking().Where(w => w.CaseId == caseId).OrderBy(w => w.CreatedAt).ToListAsync(ct);

    public Task<BanRequest?> FindBanRequestAsync(Guid id, CancellationToken ct) => _db.BanRequests.FirstOrDefaultAsync(b => b.Id == id, ct);

    public async Task<IReadOnlyList<BanRequest>> FindBanRequestsForCaseAsync(Guid caseId, CancellationToken ct) =>
        await _db.BanRequests.Where(b => b.CaseId == caseId).ToListAsync(ct);

    public async Task<(IReadOnlyList<BanRequest> Items, int Total)> ListBanRequestsAsync(BanRequestStatus? status, int page, int pageSize, CancellationToken ct)
    {
        var query = _db.BanRequests.AsNoTracking().AsQueryable();
        if (status is { } s)
        {
            query = query.Where(b => b.Status == s);
        }

        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(b => b.CreatedAt).ThenBy(b => b.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return (items, total);
    }

    public void AddWarning(Warning warning) => _db.Warnings.Add(warning);

    public void AddBanRequest(BanRequest request) => _db.BanRequests.Add(request);

    public Task<int> CountActiveWarningsAsync(Guid userId, DateTime now, CancellationToken ct) =>
        _db.Warnings.CountAsync(w => w.UserId == userId && w.RevokedAt == null && w.ExpiresAt > now, ct);

    public Task<bool> HasPendingBanRequestAsync(Guid userId, CancellationToken ct) =>
        _db.BanRequests.AnyAsync(b => b.UserId == userId && b.Status == BanRequestStatus.Pending, ct);

    public Task<bool> HasOtherActiveComplaintAsync(Guid exchangeId, Guid exceptCaseId, CancellationToken ct) =>
        _db.Cases.AnyAsync(c => c.ExchangeId == exchangeId && c.Id != exceptCaseId && c.Type != CaseType.ReviewDispute && !Finished.Contains(c.Status), ct);

    public Task<bool> HasActiveCaseAsync(Guid userId, CancellationToken ct) =>
        _db.Cases.AnyAsync(c => (c.OpenedById == userId || c.AgainstUserId == userId) && !Finished.Contains(c.Status), ct);

    public Task SaveAsync(CancellationToken ct) => _db.SaveChangesAsync(ct);
}

/// <summary>Пока по пользователю есть обращение, которое не закрыто и не отозвано (раздел 8 ТЗ), его личные данные при удалении аккаунта удерживаются.</summary>
public sealed class OpenCaseErasureHold : SkillSwap.Application.Account.IErasureHold
{
    private readonly ICaseStore _cases;

    public OpenCaseErasureHold(ICaseStore cases)
    {
        _cases = cases;
    }

    public Task<bool> HasOpenCaseAsync(Guid userId, CancellationToken ct) => _cases.HasActiveCaseAsync(userId, ct);
}
