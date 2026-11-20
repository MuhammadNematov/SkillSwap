using Microsoft.EntityFrameworkCore;
using SkillSwap.Application.Skills;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using SkillSwap.Infrastructure.Persistence;

namespace SkillSwap.Infrastructure.Skills;

public sealed class SkillSuggestionAdminStore : ISkillSuggestionAdminStore
{
    private readonly AppDbContext _db;

    public SkillSuggestionAdminStore(AppDbContext db)
    {
        _db = db;
    }

    public async Task<(IReadOnlyList<SuggestionRow> Items, int Total)> SearchAsync(SuggestionStatus status, Guid? categoryId, int page, int pageSize, CancellationToken ct)
    {
        var query = _db.SkillSuggestions.AsNoTracking().Where(s => s.Status == status);
        if (categoryId is { } category)
        {
            query = query.Where(s => s.CategoryId == category);
        }

        var total = await query.CountAsync(ct);
        // Очередь: неразобранные старые сверху (кто ждёт дольше), разобранные новые сверху.
        var ordered = status == SuggestionStatus.Pending ? query.OrderBy(s => s.CreatedAt).ThenBy(s => s.Id) : query.OrderByDescending(s => s.UpdatedAt).ThenBy(s => s.Id);
        var items = await ordered.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        var userIds = items.Select(s => s.UserId).Distinct().ToList();
        var categoryIds = items.Select(s => s.CategoryId).Distinct().ToList();
        var names = await _db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Name, ct);
        var categories = await _db.SkillCategories.AsNoTracking().Where(c => categoryIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.NameEn, ct);
        return (items.Select(s => new SuggestionRow(s, names.GetValueOrDefault(s.UserId, "—"), categories.GetValueOrDefault(s.CategoryId, "—"))).ToList(), total);
    }

    public Task<SkillSuggestion?> FindAsync(Guid id, bool tracked, CancellationToken ct) =>
        (tracked ? _db.SkillSuggestions : _db.SkillSuggestions.AsNoTracking()).FirstOrDefaultAsync(s => s.Id == id, ct);

    public async Task<IReadOnlyList<SkillSuggestion>> FindPendingSameAsync(Guid categoryId, string normalizedName, Guid exceptId, CancellationToken ct) =>
        await _db.SkillSuggestions.Where(s => s.Status == SuggestionStatus.Pending && s.CategoryId == categoryId && s.NormalizedName == normalizedName && s.Id != exceptId).ToListAsync(ct);

    public async Task<IReadOnlyDictionary<(Guid CategoryId, string Name), int>> CountPendingSameAsync(IReadOnlyCollection<(Guid CategoryId, string Name)> keys, CancellationToken ct)
    {
        if (keys.Count == 0)
        {
            return new Dictionary<(Guid CategoryId, string Name), int>();
        }

        var categories = keys.Select(k => k.CategoryId).Distinct().ToList();
        var names = keys.Select(k => k.Name).Distinct().ToList();
        var counts = await _db.SkillSuggestions.AsNoTracking()
            .Where(s => s.Status == SuggestionStatus.Pending && categories.Contains(s.CategoryId) && names.Contains(s.NormalizedName))
            .GroupBy(s => new { s.CategoryId, s.NormalizedName }).Select(g => new { g.Key.CategoryId, g.Key.NormalizedName, Count = g.Count() }).ToListAsync(ct);
        return counts.Where(c => keys.Contains((c.CategoryId, c.NormalizedName))).ToDictionary(c => (c.CategoryId, c.NormalizedName), c => c.Count);
    }

    public async Task<IReadOnlyList<Skill>> FindSimilarSkillsAsync(Guid categoryId, string normalizedName, int limit, CancellationToken ct)
    {
        var pattern = "%" + normalizedName.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        return await _db.Skills.AsNoTracking()
            .Where(s => s.CategoryId == categoryId
                && (EF.Functions.ILike(s.NameTg, pattern, "\\") || EF.Functions.ILike(s.NameRu, pattern, "\\") || EF.Functions.ILike(s.NameEn, pattern, "\\")
                    || s.Synonyms.Contains(normalizedName)))
            .OrderByDescending(s => s.IsActive).ThenBy(s => s.NameEn).Take(limit).ToListAsync(ct);
    }

    public Task<string?> GetUserNameAsync(Guid userId, CancellationToken ct) =>
        _db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => (string?)u.Name).FirstOrDefaultAsync(ct);
}
