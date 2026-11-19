using FluentValidation;
using SkillSwap.Domain.Entities;

namespace SkillSwap.Application.ExchangeRequests;

public sealed class CreateExchangeRequestRequestValidator : AbstractValidator<CreateExchangeRequestRequest>
{
    public CreateExchangeRequestRequestValidator()
    {
        RuleFor(x => x.ToUserId).NotEmpty().WithErrorCode("required");
        RuleFor(x => x.ToTeachesSkillId).NotEmpty().WithErrorCode("required");
        RuleFor(x => x.FromTeachesSkillId).Must(id => id != Guid.Empty).When(x => x.FromTeachesSkillId is not null).WithErrorCode("invalid");
        RuleFor(x => x.DurationDays).Must(d => ExchangeRequest.AllowedDurations.Contains(d)).WithErrorCode("invalid_duration");
        RuleFor(x => x.Message).Must(m => m is null || m.Trim().Length <= ExchangeRequest.MessageMaxLength).WithErrorCode("too_long");
    }
}
