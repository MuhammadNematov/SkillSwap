using Microsoft.EntityFrameworkCore;
using SkillSwap.Application.Chat;
using SkillSwap.Domain.Entities;
using SkillSwap.Infrastructure.Persistence;

namespace SkillSwap.Infrastructure.Exchanges;

public sealed class MessageStore : IMessageStore
{
    private readonly AppDbContext _db;

    public MessageStore(AppDbContext db)
    {
        _db = db;
    }

    public void Add(Message message) => _db.Messages.Add(message);

    public void AddRevision(MessageRevision revision) => _db.MessageRevisions.Add(revision);

    public Task<Message?> FindAsync(Guid id, CancellationToken ct) => _db.Messages.FirstOrDefaultAsync(m => m.Id == id, ct);

    public Task SaveAsync(CancellationToken ct) => _db.SaveChangesAsync(ct);

    public async Task<IReadOnlyList<Message>> ListBeforeAsync(Guid exchangeId, (DateTime CreatedAt, Guid Id)? before, int limit, CancellationToken ct)
    {
        var query = _db.Messages.AsNoTracking().Where(m => m.ExchangeId == exchangeId);
        if (before is { } cursor)
        {
            var (at, id) = cursor;
            query = query.Where(m => m.CreatedAt < at || (m.CreatedAt == at && m.Id.CompareTo(id) < 0));
        }

        return await query.OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id).Take(limit).ToListAsync(ct);
    }

    public async Task<int> MarkReadAsync(Guid exchangeId, Guid readerId, (DateTime CreatedAt, Guid Id)? upTo, DateTime now, CancellationToken ct)
    {
        var query = _db.Messages.Where(m => m.ExchangeId == exchangeId && m.ReadAt == null && m.SenderId != readerId);
        if (upTo is { } limit)
        {
            var (at, id) = limit;
            query = query.Where(m => m.CreatedAt < at || (m.CreatedAt == at && m.Id.CompareTo(id) <= 0));
        }

        return await query.ExecuteUpdateAsync(s => s.SetProperty(m => m.ReadAt, now), ct);
    }

    public async Task<DateTime?> OldestUnreadAtAsync(Guid recipientId, Guid exchangeId, DateTime? after, CancellationToken ct)
    {
        var query = _db.Messages.AsNoTracking()
            .Where(m => m.ExchangeId == exchangeId && m.ReadAt == null && m.DeletedAt == null && m.SenderId != recipientId);
        if (after is { } since)
        {
            query = query.Where(m => m.CreatedAt > since);
        }

        return await query.OrderBy(m => m.CreatedAt).Select(m => (DateTime?)m.CreatedAt).FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyDictionary<Guid, int>> CountUnreadAsync(Guid userId, IReadOnlyCollection<Guid> exchangeIds, CancellationToken ct) =>
        await _db.Messages.AsNoTracking()
            .Where(m => exchangeIds.Contains(m.ExchangeId) && m.ReadAt == null && m.DeletedAt == null && m.SenderId != userId)
            .GroupBy(m => m.ExchangeId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
}
