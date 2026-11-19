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
        query = scope == ModeratorScope.Queue
            ? query.Where(c => c.Status == CaseStatus.Open)
            : query.Where(c => c.ModeratorId == moderatorId && c.Status != CaseStatus.Closed && c.Status != CaseStatus.Withdrawn);
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

    public async Task<IReadOnlyCollection<Guid>> GetConflictUserIdsAsync(Guid personalUserId, CancellationToken ct)
    {
        var partners = await _db.Exchanges.AsNoTracking()
            .Where(e => e.UserAId == personalUserId || e.UserBId == personalUserId)
            .Select(e => e.UserAId == personalUserId ? e.UserBId : e.UserAId)
            .Distinct().ToListAsync(ct);
        partners.Add(personalUserId);
        return partners;
    }

    public Task SaveAsync(CancellationToken ct) => _db.SaveChangesAsync(ct);
}
