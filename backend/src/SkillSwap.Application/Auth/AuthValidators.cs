using FluentValidation;
using SkillSwap.Application.Common;
using SkillSwap.Domain.Entities;

namespace SkillSwap.Application.Auth;

// Коды ошибок (WithErrorCode) стабильны: по ним фронтенд подбирает перевод.

public sealed class RegisterRequestValidator : AbstractValidator<RegisterRequest>
{
    public RegisterRequestValidator(TimeProvider clock)
    {
        RuleFor(x => x.Email)
            .NotEmpty().WithErrorCode("required")
            .MaximumLength(256).WithErrorCode("too_long")
            .EmailAddress().WithErrorCode("invalid_email");

        RuleFor(x => x.Password)
            .NotEmpty().WithErrorCode("required")
            .MinimumLength(8).WithErrorCode("password_too_short")
            .MaximumLength(128).WithErrorCode("too_long")
            .Must(p => p.Any(char.IsLetter)).WithErrorCode("password_needs_letter")
            .Must(p => p.Any(char.IsDigit)).WithErrorCode("password_needs_digit");

        RuleFor(x => x.Name)
            .NotEmpty().WithErrorCode("required")
            .MaximumLength(100).WithErrorCode("too_long");

        // FR-3: младше 16 лет — отказ.
        DateOnly Today() => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

        RuleFor(x => x.DateOfBirth)
            .NotEqual(default(DateOnly)).WithErrorCode("required");
        RuleFor(x => x.DateOfBirth)
            .Must(d => d <= Today()).WithErrorCode("date_in_future")
            .When(x => x.DateOfBirth != default);
        // Родился не позже, чем (сегодня − 16 лет), иначе ему ещё нет 16.
        RuleFor(x => x.DateOfBirth)
            .Must(d => d <= Today().AddYears(-User.MinAge)).WithErrorCode("too_young")
            .When(x => x.DateOfBirth != default && x.DateOfBirth <= Today());

        RuleFor(x => x.UiLanguage)
            .Must(SupportedLanguages.IsSupported!).When(x => x.UiLanguage is not null)
            .WithErrorCode("unsupported_language");

        // FR-3: соглашение и согласие на доступ модератора к переписке при обращении.
        RuleFor(x => x.AcceptTerms).Equal(true).WithErrorCode("terms_not_accepted");
        RuleFor(x => x.AcceptChatModeration).Equal(true).WithErrorCode("chat_moderation_not_accepted");
    }
}

public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().WithErrorCode("required").MaximumLength(256).WithErrorCode("too_long");
        RuleFor(x => x.Password).NotEmpty().WithErrorCode("required").MaximumLength(128).WithErrorCode("too_long");
    }
}

public sealed class ConfirmEmailRequestValidator : AbstractValidator<ConfirmEmailRequest>
{
    public ConfirmEmailRequestValidator()
    {
        RuleFor(x => x.UserId).NotEmpty().WithErrorCode("required");
        RuleFor(x => x.Token).NotEmpty().WithErrorCode("required");
    }
}

public sealed class ResendConfirmationRequestValidator : AbstractValidator<ResendConfirmationRequest>
{
    public ResendConfirmationRequestValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty().WithErrorCode("required")
            .EmailAddress().WithErrorCode("invalid_email");
    }
}
