using SkillSwap.Application.Common;

namespace SkillSwap.Application.Skills;

/// <summary>Чтение каталога навыков для пользователей: видны только включённые категории и навыки.</summary>
public interface ISkillCatalogReader
{
    Task<IReadOnlyList<SkillCategoryDto>> ListCategoriesAsync(CancellationToken ct);

    Task<PagedResult<SkillDto>> SearchSkillsAsync(SkillSearch search, CancellationToken ct);
}
