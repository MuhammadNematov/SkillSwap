using FluentValidation;

namespace SkillSwap.Application.Auth;

public sealed class LoginTwoFactorRequestValidator : AbstractValidator<LoginTwoFactorRequest>
{
    public LoginTwoFactorRequestValidator()
    {
        RuleFor(x => x.TwoFactorToken).NotEmpty().WithErrorCode("required");
        RuleFor(x => x.Code).NotEmpty().WithErrorCode("required").MaximumLength(64).WithErrorCode("too_long");
    }
}

public sealed class TwoFactorVerifyRequestValidator : AbstractValidator<TwoFactorVerifyRequest>
{
    public TwoFactorVerifyRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().WithErrorCode("required").MaximumLength(64).WithErrorCode("too_long");
    }
}

public sealed class TwoFactorDisableRequestValidator : AbstractValidator<TwoFactorDisableRequest>
{
    public TwoFactorDisableRequestValidator()
    {
        RuleFor(x => x.Password).NotEmpty().WithErrorCode("required");
        RuleFor(x => x.Code).NotEmpty().WithErrorCode("required").MaximumLength(64).WithErrorCode("too_long");
    }
}
