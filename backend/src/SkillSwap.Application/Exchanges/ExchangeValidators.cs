using FluentValidation;
using SkillSwap.Domain.Entities;

namespace SkillSwap.Application.Exchanges;

public sealed class AbortExchangeRequestValidator : AbstractValidator<AbortExchangeRequest>
{
    public AbortExchangeRequestValidator()
    {
        RuleFor(x => x.Reason).Must(r => !string.IsNullOrWhiteSpace(r)).WithErrorCode("required");
        RuleFor(x => x.Reason).Must(r => r is null || r.Trim().Length <= Exchange.AbortReasonMaxLength).WithErrorCode("too_long");
    }
}
