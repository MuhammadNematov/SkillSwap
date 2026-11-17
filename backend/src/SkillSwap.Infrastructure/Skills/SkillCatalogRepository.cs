using Microsoft.EntityFrameworkCore;
using Npgsql;
using SkillSwap.Application.Common;
using SkillSwap.Application.Skills;
using SkillSwap.Domain.Entities;
using SkillSwap.Infrastructure.Persistence;

namespace SkillSwap.Infrastructure.Skills;

public sealed class SkillCatalogRepository : ISkillCatalogRepository
{
    private readonly AppDbContext _db;

    public SkillCatalogRepository(AppDbContext db)
    {
        _db = db;
    }

    public Task<SkillCategory?> FindCategoryAsync(Guid id, CancellationToken ct) =>
        _db.SkillCategories.FirstOrDefaultAsync(c => c.Id == id, ct);

    public Task<Skill?> FindSkillAsync(Guid id, CancellationToken ct) =>
        _db.Skills.FirstOrDefaultAsync(s => s.Id == id, ct);

    public async Task<IReadOnlyList<AdminCategoryDto>> ListCategoriesAsync(CancellationToken ct) =>
        await _db.SkillCategories.AsNoTracking()
            .OrderBy(c => c.SortOrder).ThenBy(c => c.NameEn)
            .Select(c => new AdminCategoryDto(c.Id, c.NameTg, c.NameRu, c.NameEn, c.SortOrder, c.IsActive, c.Skills.Count))
            .ToListAsync(ct);

    public Task<int> CountSkillsInCategoryAsync(Guid categoryId, CancellationToken ct) =>
        _db.Skills.CountAsync(s => s.CategoryId == categoryId, ct);

    public Task<bool> CategoryNameExistsAsync(string nameEn, Guid? exceptId, CancellationToken ct)
    {
        var lower = nameEn.ToLowerInvariant();
        return _db.SkillCategories.AnyAsync(c => c.NameEn.ToLower() == lower && c.Id != exceptId, ct);
    }

    public Task<bool> SkillNameExistsAsync(Guid categoryId, string nameEn, Guid? exceptId, CancellationToken ct)
    {
        var lower = nameEn.ToLowerInvariant();
        return _db.Skills.AnyAsync(s => s.CategoryId == categoryId && s.NameEn.ToLower() == lower && s.Id != exceptId, ct);
    }

    public async Task<PagedResult<AdminSkillDto>> SearchSkillsAsync(AdminSkillSearch search, CancellationToken ct)
    {
        var skills = _db.Skills.AsNoTracking().AsQueryable();

        if (search.CategoryId is { } categoryId)
        {
            skills = skills.Where(s => s.CategoryId == categoryId);
        }

        if (search.IsActive is { } isActive)
        {
            skills = skills.Where(s => s.IsActive == isActive);
        }

        skills = SkillCatalogReader.WhereMatches(skills, search.Query);

        var total = await skills.CountAsync(ct);
        var items = await skills
            .OrderBy(s => s.Category!.SortOrder).ThenBy(s => s.NameEn).ThenBy(s => s.Id)
            .Skip((search.Page - 1) * search.PageSize).Take(search.PageSize)
            .Select(s => new AdminSkillDto(s.Id, s.CategoryId, s.NameTg, s.NameRu, s.NameEn, s.Synonyms, s.IsActive))
            .ToListAsync(ct);

        return new PagedResult<AdminSkillDto>(items, search.Page, search.PageSize, total);
    }

    public void Add(SkillCategory category) => _db.SkillCategories.Add(category);

    public void Add(Skill skill) => _db.Skills.Add(skill);

    public void Remove(SkillCategory category) => _db.SkillCategories.Remove(category);

    public void Remove(Skill skill) => _db.Skills.Remove(skill);

    public async Task<CatalogSaveResult> TrySaveAsync(CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
            return CatalogSaveResult.Saved;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return CatalogSaveResult.Duplicate;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation })
        {
            return CatalogSaveResult.InUse;
        }
    }
}
