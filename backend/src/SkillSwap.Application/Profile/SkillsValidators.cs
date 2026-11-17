using FluentValidation;
using SkillSwap.Domain.Entities;

namespace SkillSwap.Application.Profile;

public sealed class SkillEntryRequestValidator : AbstractValidator<SkillEntryRequest>
{
    public SkillEntryRequestValidator()
    {
        RuleFor(x => x.SkillId).NotEmpty().WithErrorCode("required");

        // Level — nullable, чтобы пропуск поля был ошибкой, а не молчаливым «Beginner»; число вне списка (например 7) тоже отвергается.
        RuleFor(x => x.Level).NotNull().WithErrorCode("required").IsInEnum().WithErrorCode("invalid_level");

        RuleFor(x => x.Description)
            .Must(d => d is null || d.Trim().Length <= UserSkill.DescriptionMaxLength).WithErrorCode("too_long");
    }
}

public sealed class SaveSkillsRequestValidator : AbstractValidator<SaveSkillsRequest>
{
    public SaveSkillsRequestValidator()
    {
        var entry = new SkillEntryRequestValidator();

        RuleFor(x => x.Teach).NotNull().WithErrorCode("required");
        RuleFor(x => x.Learn).NotNull().WithErrorCode("required");

        RuleFor(x => x.Teach)
            .Must(list => list!.Count <= UserSkill.MaxPerDirection).WithErrorCode("too_many")
            .Must(list => list!.All(e => e is not null)).WithErrorCode("required")
            .Must(NoRepeats).WithErrorCode("duplicate_skill")
            .When(x => x.Teach is not null);
        RuleFor(x => x.Learn)
            .Must(list => list!.Count <= UserSkill.MaxPerDirection).WithErrorCode("too_many")
            .Must(list => list!.All(e => e is not null)).WithErrorCode("required")
            .Must(NoRepeats).WithErrorCode("duplicate_skill")
            .When(x => x.Learn is not null);

        RuleForEach(x => x.Teach!).SetValidator(entry).When(x => x.Teach is not null && x.Teach.All(e => e is not null));
        RuleForEach(x => x.Learn!).SetValidator(entry).When(x => x.Learn is not null && x.Learn.All(e => e is not null));
    }

    private static bool NoRepeats(List<SkillEntryRequest>? list) =>
        list is null || list.Where(e => e is not null).Select(e => e.SkillId).Distinct().Count() == list.Count;
}
