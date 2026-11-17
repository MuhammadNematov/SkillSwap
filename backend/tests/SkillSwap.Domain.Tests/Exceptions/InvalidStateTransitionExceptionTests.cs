using SkillSwap.Domain.Exceptions;

namespace SkillSwap.Domain.Tests.Exceptions;

public class InvalidStateTransitionExceptionTests
{
    [Fact]
    public void Keeps_entity_and_statuses_and_has_stable_code()
    {
        var ex = new InvalidStateTransitionException("Exchange", "Completed", "Active");

        Assert.IsAssignableFrom<DomainException>(ex);
        Assert.Equal("invalid_state_transition", ex.Code);
        Assert.Equal("Exchange", ex.Entity);
        Assert.Equal("Completed", ex.From);
        Assert.Equal("Active", ex.To);
        Assert.Contains("Completed", ex.Message);
        Assert.Contains("Active", ex.Message);
    }
}
