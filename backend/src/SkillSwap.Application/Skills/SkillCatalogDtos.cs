namespace SkillSwap.Application.Skills;

public sealed record SkillCategoryDto(Guid Id, string NameTg, string NameRu, string NameEn, int SortOrder);

public sealed record SkillDto(Guid Id, Guid CategoryId, string NameTg, string NameRu, string NameEn);

/// <param name="Query">Часть названия (на любом языке) или синонима; без учёта регистра.</param>
/// <param name="Language">На каком языке сортировать выдачу: tg, ru или en.</param>
public sealed record SkillSearch(Guid? CategoryId, string? Query, string Language, int Page, int PageSize);
