using FluentValidation;
using SkillSwap.Domain.Entities;

namespace SkillSwap.Application.Skills;

internal static class CatalogRules
{
    public static IRuleBuilderOptions<T, string?> ValidName<T>(this IRuleBuilder<T, string?> rule, int max) => rule
        .Must(v => !string.IsNullOrWhiteSpace(v)).WithErrorCode("required")
        .Must(v => v is null || v.Trim().Length <= max).WithErrorCode("too_long");

    public static IRuleBuilderOptions<T, string[]?> ValidSynonyms<T>(this IRuleBuilder<T, string[]?> rule) => rule
        .Must(list => list is null || list.All(s => s is not null && s.Trim().Length <= Skill.SynonymMaxLength))
            .WithErrorCode("synonym_too_long")
        .Must(list => list is null || list.Select(s => s?.Trim().ToLowerInvariant()).Where(s => !string.IsNullOrEmpty(s)).Distinct().Count() <= Skill.MaxSynonyms)
            .WithErrorCode("too_many_synonyms");
}

public sealed class CategoryCreateRequestValidator : AbstractValidator<CategoryCreateRequest>
{
    public CategoryCreateRequestValidator()
    {
        RuleFor(x => x.NameTg).ValidName(SkillCategory.NameMaxLength);
        RuleFor(x => x.NameRu).ValidName(SkillCategory.NameMaxLength);
        RuleFor(x => x.NameEn).ValidName(SkillCategory.NameMaxLength);
        RuleFor(x => x.SortOrder).InclusiveBetween(0, 10_000).WithErrorCode("out_of_range");
    }
}

public sealed class CategoryUpdateRequestValidator : AbstractValidator<CategoryUpdateRequest>
{
    public CategoryUpdateRequestValidator()
    {
        RuleFor(x => x.NameTg).ValidName(SkillCategory.NameMaxLength);
        RuleFor(x => x.NameRu).ValidName(SkillCategory.NameMaxLength);
        RuleFor(x => x.NameEn).ValidName(SkillCategory.NameMaxLength);
        RuleFor(x => x.SortOrder).InclusiveBetween(0, 10_000).WithErrorCode("out_of_range");
        RuleFor(x => x.IsActive).NotNull().WithErrorCode("required");
    }
}

public sealed class SkillCreateRequestValidator : AbstractValidator<SkillCreateRequest>
{
    public SkillCreateRequestValidator()
    {
        RuleFor(x => x.CategoryId).NotEmpty().WithErrorCode("required");
        RuleFor(x => x.NameTg).ValidName(Skill.NameMaxLength);
        RuleFor(x => x.NameRu).ValidName(Skill.NameMaxLength);
        RuleFor(x => x.NameEn).ValidName(Skill.NameMaxLength);
        RuleFor(x => x.Synonyms).ValidSynonyms();
    }
}

public sealed class SkillUpdateRequestValidator : AbstractValidator<SkillUpdateRequest>
{
    public SkillUpdateRequestValidator()
    {
        RuleFor(x => x.CategoryId).NotEmpty().WithErrorCode("required");
        RuleFor(x => x.NameTg).ValidName(Skill.NameMaxLength);
        RuleFor(x => x.NameRu).ValidName(Skill.NameMaxLength);
        RuleFor(x => x.NameEn).ValidName(Skill.NameMaxLength);
        RuleFor(x => x.Synonyms).ValidSynonyms();
        RuleFor(x => x.IsActive).NotNull().WithErrorCode("required");
    }
}
