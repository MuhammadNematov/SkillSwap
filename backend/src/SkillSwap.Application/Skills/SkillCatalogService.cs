using SkillSwap.Application.Common;
using SkillSwap.Application.Exceptions;

namespace SkillSwap.Application.Skills;

/// <summary>Каталог «Категория → Навык» (раздел 5.2 ТЗ): список категорий и поиск навыков.</summary>
public sealed class SkillCatalogService
{
    public const int QueryMaxLength = 100;

    private readonly ISkillCatalogReader _reader;

    public SkillCatalogService(ISkillCatalogReader reader)
    {
        _reader = reader;
    }

    public Task<IReadOnlyList<SkillCategoryDto>> ListCategoriesAsync(CancellationToken ct) =>
        _reader.ListCategoriesAsync(ct);

    public Task<PagedResult<SkillDto>> SearchAsync(
        Guid? categoryId, string? query, string? language, int page, int pageSize, CancellationToken ct)
    {
        Paging.Validate(page, pageSize);

        var normalizedQuery = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
        if (normalizedQuery is { Length: > QueryMaxLength })
        {
            throw new BadRequestException("query_too_long", $"Поисковый запрос длиннее {QueryMaxLength} символов.");
        }

        var lang = string.IsNullOrWhiteSpace(language) ? SupportedLanguages.Default : language.Trim().ToLowerInvariant();
        if (!SupportedLanguages.IsSupported(lang))
        {
            throw new BadRequestException("unsupported_language", "Язык должен быть tg, ru или en.");
        }

        return _reader.SearchSkillsAsync(new SkillSearch(categoryId, normalizedQuery, lang, page, pageSize), ct);
    }
}
