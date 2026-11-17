using FluentValidation;
using SkillSwap.Application.Common;
using SkillSwap.Domain.Entities;

namespace SkillSwap.Application.Profile;

public sealed class UpdateProfileRequestValidator : AbstractValidator<UpdateProfileRequest>
{
    public const int MaxLanguages = LessonLanguages.MaxPerUser;

    public UpdateProfileRequestValidator()
    {
        RuleFor(x => x.Name)
            .Must(v => !string.IsNullOrWhiteSpace(v)).WithErrorCode("required")
            .Must(v => v is null || v.Trim().Length <= User.NameMaxLength).WithErrorCode("too_long");

        RuleFor(x => x.Bio)
            .Must(v => v is null || v.Trim().Length <= User.BioMaxLength).WithErrorCode("too_long");

        RuleFor(x => x.UiLanguage)
            .Must(v => SupportedLanguages.IsSupported(v)).WithErrorCode("unsupported_language");

        RuleFor(x => x.TimeZone)
            .Must(TimeZones.IsValid).WithErrorCode("invalid_time_zone");

        RuleFor(x => x.Languages)
            .NotNull().WithErrorCode("required")
            .Must(list => list is null || list.All(c => c is not null && LessonLanguages.IsValid(LessonLanguages.Normalize(c))))
                .WithErrorCode("unsupported_language")
            .Must(list => list is null || list.Where(c => c is not null).Select(c => LessonLanguages.Normalize(c)).Distinct().Count() <= MaxLanguages)
                .WithErrorCode("too_many");

        RuleFor(x => x.IsSearchable).NotNull().WithErrorCode("required");
    }
}
