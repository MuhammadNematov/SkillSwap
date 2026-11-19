using Microsoft.EntityFrameworkCore;
using SkillSwap.Application.Moderation;
using SkillSwap.Domain.Entities;
using SkillSwap.Infrastructure.Persistence;

namespace SkillSwap.Infrastructure.Moderation;

public sealed class CaseMessageStore : ICaseMessageStore
{
    private readonly AppDbContext _db;

    public CaseMessageStore(AppDbContext db)
    {
        _db = db;
    }

    public void Add(CaseMessage message) => _db.CaseMessages.Add(message);

    public Task<CaseMessage?> FindAsync(Guid id, CancellationToken ct) => _db.CaseMessages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct);

    public async Task<IReadOnlyList<CaseMessage>> ListAsync(Guid caseId, Guid? forUser, CancellationToken ct)
    {
        var query = _db.CaseMessages.AsNoTracking().Where(m => m.CaseId == caseId);
        if (forUser is { } user)
        {
            query = query.Where(m => m.SenderId == user || m.RecipientId == user);
        }

        return await query.OrderBy(m => m.CreatedAt).ThenBy(m => m.Id).ToListAsync(ct);
    }

    public Task<int> CountQuestionsAsync(Guid caseId, CancellationToken ct) =>
        _db.CaseMessages.CountAsync(m => m.CaseId == caseId && m.InReplyToId == null, ct);

    public Task<bool> HasUnansweredAsync(Guid caseId, CancellationToken ct) =>
        _db.CaseMessages.AnyAsync(m => m.CaseId == caseId && m.InReplyToId == null && !_db.CaseMessages.Any(r => r.InReplyToId == m.Id), ct);

    public async Task<IReadOnlyDictionary<Guid, int>> CountUnansweredAsync(IReadOnlyCollection<Guid> caseIds, Guid? recipientId, CancellationToken ct)
    {
        var rows = await _db.CaseMessages.AsNoTracking()
            .Where(m => caseIds.Contains(m.CaseId) && m.InReplyToId == null && (recipientId == null || m.RecipientId == recipientId)
                && !_db.CaseMessages.Any(r => r.InReplyToId == m.Id))
            .GroupBy(m => m.CaseId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        return rows.ToDictionary(r => r.Key, r => r.Count);
    }

    public async Task<IReadOnlySet<Guid>> AnsweredQuestionIdsAsync(IReadOnlyCollection<Guid> questionIds, CancellationToken ct) =>
        (await _db.CaseMessages.AsNoTracking().Where(m => m.InReplyToId != null && questionIds.Contains(m.InReplyToId.Value)).Select(m => m.InReplyToId!.Value).ToListAsync(ct)).ToHashSet();

    public Task SaveAsync(CancellationToken ct) => _db.SaveChangesAsync(ct);
}
