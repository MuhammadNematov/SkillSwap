using FluentValidation;

namespace SkillSwap.Application.Auth;

public sealed class ForgotPasswordRequestValidator : AbstractValidator<ForgotPasswordRequest>
{
    public ForgotPasswordRequestValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty().WithErrorCode("required")
            .MaximumLength(256).WithErrorCode("too_long")
            .EmailAddress().WithErrorCode("invalid_email");
    }
}

public sealed class ResetPasswordRequestValidator : AbstractValidator<ResetPasswordRequest>
{
    public ResetPasswordRequestValidator()
    {
        RuleFor(x => x.UserId).NotEmpty().WithErrorCode("required");
        RuleFor(x => x.Token).NotEmpty().WithErrorCode("required").MaximumLength(2048).WithErrorCode("too_long");
        RuleFor(x => x.NewPassword).MustBeValidPassword();
    }
}
