using Microsoft.EntityFrameworkCore;
using SkillSwap.Application.Common;
using SkillSwap.Application.Skills;
using SkillSwap.Infrastructure.Persistence;

namespace SkillSwap.Infrastructure.Skills;

public sealed class SkillCatalogReader : ISkillCatalogReader
{
    private readonly AppDbContext _db;

    public SkillCatalogReader(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<SkillCategoryDto>> ListCategoriesAsync(CancellationToken ct) =>
        await _db.SkillCategories.AsNoTracking()
            .Where(c => c.IsActive)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.NameEn)
            .Select(c => new SkillCategoryDto(c.Id, c.NameTg, c.NameRu, c.NameEn, c.SortOrder))
            .ToListAsync(ct);

    public async Task<PagedResult<SkillDto>> SearchSkillsAsync(SkillSearch search, CancellationToken ct)
    {
        var skills = _db.Skills.AsNoTracking().Where(s => s.IsActive && s.Category!.IsActive);

        if (search.CategoryId is { } categoryId)
        {
            skills = skills.Where(s => s.CategoryId == categoryId);
        }

        if (search.Query is { } query)
        {
            // Подстрока без учёта регистра в названии на любом из трёх языков или в синониме.
            var pattern = $"%{EscapeLike(query)}%";
            skills = skills.Where(s =>
                EF.Functions.ILike(s.NameTg, pattern, LikeEscape) ||
                EF.Functions.ILike(s.NameRu, pattern, LikeEscape) ||
                EF.Functions.ILike(s.NameEn, pattern, LikeEscape) ||
                s.Synonyms.Any(synonym => EF.Functions.ILike(synonym, pattern, LikeEscape)));
        }

        var total = await skills.CountAsync(ct);

        // Порядок на выбранном языке; Id в конце делает страницы стабильными.
        var ordered = search.Language switch
        {
            "tg" => skills.OrderBy(s => s.NameTg),
            "ru" => skills.OrderBy(s => s.NameRu),
            _ => skills.OrderBy(s => s.NameEn),
        };

        var items = await ordered.ThenBy(s => s.Id)
            .Skip((search.Page - 1) * search.PageSize).Take(search.PageSize)
            .Select(s => new SkillDto(s.Id, s.CategoryId, s.NameTg, s.NameRu, s.NameEn))
            .ToListAsync(ct);

        return new PagedResult<SkillDto>(items, search.Page, search.PageSize, total);
    }

    private const string LikeEscape = "\\";

    /// <summary>Символы %, _ и \ в запросе пользователя ищутся как обычные, а не как шаблон.</summary>
    internal static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
