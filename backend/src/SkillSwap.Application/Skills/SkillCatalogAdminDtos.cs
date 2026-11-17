namespace SkillSwap.Application.Skills;

public sealed record CategoryCreateRequest(string NameTg, string NameRu, string NameEn, int SortOrder);

/// <summary>PUT заменяет категорию целиком, поэтому <c>isActive</c> обязателен (иначе пропущенное поле выключило бы категорию).</summary>
public sealed record CategoryUpdateRequest(string NameTg, string NameRu, string NameEn, int SortOrder, bool? IsActive);

public sealed record AdminCategoryDto(
    Guid Id, string NameTg, string NameRu, string NameEn, int SortOrder, bool IsActive, int SkillCount);

public sealed record SkillCreateRequest(
    Guid CategoryId, string NameTg, string NameRu, string NameEn, string[]? Synonyms);

public sealed record SkillUpdateRequest(
    Guid CategoryId, string NameTg, string NameRu, string NameEn, string[]? Synonyms, bool? IsActive);

public sealed record AdminSkillDto(
    Guid Id, Guid CategoryId, string NameTg, string NameRu, string NameEn, string[] Synonyms, bool IsActive);

/// <param name="IsActive">null — показывать и включённые, и выключенные.</param>
public sealed record AdminSkillSearch(Guid? CategoryId, string? Query, bool? IsActive, int Page, int PageSize);
