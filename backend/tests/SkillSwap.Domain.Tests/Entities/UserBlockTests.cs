using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using SkillSwap.Domain.Exceptions;

namespace SkillSwap.Domain.Tests.Entities;

/// <summary>Блокировка и разблокировка обычного аккаунта админом.</summary>
public class UserBlockTests
{
    private static User Regular() => User.CreateRegular("a@example.com", "Алишер", new DateOnly(1995, 1, 1), "ru");

    [Fact]
    public void Block_then_unblock_returns_the_account_to_active()
    {
        var user = Regular();

        user.Block();
        Assert.Equal(UserStatus.Blocked, user.Status);
        user.Unblock();

        Assert.Equal(UserStatus.Active, user.Status);
    }

    [Fact]
    public void Only_a_blocked_account_can_be_unblocked()
    {
        var active = Regular();
        var deleted = Regular();
        deleted.Block();
        deleted.Unblock();
        deleted.MarkDeleted(DateTime.UtcNow, keepPersonalData: false);

        Assert.Equal("user_not_blocked", Assert.Throws<DomainValidationException>(() => active.Unblock()).Code);
        Assert.Equal("user_not_blocked", Assert.Throws<DomainValidationException>(() => deleted.Unblock()).Code);
    }

    [Fact]
    public void A_blocked_account_cannot_be_blocked_again_and_staff_cannot_be_blocked()
    {
        var user = Regular();
        user.Block();

        Assert.Equal("user_not_active", Assert.Throws<DomainValidationException>(() => user.Block()).Code);
        Assert.Equal("staff_cannot_be_blocked", Assert.Throws<DomainValidationException>(() => User.CreateStaff("s@example.com", "Staff", UserRole.Moderator).Block()).Code);
    }
}
