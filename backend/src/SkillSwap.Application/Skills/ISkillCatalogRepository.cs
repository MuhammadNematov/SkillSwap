using SkillSwap.Application.Common;
using SkillSwap.Domain.Entities;

namespace SkillSwap.Application.Skills;

public enum CatalogSaveResult
{
    Saved,

    /// <summary>Такое название уже есть (сработал уникальный индекс: например, два админа добавили одно и то же одновременно).</summary>
    Duplicate,

    /// <summary>Запись нужна другим данным (навык в анкете, навыки в категории): удалять нельзя.</summary>
    InUse,
}

/// <summary>Правка каталога админом (раздел 5.2 ТЗ).</summary>
public interface ISkillCatalogRepository
{
    Task<SkillCategory?> FindCategoryAsync(Guid id, CancellationToken ct);

    Task<Skill?> FindSkillAsync(Guid id, CancellationToken ct);

    Task<IReadOnlyList<AdminCategoryDto>> ListCategoriesAsync(CancellationToken ct);

    Task<int> CountSkillsInCategoryAsync(Guid categoryId, CancellationToken ct);

    /// <summary>Есть ли категория с таким английским названием (без учёта регистра), кроме указанной.</summary>
    Task<bool> CategoryNameExistsAsync(string nameEn, Guid? exceptId, CancellationToken ct);

    /// <summary>Есть ли в категории навык с таким английским названием (без учёта регистра), кроме указанного.</summary>
    Task<bool> SkillNameExistsAsync(Guid categoryId, string nameEn, Guid? exceptId, CancellationToken ct);

    Task<PagedResult<AdminSkillDto>> SearchSkillsAsync(AdminSkillSearch search, CancellationToken ct);

    void Add(SkillCategory category);

    void Add(Skill skill);

    void Remove(SkillCategory category);

    void Remove(Skill skill);

    Task<CatalogSaveResult> TrySaveAsync(CancellationToken ct);
}
