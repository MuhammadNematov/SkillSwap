using Microsoft.EntityFrameworkCore;
using SkillSwap.Application.Reviews;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using SkillSwap.Infrastructure.Persistence;

namespace SkillSwap.Infrastructure.Moderation;

public sealed class ReviewStore : IReviewStore
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    public ReviewStore(AppDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public void Add(Review review) => _db.Reviews.Add(review);

    public Task<Review?> FindAsync(Guid id, CancellationToken ct) => _db.Reviews.FirstOrDefaultAsync(r => r.Id == id, ct);

    public Task<Review?> FindByAuthorAsync(Guid exchangeId, Guid authorId, bool tracked, CancellationToken ct)
    {
        var query = tracked ? _db.Reviews : _db.Reviews.AsNoTracking();
        return query.FirstOrDefaultAsync(r => r.ExchangeId == exchangeId && r.AuthorId == authorId, ct);
    }

    public async Task<IReadOnlySet<Guid>> ListWrittenExchangeIdsAsync(Guid authorId, IReadOnlyCollection<Guid> exchangeIds, CancellationToken ct) =>
        (await _db.Reviews.AsNoTracking().Where(r => r.AuthorId == authorId && exchangeIds.Contains(r.ExchangeId)).Select(r => r.ExchangeId).ToListAsync(ct)).ToHashSet();

    public async Task<(IReadOnlyList<Review> Items, int Total)> ListVisibleForTargetAsync(Guid targetId, int page, int pageSize, CancellationToken ct)
    {
        var query = _db.Reviews.AsNoTracking().Where(r => r.TargetId == targetId && (r.Status == ReviewStatus.Published || r.Status == ReviewStatus.Frozen));
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(r => r.PublishedAt).ThenBy(r => r.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return (items, total);
    }

    public async Task<RatingAggregate> AggregateAsync(Guid targetId, CancellationToken ct)
    {
        // Среднее из средних по четырём критериям всех опубликованных и не оспариваемых отзывов; оспариваемые (Frozen) в рейтинг не входят.
        var rows = await _db.Reviews.AsNoTracking()
            .Where(r => r.TargetId == targetId && r.Status == ReviewStatus.Published)
            .Select(r => new { Sum = r.ScoreKnowledge + r.ScoreExplaining + r.ScorePunctuality + r.ScorePoliteness })
            .ToListAsync(ct);
        return rows.Count == 0 ? new RatingAggregate(0, 0m) : new RatingAggregate(rows.Count, rows.Sum(r => r.Sum) / (4m * rows.Count));
    }

    public async Task ApplyRatingAsync(Guid userId, decimal average, int count, CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        await _db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s
            .SetProperty(u => u.RatingAvg, average)
            .SetProperty(u => u.RatingCount, count)
            .SetProperty(u => u.UpdatedAt, now), ct);
    }

    public async Task<IReadOnlyList<Review>> ListWrittenByAsync(Guid authorId, CancellationToken ct) =>
        await _db.Reviews.AsNoTracking().Where(r => r.AuthorId == authorId).OrderBy(r => r.CreatedAt).ToListAsync(ct);

    public async Task<IReadOnlyList<Review>> ListReceivedAsync(Guid targetId, CancellationToken ct) =>
        await _db.Reviews.AsNoTracking()
            .Where(r => r.TargetId == targetId && (r.Status == ReviewStatus.Published || r.Status == ReviewStatus.Frozen))
            .OrderBy(r => r.PublishedAt).ToListAsync(ct);

    public Task SaveAsync(CancellationToken ct) => _db.SaveChangesAsync(ct);
}
