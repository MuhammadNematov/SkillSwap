using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Exceptions;

namespace SkillSwap.Domain.Tests.Entities;

/// <summary>История привязок и запросы на снятие (раздел 3 ТЗ, правило 3).</summary>
public class StaffPersonalLinkTests
{
    private static readonly DateTime Now = new(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_link_record_is_active_until_an_admin_unlinks_it_once()
    {
        var link = StaffPersonalLink.Create(Guid.NewGuid(), Guid.NewGuid(), Now);
        var admin = Guid.NewGuid();

        Assert.True(link.IsActive);
        Assert.True(link.Unlink(admin, Now.AddDays(1)));
        Assert.False(link.IsActive);
        Assert.Equal(admin, link.UnlinkedById);
        Assert.False(link.Unlink(Guid.NewGuid(), Now.AddDays(2)));   // повтор не меняет, кто и когда снял
        Assert.Equal(admin, link.UnlinkedById);
        Assert.Equal(Now.AddDays(1), link.UnlinkedAt);
    }

    [Fact]
    public void A_staff_account_cannot_be_linked_to_itself()
    {
        var id = Guid.NewGuid();

        Assert.Equal("link_same_account", Assert.Throws<DomainValidationException>(() => StaffPersonalLink.Create(id, id, Now)).Code);
    }

    [Theory]
    [InlineData("коротко")]
    [InlineData("")]
    [InlineData(null)]
    public void An_unlink_request_needs_a_reason_of_at_least_ten_characters(string? reason)
    {
        Assert.Equal("unlink_reason_too_short", Assert.Throws<DomainValidationException>(() => PersonalUnlinkRequest.Create(Guid.NewGuid(), reason)).Code);
    }

    [Fact]
    public void An_unlink_request_is_decided_once()
    {
        var approved = PersonalUnlinkRequest.Create(Guid.NewGuid(), "Личный аккаунт закрыт, привязка не нужна.");
        var rejected = PersonalUnlinkRequest.Create(Guid.NewGuid(), "Личный аккаунт закрыт, привязка не нужна.");
        var admin = Guid.NewGuid();

        approved.Approve(admin, Now);
        rejected.Reject(admin, Now);

        Assert.Equal(UnlinkRequestStatus.Approved, approved.Status);
        Assert.Equal(UnlinkRequestStatus.Rejected, rejected.Status);
        Assert.Equal(admin, approved.DecidedById);
        Assert.Throws<InvalidStateTransitionException>(() => approved.Reject(admin, Now));
        Assert.Throws<InvalidStateTransitionException>(() => rejected.Approve(admin, Now));
    }
}
