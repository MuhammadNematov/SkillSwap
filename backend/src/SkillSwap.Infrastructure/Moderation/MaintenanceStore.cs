using Microsoft.EntityFrameworkCore;
using SkillSwap.Application.Moderation;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using SkillSwap.Infrastructure.Persistence;

namespace SkillSwap.Infrastructure.Moderation;

public sealed class MaintenanceStore : IMaintenanceStore
{
    /// <summary>Допуск при сравнении со сроком: задача сверки не должна опережать разовую (она ставится на тот же момент).</summary>
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(1);

    private readonly AppDbContext _db;

    public MaintenanceStore(AppDbContext db)
    {
        _db = db;
    }

    public async Task<int> DeleteOldNotificationsAsync(bool read, DateTime olderThan, int limit, CancellationToken ct)
    {
        var ids = _db.Notifications.AsNoTracking().Where(n => n.IsRead == read && n.CreatedAt < olderThan).OrderBy(n => n.CreatedAt).Select(n => n.Id).Take(limit);
        return await _db.Notifications.Where(n => ids.Contains(n.Id)).ExecuteDeleteAsync(ct);
    }

    public async Task<IReadOnlySet<string>> FindAttachmentKeysAsync(IReadOnlyCollection<string> keys, CancellationToken ct) =>
        (await _db.Attachments.AsNoTracking().Where(a => keys.Contains(a.StorageKey)).Select(a => a.StorageKey).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);

    public async Task<IReadOnlyList<Guid>> ListExpiredWaitingAsync(DateTime now, CancellationToken ct)
    {
        var before = now - Grace;
        return await _db.Cases.AsNoTracking().Where(c => c.Status == CaseStatus.WaitingResponse && c.ResponseDeadlineAt < before).Select(c => c.Id).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> ListResolvedDueAsync(DateTime now, CancellationToken ct)
    {
        var before = now - CaseRules.AppealWindow - Grace;
        return await _db.Cases.AsNoTracking().Where(c => c.Status == CaseStatus.Resolved && c.ResolvedAt < before).Select(c => c.Id).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Case>> ListOverdueUnmarkedAsync(DateTime now, CancellationToken ct) =>
        await _db.Cases.Where(c => c.Status == CaseStatus.Open && c.DueAt < now && c.OverdueMarkedAt == null).ToListAsync(ct);

    public async Task<IReadOnlyList<ChatAccessGrant>> ListStaleGrantsAsync(CancellationToken ct) =>
        await (from g in _db.ChatAccessGrants
               join c in _db.Cases on g.CaseId equals c.Id
               where g.RevokedAt == null
                   && ((g.Reason == GrantReason.Case && c.Status != CaseStatus.InProgress && c.Status != CaseStatus.WaitingResponse)
                       || (g.Reason == GrantReason.AppealReview && c.Status != CaseStatus.AppealReview))
               select g).ToListAsync(ct);

    public async Task<IReadOnlyList<Guid>> ListErasurePendingAsync(CancellationToken ct) =>
        await _db.Users.AsNoTracking().Where(u => u.Status == UserStatus.Deleted && u.ErasurePending).Select(u => u.Id).ToListAsync(ct);

    public Task SaveAsync(CancellationToken ct) => _db.SaveChangesAsync(ct);

    public async Task<int> ReleaseEvidenceAsync(DateTime closedBefore, CancellationToken ct) =>
        await _db.CaseEvidence
            .Where(e => _db.Cases.Any(c => c.Id == e.CaseId && (c.Status == CaseStatus.Withdrawn || (c.Status == CaseStatus.Closed && c.ClosedAt <= closedBefore))))
            .ExecuteDeleteAsync(ct);

    public async Task<IReadOnlyList<Guid>> ListPurgeCandidatesAsync(DateTime closedBefore, int limit, CancellationToken ct) =>
        await _db.Exchanges.AsNoTracking()
            .Where(e => e.ClosedAt != null && e.ClosedAt <= closedBefore && e.ChatPurgedAt == null
                && (e.Status == ExchangeStatus.Completed || e.Status == ExchangeStatus.Aborted)
                && !_db.Cases.Any(c => c.ExchangeId == e.Id && c.Status != CaseStatus.Withdrawn && c.Status != CaseStatus.Closed))
            .OrderBy(e => e.ClosedAt).Select(e => e.Id).Take(limit).ToListAsync(ct);

    public async Task<PurgeResult> PurgeExchangeAsync(Guid exchangeId, DateTime now, CancellationToken ct)
    {
        // Вложения, на которые ссылается защищённое сообщение, остаются вместе с ним.
        var protectedAttachments = _db.Messages
            .Where(m => m.ExchangeId == exchangeId && m.AttachmentId != null && _db.CaseEvidence.Any(e => e.MessageId == m.Id))
            .Select(m => m.AttachmentId!.Value);
        var attachments = await _db.Attachments.AsNoTracking()
            .Where(a => a.ExchangeId == exchangeId && !protectedAttachments.Contains(a.Id)).Select(a => new { a.Id, a.StorageKey }).ToListAsync(ct);

        // Сначала сообщения (прежние версии уходят каскадом), затем вложения: связь сообщения с вложением без каскада.
        var messages = await _db.Messages.Where(m => m.ExchangeId == exchangeId && !_db.CaseEvidence.Any(e => e.MessageId == m.Id)).ExecuteDeleteAsync(ct);
        var ids = attachments.Select(a => a.Id).ToList();
        var removed = ids.Count == 0 ? 0 : await _db.Attachments.Where(a => ids.Contains(a.Id)).ExecuteDeleteAsync(ct);

        var left = await _db.Messages.AnyAsync(m => m.ExchangeId == exchangeId, ct);
        if (!left)
        {
            await _db.Exchanges.Where(e => e.Id == exchangeId).ExecuteUpdateAsync(s => s.SetProperty(e => e.ChatPurgedAt, now), ct);
        }

        return new PurgeResult(messages, removed, attachments.Select(a => a.StorageKey).ToList(), !left);
    }
}
